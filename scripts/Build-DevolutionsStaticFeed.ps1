[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $WorkspaceDirectory,

    [string] $PingetCliPath,

    [string] $PsignToolPath,

    [string] $PackageName = 'Devolutions.WinGet.Source',

    [string] $Publisher = 'CN=Devolutions Inc., O=Devolutions Inc., L=Lavaltrie, S=Quebec, C=CA',

    [string] $PackageVersion = '1.0.0.0',

    [string[]] $TlsDnsNames = @('winget.devolutions.test', 'localhost'),

    [string[]] $TlsIpAddresses = @('127.0.0.1'),

    [switch] $GenerateTestPki,

    [string] $SigningPfxPath,

    [string] $SigningPasswordEnvironmentVariable,

    [string[]] $TrustedCaPaths = @(),

    [string] $ManifestRepository = 'microsoft/winget-pkgs',

    [string] $SubtreePath = 'manifests/d/Devolutions',

    [string] $PullRequestAuthor = 'devolutionsbot',

    [switch] $SkipOpenPullRequests,

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
    if (-not [string]::IsNullOrWhiteSpace($SparseCheckoutDirectory)) {
        $syncParams['SparseCheckoutDirectory'] = $SparseCheckoutDirectory
    }
    $syncReport = & (Join-Path $PSScriptRoot 'Sync-WinGetManifests.ps1') @syncParams
}
elseif (-not (Test-Path -LiteralPath $manifestsDir -PathType Container)) {
    throw "Manifest directory does not exist and -SkipManifestSync was specified: $manifestsDir"
}

$generatedPki = $null
$ephemeralSignPasswordVar = $null
$ephemeralSignPassword = $null

try {
    if ($GenerateTestPki) {
        New-Item -ItemType Directory -Path $pkiDir -Force | Out-Null
        $rootCer = Join-Path $pkiDir 'root-ca.cer'
        $rootPem = Join-Path $pkiDir 'root-ca.pem'
        $rootPfx = Join-Path $pkiDir 'root-ca.pfx'
        $codeSignPfx = Join-Path $pkiDir 'codesign.pfx'
        $codeSignCer = Join-Path $pkiDir 'codesign.cer'
        $tlsPfx = Join-Path $pkiDir 'tls-server.pfx'
        $tlsPem = Join-Path $pkiDir 'tls-fullchain.pem'
        $tlsKey = Join-Path $pkiDir 'tls-server.key'
        $tlsPasswordFile = Join-Path $pkiDir 'tls-password.txt'

        $caPassword = New-EphemeralSecret
        $ephemeralSignPassword = New-EphemeralSecret
        $tlsPassword = New-EphemeralSecret

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
                "--password=$tlsPassword",
                '--pem-out', $tlsPem,
                '--key-out', $tlsKey
            ))
            foreach ($dns in $TlsDnsNames) {
                $tlsArgs.Add('--dns')
                $tlsArgs.Add($dns)
            }
            foreach ($ip in $TlsIpAddresses) {
                $tlsArgs.Add('--ip')
                $tlsArgs.Add($ip)
            }
            $null = Invoke-ResponseFileTool -ToolPath $psignFullPath -Arguments $tlsArgs.ToArray() -SecretsToRedact @($caPassword, $tlsPassword)

            # Remove the Root CA private key immediately after issuing the leaf certificates.
            Remove-Item -LiteralPath $rootPfx -Force
            [System.IO.File]::WriteAllText($tlsPasswordFile, $tlsPassword, [System.Text.UTF8Encoding]::new($false))
        }
        finally {
            $caPassword = $null
        }

        $ephemeralSignPasswordVar = 'PINGET_STATIC_FEED_SIGN_PW_' + [Guid]::NewGuid().ToString('N')
        [Environment]::SetEnvironmentVariable($ephemeralSignPasswordVar, $ephemeralSignPassword, 'Process')
        $SigningPfxPath = $codeSignPfx
        $SigningPasswordEnvironmentVariable = $ephemeralSignPasswordVar
        $TrustedCaPaths = @($rootCer)
        $generatedPki = [pscustomobject]@{
            RootCaCertPath = $rootCer
            RootCaPemPath = $rootPem
            CodeSigningPfxPath = $codeSignPfx
            CodeSigningCertPath = $codeSignCer
            TlsServerPfxPath = $tlsPfx
            TlsFullChainPemPath = $tlsPem
            TlsPrivateKeyPath = $tlsKey
            TlsPasswordFilePath = $tlsPasswordFile
        }
    }

    if ([string]::IsNullOrWhiteSpace($SigningPfxPath) -or [string]::IsNullOrWhiteSpace($SigningPasswordEnvironmentVariable)) {
        throw 'Provide -GenerateTestPki or both -SigningPfxPath and -SigningPasswordEnvironmentVariable.'
    }

    $buildRaw = & $pingetFullPath --output json repository build --input $manifestsDir --state $stateDir
    if ($LASTEXITCODE -ne 0) { throw "pinget repository build failed." }
    $buildReport = $buildRaw | ConvertFrom-Json

    $verifyStateRaw = & $pingetFullPath --output json repository verify --state $stateDir --build $buildReport.buildId
    if ($LASTEXITCODE -ne 0) { throw "pinget repository verify --state failed." }

    $publishArgs = [System.Collections.Generic.List[string]]::new()
    $publishArgs.AddRange([string[]]@(
        '--output', 'json', 'repository', 'publish',
        '--state', $stateDir,
        '--build', $buildReport.buildId,
        '--destination', $siteDir,
        '--name', $PackageName,
        '--publisher', $Publisher,
        '--version', $PackageVersion,
        '--pfx', ([System.IO.Path]::GetFullPath($SigningPfxPath)),
        '--password-env', $SigningPasswordEnvironmentVariable,
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
        Sync = $syncReport
        Build = $buildReport
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
