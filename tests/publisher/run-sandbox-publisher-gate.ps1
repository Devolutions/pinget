param(
    [Parameter(Mandatory = $true)]
    [string] $StageRoot,
    [Parameter(Mandatory = $true)]
    [string] $ResultsRoot
)

$ErrorActionPreference = 'Stop'
$StageRoot = [System.IO.Path]::GetFullPath($StageRoot)
$ResultsRoot = [System.IO.Path]::GetFullPath($ResultsRoot)
$bootstrapRoot = Join-Path $ResultsRoot 'bootstrap'
$transcriptPath = Join-Path $ResultsRoot 'publisher-gate-transcript.txt'
$summaryPath = Join-Path $ResultsRoot 'publisher-gate-summary.json'
$signerCertificatePath = Join-Path $StageRoot 'publisher-contract.cer'
$pfxPath = Join-Path $StageRoot 'publisher-contract.pfx'
$passwordPath = Join-Path $StageRoot 'publisher-pfx-password.txt'
$testRootThumbprints = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)
$runSummary = [System.Collections.Generic.List[object]]::new()
$exitCode = 0

if (-not (Test-Path -LiteralPath $ResultsRoot)) {
    throw "Writable Sandbox results folder is missing: $ResultsRoot"
}
if (-not (Test-Path -LiteralPath $signerCertificatePath) -or
    -not (Test-Path -LiteralPath $pfxPath) -or
    -not (Test-Path -LiteralPath $passwordPath)) {
    throw 'The read-only staged signer certificate, PFX, and password file are required.'
}

$signer = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $signerCertificatePath)
$testRootThumbprints.Add($signer.Thumbprint) | Out-Null
$signer.Dispose()

try {
    Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null
    $env:PINGET_PUBLISHER_PFX_PASSWORD = [System.IO.File]::ReadAllText($passwordPath)

    Copy-Item -LiteralPath (Join-Path $StageRoot 'bootstrap') `
        -Destination $bootstrapRoot -Recurse
    Copy-Item -LiteralPath $signerCertificatePath `
        -Destination (Join-Path $ResultsRoot 'publisher-contract.cer')
    & (Join-Path $StageRoot 'tests\bootstrap-winget-sandbox.ps1') `
        -WorkRoot $ResultsRoot

    $harness = Join-Path $StageRoot 'tests\sandbox-conformance.ps1'
    $feedServer = Join-Path $StageRoot 'tests\serve-feed.ps1'
    $psignTool = Join-Path $StageRoot 'tools\psign-tool.exe'
    $publishers = @(
        [pscustomobject]@{
            Name = 'Rust'
            Cli = Join-Path $StageRoot 'tools\pinget-rust.exe'
            Source = 'pinget-publisher-rust-conformance'
        },
        [pscustomobject]@{
            Name = 'Dotnet'
            Cli = Join-Path $StageRoot 'tools\dotnet\pinget.exe'
            Source = 'pinget-publisher-dotnet-conformance'
        }
    )

    foreach ($publisher in $publishers) {
        $workRoot = Join-Path $ResultsRoot "work-$($publisher.Name)"
        New-Item -ItemType Directory -Path $workRoot | Out-Null
        $publisherInput = Join-Path $workRoot 'input'
        New-Item -ItemType Directory -Path $publisherInput | Out-Null
        Copy-Item -Path (Join-Path $StageRoot 'input\*') `
            -Destination $publisherInput -Recurse
        Copy-Item -LiteralPath $feedServer -Destination (Join-Path $workRoot 'serve-feed.ps1')
        Copy-Item -LiteralPath (Join-Path $ResultsRoot 'tls-server.pfx') `
            -Destination (Join-Path $workRoot 'tls-server.pfx')
        Copy-Item -LiteralPath (Join-Path $ResultsRoot 'tls-server.cer') `
            -Destination (Join-Path $workRoot 'tls-server.cer')

        Write-Output "START signed stock WinGet acceptance: $($publisher.Name)"
        & $harness `
            -WorkRoot $workRoot `
            -SourceName $publisher.Source `
            -ExecutionTarget Sandbox `
            -ExpectedWinGetVersion '1.29.380.0' `
            -SignerCertificatePath $signerCertificatePath `
            -PublisherCliPath $publisher.Cli `
            -PublisherStateDirectory (Join-Path $workRoot 'publisher-state') `
            -PublisherInputDirectory $publisherInput `
            -PfxPath $pfxPath `
            -PfxPasswordEnvironmentVariable 'PINGET_PUBLISHER_PFX_PASSWORD' `
            -RustReaderCliPath (Join-Path $StageRoot 'tools\pinget-rust.exe') `
            -DotnetReaderCliPath (Join-Path $StageRoot 'tools\dotnet\pinget.exe') `
            -PublisherName 'Pinget.Publisher.Conformance' `
            -PublisherIdentity 'CN=Pinget Publisher Conformance Test' `
            -InitialPackageVersion '1.0.0.0' `
            -PsignToolPath $psignTool

        $runSummary.Add([pscustomobject]@{
            publisher = $publisher.Name
            result = 'passed'
            sourceName = $publisher.Source
            multiblockVersion = '1.0.499'
        })
    }
}
catch {
    $exitCode = 1
    Write-Warning $_
    $runSummary.Add([pscustomobject]@{
        publisher = 'gate'
        result = 'failed'
        error = $_.Exception.ToString()
    })
}
finally {
    try {
        $tlsCertificatePath = Join-Path $ResultsRoot 'tls-server.cer'
        if (Test-Path -LiteralPath $tlsCertificatePath) {
            $tlsCertificate =
                [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $tlsCertificatePath)
            $testRootThumbprints.Add($tlsCertificate.Thumbprint) | Out-Null
            $tlsCertificate.Dispose()
        }

        foreach ($location in @(
                [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser,
                [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
            )) {
            foreach ($storeName in @(
                    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
                    [System.Security.Cryptography.X509Certificates.StoreName]::Root
                )) {
                $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
                    $storeName, $location)
                try {
                    $store.Open(
                        [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    foreach ($certificate in @($store.Certificates)) {
                        if ($testRootThumbprints.Contains($certificate.Thumbprint)) {
                            $store.Remove($certificate)
                            $certificate.Dispose()
                        }
                    }
                }
                finally {
                    $store.Close()
                }
            }
        }
    }
    catch {
        $exitCode = 1
        $runSummary.Add([pscustomobject]@{
            publisher = 'cleanup'
            result = 'failed'
            error = $_.Exception.ToString()
        })
    }

    if ($env:PINGET_PUBLISHER_PFX_PASSWORD) {
        Remove-Item Env:\PINGET_PUBLISHER_PFX_PASSWORD -ErrorAction SilentlyContinue
    }
    try {
        Stop-Transcript | Out-Null
    }
    catch {
    }
    [System.IO.File]::WriteAllText(
        $summaryPath,
        ($runSummary | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
}

if ($exitCode -ne 0) {
    exit $exitCode
}
