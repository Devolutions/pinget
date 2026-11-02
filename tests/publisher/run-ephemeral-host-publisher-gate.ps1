param(
    [Parameter(Mandatory = $true)]
    [string] $RustCliPath,
    [Parameter(Mandatory = $true)]
    [string] $DotnetCliPath,
    [Parameter(Mandatory = $true)]
    [string] $PsignToolPath
)

$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
    -not [System.IO.Path]::IsPathRooted($env:RUNNER_TEMP)) {
    throw 'This gate is restricted to a GitHub-hosted runner with an absolute RUNNER_TEMP directory.'
}
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The signed stock WinGet gate requires a Windows GitHub-hosted runner.'
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The GitHub-hosted Windows runner must permit runner-local LocalMachine Root certificate trust.'
}

foreach ($path in @($RustCliPath, $DotnetCliPath, $PsignToolPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required publisher or psign-tool was not found: $path"
    }
}

$appInstaller = Get-AppxPackage -Name Microsoft.DesktopAppInstaller
if ($null -eq $appInstaller) {
    throw 'The GitHub-hosted Windows runner does not have the stock Microsoft.DesktopAppInstaller package.'
}
$wingetCommand = Get-Command winget.exe -ErrorAction SilentlyContinue
if ($null -eq $wingetCommand) {
    throw 'The GitHub-hosted Windows runner does not expose the stock winget.exe execution alias.'
}
$clientVersionOutput = & $wingetCommand.Source --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($clientVersionOutput -join '').Trim() -notmatch '^v(\d+\.\d+\.\d+)$') {
    throw "Could not identify the stock WinGet client version: $($clientVersionOutput -join ' ')"
}
$clientVersion = $Matches[1]
Write-Output "Stock App Installer package $($appInstaller.Version); WinGet client $clientVersion."

$workRoot = Join-Path $env:RUNNER_TEMP "pinget-stock-gate-$([Guid]::NewGuid().ToString('N'))"
$signerPfxPath = Join-Path $workRoot 'publisher-signing.pfx'
$signerCertificatePath = Join-Path $workRoot 'publisher-signing.cer'
$tlsPfxPath = Join-Path $workRoot 'tls-server.pfx'
$tlsCertificatePath = Join-Path $workRoot 'tls-server.cer'
$passwordVariable = 'PINGET_PUBLISHER_EPHEMERAL_PASSWORD'
$password = $null
$signerThumbprint = $null
$tlsThumbprint = $null
$trustMayHaveBeenAdded = $false
$gateStarted = $false
$gateSucceeded = $false
$workDirectoryCreated = $false
$sourceName = $null
$wingetPath = $wingetCommand.Source
$publisherSubject = 'CN=Pinget Publisher Conformance Test'

function New-RandomPassword {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try {
        return [Convert]::ToBase64String($bytes)
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function Get-RootStoreThumbprints {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return @($store.Certificates | ForEach-Object Thumbprint)
    }
    finally {
        $store.Close()
    }
}

function Test-RunWinGetSourceRegistered {
    $escapedSourceName = [regex]::Escape($sourceName)
    $sourceList = & $wingetPath source list --disable-interactivity 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect WinGet sources: $($sourceList -join "`n")"
    }

    # `winget source list` renders a table with URL/type columns after the
    # source name; matching a name-only line misses registered sources.
    return ($sourceList -join "`n") -match "(?m)^\s*$escapedSourceName\s+\S"
}

function Remove-RunRootCertificates {
    $thumbprints = @($signerThumbprint, $tlsThumbprint) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if ($thumbprints.Count -eq 0) {
        return
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($certificate in @($store.Certificates)) {
            if ($thumbprints -contains $certificate.Thumbprint) {
                $store.Remove($certificate)
                $certificate.Dispose()
            }
        }
    }
    finally {
        $store.Close()
    }
}

function Remove-RunWinGetSource {
    if (-not $gateStarted -or -not (Test-Path -LiteralPath $wingetPath)) {
        return
    }

    if (Test-RunWinGetSourceRegistered) {
        $removeOutput = & $wingetPath source remove --name $sourceName --disable-interactivity 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove temporary WinGet source '$sourceName': $($removeOutput -join "`n")"
        }
    }

    if (Test-RunWinGetSourceRegistered) {
        throw "Temporary WinGet source '$sourceName' remains registered after cleanup."
    }
}

