#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceDirectory,

    [string] $PingetCliPath,

    [string] $PsignToolPath,

    [string] $PackageName = 'Devolutions.WinGet.Source',

    [string] $Publisher = 'CN=Devolutions Inc., O=Devolutions Inc., L=Lavaltrie, S=Quebec, C=CA',

    [string] $PackageVersion,

    [string[]] $TlsDnsNames = @('winget.devolutions.test', 'localhost'),

    [string[]] $TlsIpAddresses = @('127.0.0.1'),

    [switch] $GenerateTestPki,

    [switch] $ForceRegenerateTestPki,

    [string] $TlsPasswordEnvironmentVariable,

    [switch] $EmitTlsPemKey,

    [string] $SigningPfxPath,

    [string] $SigningPasswordEnvironmentVariable,

    [string] $TimestampUrl,

    [string[]] $PsignSignArguments = @(),

    [string[]] $TrustedCaPaths = @(),

    [string] $ManifestRepository = 'microsoft/winget-pkgs',

    [string] $SubtreePath = 'manifests/d/Devolutions',

    [string] $PullRequestAuthor = 'devolutionsbot',

    [switch] $SkipOpenPullRequests,

    [switch] $SkipConflictingPullRequests,

    [switch] $SkipManifestSync,

    [string] $SparseCheckoutDirectory
)

$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $userTemp = Join-Path $env:LOCALAPPDATA 'Temp'
    New-Item -ItemType Directory -Path $userTemp -Force | Out-Null
    $env:TEMP = $userTemp
    $env:TMP = $userTemp
}