try {
    New-Item -ItemType Directory -Path $workRoot -ErrorAction Stop | Out-Null
    $workDirectoryCreated = $true

    $fixtureDirectory = Join-Path $PSScriptRoot 'fixtures\singleton'
    if (-not (Test-Path -LiteralPath $fixtureDirectory -PathType Container)) {
        throw "Committed singleton stock-client fixture directory is missing: $fixtureDirectory"
    }

    $password = New-RandomPassword
    [Environment]::SetEnvironmentVariable($passwordVariable, $password, 'Process')

    $signerRsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $signerRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $publisherSubject,
            $signerRsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $signerRequest.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $signerEku = [System.Security.Cryptography.OidCollection]::new()
        [void]$signerEku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        $signerRequest.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $signerEku, $false))
        $signerCertificate = $signerRequest.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-5),
            [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $signerThumbprint = $signerCertificate.Thumbprint
            [System.IO.File]::WriteAllBytes(
                $signerPfxPath,
                $signerCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
                    $password))
            [System.IO.File]::WriteAllBytes(
                $signerCertificatePath,
                $signerCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally {
            $signerCertificate.Dispose()
        }
    }
    finally {
        $signerRsa.Dispose()
    }

    $tlsRsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $tlsRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=127.0.0.1',
            $tlsRsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $tlsRequest.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
                $false, $false, 0, $true))
        $tlsRequest.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,
                $true))
        $tlsEku = [System.Security.Cryptography.OidCollection]::new()
        [void]$tlsEku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $tlsRequest.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $tlsEku, $false))
        $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddIpAddress([System.Net.IPAddress]::Parse('127.0.0.1'))
        $tlsRequest.CertificateExtensions.Add($san.Build())
        $tlsCertificate = $tlsRequest.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-5),
            [DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $tlsThumbprint = $tlsCertificate.Thumbprint
            [System.IO.File]::WriteAllBytes(
                $tlsPfxPath,
                $tlsCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
            [System.IO.File]::WriteAllBytes(
                $tlsCertificatePath,
                $tlsCertificate.Export(
                    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
        finally {
            $tlsCertificate.Dispose()
        }
    }
    finally {
        $tlsRsa.Dispose()
    }

    $existingThumbprints = Get-RootStoreThumbprints
    if ($existingThumbprints -contains $signerThumbprint -or
        $existingThumbprints -contains $tlsThumbprint) {
        throw 'A generated disposable certificate unexpectedly already exists in LocalMachine Root; refusing to alter pre-existing trust.'
    }
    $harness = Join-Path $PSScriptRoot 'sandbox-conformance.ps1'
    $publishers = @(
        [pscustomobject]@{ Name = 'Rust'; Cli = $RustCliPath },
        [pscustomobject]@{ Name = 'Dotnet'; Cli = $DotnetCliPath }
    )
    foreach ($publisher in $publishers) {
        $publisherWorkRoot = Join-Path $workRoot $publisher.Name.ToLowerInvariant()
        $inputDirectory = Join-Path $publisherWorkRoot 'input'
        $stateDirectory = Join-Path $publisherWorkRoot 'publisher-state'
        $sourceName = "pinget-ci-$($publisher.Name.ToLowerInvariant())-$([Guid]::NewGuid().ToString('N'))"
        $escapedSourceName = [regex]::Escape($sourceName)
        New-Item -ItemType Directory -Path $publisherWorkRoot -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $fixtureDirectory -Destination $inputDirectory -Recurse
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'serve-feed.ps1') `
            -Destination (Join-Path $publisherWorkRoot 'serve-feed.ps1')
        Copy-Item -LiteralPath $tlsPfxPath -Destination (Join-Path $publisherWorkRoot 'tls-server.pfx')
        Copy-Item -LiteralPath $tlsCertificatePath -Destination (Join-Path $publisherWorkRoot 'tls-server.cer')

        $existingSources = & $wingetPath source list --disable-interactivity 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inspect existing WinGet sources: $($existingSources -join "`n")"
        }
        if (($existingSources -join "`n") -match "(?m)^\s*$escapedSourceName\s+\S") {
            throw "Generated temporary source name unexpectedly collides with existing source '$sourceName'."
        }

        $trustMayHaveBeenAdded = $true
        $gateStarted = $true
        Write-Output "START signed stock WinGet acceptance for $($publisher.Name)."
        & $harness `
            -WorkRoot $publisherWorkRoot `
            -SourceName $sourceName `
            -ExecutionTarget EphemeralHost `
            -AllowEphemeralHostMutations `
            -ExpectedWinGetVersion $appInstaller.Version.ToString() `
            -ExpectedWinGetClientVersion $clientVersion `
            -SignerCertificatePath $signerCertificatePath `
            -PublisherCliPath $publisher.Cli `
            -PublisherStateDirectory $stateDirectory `
            -PublisherInputDirectory $inputDirectory `
            -PfxPath $signerPfxPath `
            -PfxPasswordEnvironmentVariable $passwordVariable `
            -RustReaderCliPath $RustCliPath `
            -DotnetReaderCliPath $DotnetCliPath `
            -PublisherName 'Pinget.Publisher.Conformance' `
            -PublisherIdentity $publisherSubject `
            -InitialPackageVersion '1.0.0.0' `
            -PsignToolPath $PsignToolPath

        Remove-RunWinGetSource
        $gateStarted = $false
        $remainingThumbprints = Get-RootStoreThumbprints
        if ($remainingThumbprints -contains $signerThumbprint -or
            $remainingThumbprints -contains $tlsThumbprint) {
            throw "The $($publisher.Name) gate left a temporary certificate in LocalMachine Root."
        }
        $trustMayHaveBeenAdded = $false
    }

    $gateSucceeded = $true
    Write-Output "PASS signed stock WinGet conformance for Rust and .NET on GitHub runner with WinGet $clientVersion (App Installer $($appInstaller.Version))."
}
finally {
    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    if ($gateStarted) {
        try {
            Remove-RunWinGetSource
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }
    if ($trustMayHaveBeenAdded) {
        try {
            Remove-RunRootCertificates
            $remainingThumbprints = Get-RootStoreThumbprints
            if ($remainingThumbprints -contains $signerThumbprint -or
                $remainingThumbprints -contains $tlsThumbprint) {
                throw 'Disposable certificate cleanup verification found a test certificate still trusted.'
            }
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    [Environment]::SetEnvironmentVariable($passwordVariable, $null, 'Process')
    $password = $null
    if ($workDirectoryCreated -and (Test-Path -LiteralPath $workRoot)) {
        try {
            Remove-Item -LiteralPath $workRoot -Recurse -Force
        }
        catch {
            $cleanupErrors.Add("Could not remove runner-local gate directory '$workRoot': $($_.Exception.Message)")
        }
    }

    if ($cleanupErrors.Count -gt 0) {
        throw "Ephemeral stock WinGet gate cleanup failed: $($cleanupErrors -join '; ')"
    }
    if (-not $gateSucceeded) {
        Write-Output 'Ephemeral stock WinGet gate did not pass; runner-local cleanup completed.'
    }
}