function New-EphemeralSecret {
    $bytes = [byte[]]::new(24)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    try {
        return ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', 'X') + '!9aA'
    }
    finally {
        [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    }
}

function Invoke-ResponseFileTool {
    param(
        [string] $ToolPath,
        [string[]] $Arguments,
        [string[]] $SecretsToRedact = @()
    )
    $tempBase = if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        Join-Path $env:LOCALAPPDATA 'Temp'
    }
    else {
        [System.IO.Path]::GetTempPath()
    }
    $rspDir = Join-Path $tempBase ("pinget-build-rsp-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $rspDir -Force | Out-Null
    $rspPath = Join-Path $rspDir 'args.rsp'
    try {
        for ($argIndex = 0; $argIndex -lt $Arguments.Count; $argIndex++) {
            if ($Arguments[$argIndex].IndexOfAny([char[]]@("`r", "`n")) -ge 0) {
                throw "psign-tool argument at index $argIndex must not contain CR or LF line breaks."
            }
        }
        $lines = foreach ($arg in $Arguments) {
            '"' + $arg.Replace('"', '""') + '"'
        }
        [System.IO.File]::WriteAllText($rspPath, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
        $output = & $ToolPath "@$rspPath" 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
        foreach ($secret in $SecretsToRedact) {
            if (-not [string]::IsNullOrEmpty($secret)) {
                $text = $text.Replace($secret, '<redacted>')
            }
        }
        if ($exitCode -ne 0) {
            throw "psign-tool failed (exit code $exitCode): $text"
        }
        return $text
    }
    finally {
        Remove-Item -LiteralPath $rspDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PingetCliPath)) {
    $defaultRustCli = Join-Path $repoRoot 'rust\target\release\pinget.exe'
    if (Test-Path -LiteralPath $defaultRustCli -PathType Leaf) {
        $PingetCliPath = $defaultRustCli
    }
    else {
        $cmd = Get-Command pinget.exe -ErrorAction SilentlyContinue
        if ($null -eq $cmd) {
            throw 'pinget.exe was not found. Build rust\target\release\pinget.exe or pass -PingetCliPath.'
        }
        $PingetCliPath = $cmd.Source
    }
}
$pingetFullPath = [System.IO.Path]::GetFullPath($PingetCliPath)
if (-not (Test-Path -LiteralPath $pingetFullPath -PathType Leaf)) {
    throw "Pinget CLI does not exist: $pingetFullPath"
}

if ([string]::IsNullOrWhiteSpace($PsignToolPath)) {
    $cmd = Get-Command psign-tool.exe -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { $cmd = Get-Command psign-tool.cmd -ErrorAction SilentlyContinue }
    if ($null -eq $cmd) {
        throw 'psign-tool was not found. Pass -PsignToolPath or install Devolutions.Psign.Tool.'
    }
    $PsignToolPath = $cmd.Source
}
$psignFullPath = [System.IO.Path]::GetFullPath($PsignToolPath)
if (-not (Test-Path -LiteralPath $psignFullPath -PathType Leaf)) {
    throw "psign-tool does not exist: $psignFullPath"
}

$workFullPath = [System.IO.Path]::GetFullPath($WorkspaceDirectory)
New-Item -ItemType Directory -Path $workFullPath -Force | Out-Null

$manifestsDir = Join-Path $workFullPath 'manifests'
$stateDir = Join-Path $workFullPath 'publisher-state'
$siteDir = Join-Path $workFullPath 'site'
$pkiDir = Join-Path $workFullPath 'pki'

$syncReport = $null
if (-not $SkipManifestSync) {
    $syncParams = @{
        OutputDirectory = $manifestsDir
        Repository = $ManifestRepository
        SubtreePath = $SubtreePath
        PullRequestAuthor = $PullRequestAuthor
    }
    if ($SkipOpenPullRequests) {
        $syncParams['SkipOpenPullRequests'] = $true
    }
    if ($SkipConflictingPullRequests) {
        $syncParams['SkipConflictingPullRequests'] = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($SparseCheckoutDirectory)) {
        $syncParams['SparseCheckoutDirectory'] = $SparseCheckoutDirectory
    }
    $syncReport = & (Join-Path $PSScriptRoot 'Sync-WinGetManifests.ps1') @syncParams
}
elseif (-not (Test-Path -LiteralPath $manifestsDir -PathType Container)) {
    throw "Manifest directory does not exist and -SkipManifestSync was specified: $manifestsDir"
}

function Step-QuadVersion {
    param([string] $Version)
    $parts = $Version.Trim().Split('.')
    if ($parts.Length -ne 4) {
        throw "Existing published packageVersion '$Version' is not a 4-part AppX version."
    }
    $nums = [int[]]::new(4)
    for ($i = 0; $i -lt 4; $i++) {
        $parsed = 0
        if (-not [int]::TryParse($parts[$i], [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or
            $parsed -lt 0 -or $parsed -gt 65535) {
            throw "Existing published packageVersion '$Version' has an out-of-range AppX component '$($parts[$i])'."
        }
        $nums[$i] = $parsed
    }
    for ($i = 3; $i -ge 0; $i--) {
        if ($nums[$i] -lt 65535) {
            $nums[$i]++
            for ($j = $i + 1; $j -lt 4; $j++) {
                $nums[$j] = 0
            }
            return ($nums -join '.')
        }
    }
    throw "Cannot auto-increment exhausted AppX packageVersion '$Version'."
}

function Resolve-EffectivePackageVersion {
    param(
        [string] $ExplicitVersion,
        [string] $DestinationDir,
        [string] $BuildId,
        [bool] $RotatedSigningAuthority
    )
    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        return $ExplicitVersion.Trim()
    }
    $markerPath = Join-Path $DestinationDir '.pinget-publisher.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return '1.0.0.0'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    $priorVersion = [string]$marker.packageVersion
    if ([string]::IsNullOrWhiteSpace($priorVersion)) {
        return '1.0.0.0'
    }
    $hasPending = $null -ne $marker.PSObject.Properties['pendingCommit'] -and $null -ne $marker.pendingCommit
    if (-not $RotatedSigningAuthority -and -not $hasPending -and [string]$marker.buildId -eq $BuildId) {
        return $priorVersion
    }
    return Step-QuadVersion -Version $priorVersion
}

$generatedPki = $null
$ephemeralSignPasswordVar = $null
$ephemeralSignPassword = $null
$mintedNewSigningAuthority = $false

try {
    if ($GenerateTestPki) {
        New-Item -ItemType Directory -Path $pkiDir -Force | Out-Null
        $rootCer = Join-Path $pkiDir 'root-ca.cer'
        $rootPem = Join-Path $pkiDir 'root-ca.pem'
        $rootPfx = Join-Path $pkiDir 'root-ca.pfx'
        $codeSignPfx = Join-Path $pkiDir 'codesign.pfx'
        $codeSignCer = Join-Path $pkiDir 'codesign.cer'
        $tlsPfx = Join-Path $pkiDir 'tls-server.pfx'
        $tlsPem = if ($EmitTlsPemKey) { Join-Path $pkiDir 'tls-fullchain.pem' } else { $null }
        $tlsKey = if ($EmitTlsPemKey) { Join-Path $pkiDir 'tls-server.key' } else { $null }

        $canReuseExistingTestPki = (-not $ForceRegenerateTestPki) -and
            (-not [string]::IsNullOrWhiteSpace($SigningPasswordEnvironmentVariable)) -and
            (Test-Path -LiteralPath $rootCer -PathType Leaf) -and
            (Test-Path -LiteralPath $codeSignPfx -PathType Leaf) -and
            (Test-Path -LiteralPath $tlsPfx -PathType Leaf)

        if (-not $canReuseExistingTestPki) {
            $mintedNewSigningAuthority = $true
            $caPassword = New-EphemeralSecret
            if (-not [string]::IsNullOrWhiteSpace($SigningPasswordEnvironmentVariable)) {
                $existingSignPw = [Environment]::GetEnvironmentVariable($SigningPasswordEnvironmentVariable)
                if ($null -eq $existingSignPw) {
                    throw "Signing password environment variable '$SigningPasswordEnvironmentVariable' is not set."
                }
                $ephemeralSignPassword = $existingSignPw
            }
            else {
                $ephemeralSignPassword = New-EphemeralSecret
                $ephemeralSignPasswordVar = 'PINGET_STATIC_FEED_SIGN_PW_' + [Guid]::NewGuid().ToString('N')
                [Environment]::SetEnvironmentVariable($ephemeralSignPasswordVar, $ephemeralSignPassword, 'Process')
                $SigningPasswordEnvironmentVariable = $ephemeralSignPasswordVar
            }

            $tlsPassword = [string]::Empty
            if (-not [string]::IsNullOrWhiteSpace($TlsPasswordEnvironmentVariable)) {
                $envTlsPw = [Environment]::GetEnvironmentVariable($TlsPasswordEnvironmentVariable)
                if ($null -eq $envTlsPw) {
                    throw "TLS password environment variable '$TlsPasswordEnvironmentVariable' is not set."
                }
                $tlsPassword = $envTlsPw
            }

            try {
                $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments @(
                    'portable', 'generate-root-ca',
                    '--subject', 'CN=Devolutions Static Feed Test Root CA, O=Devolutions Inc., C=CA',
                    '--validity-days', '3650',
                    '--cert-out', $rootCer,
                    '--pem-out', $rootPem,
                    '--pfx-out', $rootPfx,
                    "--password=$caPassword"
                ) -SecretsToRedact @($caPassword)

                $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments @(
                    'portable', 'issue-code-signing-cert',
                    '--ca-pfx', $rootPfx,
                    "--ca-password=$caPassword",
                    '--subject', $Publisher,
                    '--validity-days', '365',
                    '--pfx-out', $codeSignPfx,
                    "--password=$ephemeralSignPassword",
                    '--cert-out', $codeSignCer
                ) -SecretsToRedact @($caPassword, $ephemeralSignPassword)

                $tlsArgs = [System.Collections.Generic.List[string]]::new()
                $tlsArgs.AddRange([string[]]@(
                    'portable', 'issue-tls-server-cert',
                    '--ca-pfx', $rootPfx,
                    "--ca-password=$caPassword",
                    '--validity-days', '365',
                    '--pfx-out', $tlsPfx,
                    "--password=$tlsPassword"
                ))
                if ($EmitTlsPemKey) {
                    $tlsArgs.Add('--pem-out')
                    $tlsArgs.Add($tlsPem)
                    $tlsArgs.Add('--key-out')
                    $tlsArgs.Add($tlsKey)
                }
                foreach ($dns in $TlsDnsNames) {
                    $tlsArgs.Add('--dns')
                    $tlsArgs.Add($dns)
                }
                foreach ($ip in $TlsIpAddresses) {
                    $tlsArgs.Add('--ip')
                    $tlsArgs.Add($ip)
                }
                $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments $tlsArgs.ToArray() -SecretsToRedact @($caPassword, $tlsPassword)
            }
            finally {
                # Always remove the Root CA private key even if leaf issuance fails.
                if (Test-Path -LiteralPath $rootPfx) {
                    Remove-Item -LiteralPath $rootPfx -Force -ErrorAction SilentlyContinue
                }
                $caPassword = $null
                $tlsPassword = $null
            }
        }

        $SigningPfxPath = $codeSignPfx
        $TrustedCaPaths = @($rootCer)
        $generatedPki = [pscustomobject]@{
            RootCaCertPath = $rootCer
            RootCaPemPath = $rootPem
            CodeSigningPfxPath = $codeSignPfx
            CodeSigningCertPath = $codeSignCer
            TlsServerPfxPath = $tlsPfx
            TlsFullChainPemPath = $tlsPem
            TlsPrivateKeyPath = $tlsKey
            ReusedExistingPki = $canReuseExistingTestPki
        }
    }

    if ($PsignSignArguments.Count -gt 0 -and (
            $GenerateTestPki -or
            -not [string]::IsNullOrWhiteSpace($SigningPfxPath) -or
            -not [string]::IsNullOrWhiteSpace($SigningPasswordEnvironmentVariable) -or
            -not [string]::IsNullOrWhiteSpace($TimestampUrl)
        )) {
        throw 'Do not combine -PsignSignArguments with -GenerateTestPki, -SigningPfxPath, -SigningPasswordEnvironmentVariable, or -TimestampUrl.'
    }
    if ($PsignSignArguments.Count -eq 0 -and (
            [string]::IsNullOrWhiteSpace($SigningPfxPath) -or
            [string]::IsNullOrWhiteSpace($SigningPasswordEnvironmentVariable)
        )) {
        throw 'Provide -GenerateTestPki, -PsignSignArguments, or both -SigningPfxPath and -SigningPasswordEnvironmentVariable.'
    }
    $parsedTimestampUri = $null
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl) -and (
            $TimestampUrl -match '\s' -or
            -not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$parsedTimestampUri) -or
            ($parsedTimestampUri.Scheme -ne [Uri]::UriSchemeHttp -and $parsedTimestampUri.Scheme -ne [Uri]::UriSchemeHttps)
        )) {
        throw '-TimestampUrl must be an absolute HTTP or HTTPS URL without whitespace.'
    }

    $buildRaw = & $pingetFullPath --output json repository build --input $manifestsDir --state $stateDir
    if ($LASTEXITCODE -ne 0) { throw "pinget repository build failed." }
    $buildReport = $buildRaw | ConvertFrom-Json

    $verifyStateRaw = & $pingetFullPath --output json repository verify --state $stateDir --build $buildReport.buildId
    if ($LASTEXITCODE -ne 0) { throw "pinget repository verify --state failed." }

    $effectivePackageVersion = Resolve-EffectivePackageVersion `
        -ExplicitVersion $PackageVersion `
        -DestinationDir $siteDir `
        -BuildId ([string]$buildReport.buildId) `
        -RotatedSigningAuthority $mintedNewSigningAuthority

    $stagedDir = Join-Path $workFullPath 'staged'
    New-Item -ItemType Directory -Path $stagedDir -Force | Out-Null
    $stagedMsix = Join-Path $stagedDir 'source2.msix'

    $packageRaw = & $pingetFullPath --output json repository package `
        --state $stateDir `
        --build $buildReport.buildId `
        --name $PackageName `
        --publisher $Publisher `
        --version $effectivePackageVersion `
        --msix $stagedMsix `
        --destination $siteDir
    if ($LASTEXITCODE -ne 0) { throw "pinget repository package failed." }
    $packageReport = $packageRaw | ConvertFrom-Json

    if ($PsignSignArguments.Count -gt 0) {
        $customSignArgs = [string[]]($PsignSignArguments + @($stagedMsix))
        $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments $customSignArgs
    }
    else {
        $pfxFullPath = [System.IO.Path]::GetFullPath($SigningPfxPath)
        if (-not (Test-Path -LiteralPath $pfxFullPath -PathType Leaf)) {
            throw "Signing PFX does not exist: $pfxFullPath"
        }
        $signPassword = [Environment]::GetEnvironmentVariable($SigningPasswordEnvironmentVariable)
        if ($null -eq $signPassword) {
            throw "Signing password environment variable '$SigningPasswordEnvironmentVariable' is not set."
        }
        $signArgs = [System.Collections.Generic.List[string]]::new()
        $signArgs.AddRange([string[]]@(
            '--mode', 'portable', 'sign',
            '--pfx', $pfxFullPath,
            "--password=$signPassword",
            '--digest', 'sha256'
        ))
        if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
            $signArgs.Add('--timestamp-url')
            $signArgs.Add($TimestampUrl)
            $signArgs.Add('--timestamp-digest')
            $signArgs.Add('sha256')
        }
        $signArgs.Add($stagedMsix)
        try {
            $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments $signArgs.ToArray() -SecretsToRedact @($signPassword)
        }
        finally {
            $signPassword = $null
        }
    }

    $publishArgs = [System.Collections.Generic.List[string]]::new()
    $publishArgs.AddRange([string[]]@(
        '--output', 'json', 'repository', 'publish',
        '--state', $stateDir,
        '--build', $buildReport.buildId,
        '--destination', $siteDir,
        '--msix', $stagedMsix,
        '--psign-tool', $psignFullPath
    ))
    foreach ($ca in $TrustedCaPaths) {
        $publishArgs.Add('--trusted-ca')
        $publishArgs.Add([System.IO.Path]::GetFullPath($ca))
    }

    $publishRaw = & $pingetFullPath @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "pinget repository publish failed." }
    $publishReport = $publishRaw | ConvertFrom-Json

    $verifyDestArgs = [System.Collections.Generic.List[string]]::new()
    $verifyDestArgs.AddRange([string[]]@(
        '--output', 'json', 'repository', 'verify',
        '--destination', $siteDir,
        '--psign-tool', $psignFullPath
    ))
    foreach ($ca in $TrustedCaPaths) {
        $verifyDestArgs.Add('--trusted-ca')
        $verifyDestArgs.Add([System.IO.Path]::GetFullPath($ca))
    }
    $verifyDestRaw = & $pingetFullPath @verifyDestArgs
    if ($LASTEXITCODE -ne 0) { throw "pinget repository verify --destination failed." }
    $verifyDestReport = $verifyDestRaw | ConvertFrom-Json

    [pscustomobject]@{
        SiteDirectory = $siteDir
        StateDirectory = $stateDir
        ManifestsDirectory = $manifestsDir
        PackageVersion = $effectivePackageVersion
        Sync = $syncReport
        Build = $buildReport
        Package = $packageReport
        Publish = $publishReport
        Verify = $verifyDestReport
        TestPki = $generatedPki
    }
}
finally {
    if ($null -ne $ephemeralSignPasswordVar) {
        [Environment]::SetEnvironmentVariable($ephemeralSignPasswordVar, $null, 'Process')
    }
    $ephemeralSignPassword = $null
}
