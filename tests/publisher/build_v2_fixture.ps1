[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Output,
    [Parameter(Mandatory = $true)][string] $MakeAppx,
    [string] $Publisher = 'CN=Pinget Publisher Contract Test',
    [string] $InstallerUrl = 'https://127.0.0.1:8765/HarmlessPayload.txt',
    [string] $SqliteLibraryPath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publisher-validation.ps1')

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'The WinGet v2 fixture requires the native Windows Compression API.'
}
if ([string]::IsNullOrWhiteSpace($SqliteLibraryPath)) {
    $runtime = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { 'win-x64' }
        'Arm64' { 'win-arm64' }
        'X86' { 'win-x86' }
        default { throw "Unsupported architecture for native SQLite inspection: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)" }
    }
    $SqliteLibraryPath = Join-Path $PSScriptRoot "..\..\dotnet\src\Devolutions.Pinget.Cli\bin\Release\net10.0\runtimes\$runtime\native\e_sqlite3.dll"
}
Initialize-PublisherValidation -SqliteLibraryPath $SqliteLibraryPath

$outputPath = [System.IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputPath) {
    throw "Refusing to overwrite fixture output directory: $outputPath"
}
if (-not (Test-Path -LiteralPath $MakeAppx -PathType Leaf)) {
    throw "MakeAppx does not exist: $MakeAppx"
}
$fixtureRoot = Join-Path $PSScriptRoot 'fixtures'
$feedPath = Join-Path $outputPath 'feed'
$stagePath = Join-Path $outputPath 'package-staging'
$manifestRelative = 'manifests/Pinget.Contract.Singleton.yaml'
$sidecarPrefix = 'packages/Pinget.Contract.Singleton'
$payload = [System.Text.Encoding]::UTF8.GetBytes("Harmless Pinget contract fixture. Download only; never execute.`n")
$payloadHash = Get-Sha256Hex $payload
$installerUrl = $InstallerUrl.Replace("'", "''")
if ($installerUrl.Contains("`n") -or $installerUrl.Contains("`r")) {
    throw 'Installer URL must be a single line.'
}

New-Item -ItemType Directory -Path (Join-Path $feedPath 'manifests') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $feedPath 'packages') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagePath 'Public') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagePath 'Assets') -Force | Out-Null

$payloadPath = Join-Path $feedPath 'HarmlessPayload.txt'
[System.IO.File]::WriteAllBytes($payloadPath, $payload)
$manifestTemplate = Get-Content -LiteralPath (Join-Path $fixtureRoot 'singleton.yaml.in') -Raw
$manifestText = $manifestTemplate.Replace('{{INSTALLER_URL}}', $installerUrl).Replace('{{INSTALLER_SHA256}}', $payloadHash)
$manifestText = $manifestText -replace "`r`n|`r", "`n"
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($manifestText)
[System.IO.File]::WriteAllBytes((Join-Path $feedPath $manifestRelative.Replace('/', '\')), $manifestBytes)

$manifestHash = Get-Sha256Hex $manifestBytes
$versionDataText = "sV: `"1.0`"`nvD:`n  - v: `"1.0.0`"`n    rP: $manifestRelative`n    s256H: $manifestHash`n"
$versionData = [System.Text.UTF8Encoding]::new($false).GetBytes($versionDataText)
$compressedSidecar = [PingetPublisher.Validation.NativeMsZip]::Compress($versionData)
$sidecarHash = Get-Sha256Hex $compressedSidecar
$sidecarRelative = "$sidecarPrefix/$($sidecarHash.Substring(0, 8))/versionData.mszyml"
$sidecarPath = Join-Path $feedPath $sidecarRelative.Replace('/', '\')
New-Item -ItemType Directory -Path (Split-Path -Parent $sidecarPath) -Force | Out-Null
[System.IO.File]::WriteAllBytes($sidecarPath, $compressedSidecar)
[System.IO.File]::WriteAllBytes((Join-Path $outputPath 'versionData.yml'), $versionData)

$databasePath = Join-Path $stagePath 'Public\index.db'
$schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'index-schema.sql') -Raw
$databaseIdentifier = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$sidecarHashSql = $sidecarHash
$sql = @"
$schema
INSERT INTO metadata(name, value) VALUES
    ('majorVersion', '2'),
    ('minorVersion', '0'),
    ('databaseIdentifier', '$databaseIdentifier'),
    ('lastwritetime', '1793380000');
INSERT INTO packages(rowid, id, name, moniker, latest_version, arp_min_version, arp_max_version, hash)
VALUES(1, 'Pinget.Contract.Singleton', 'Pinget Contract Fixture', 'pinget-contract', '1.0.0', NULL, NULL, X'$sidecarHashSql');
INSERT INTO tags2(rowid, tag) VALUES(1, 'publisher-contract');
INSERT INTO tags2_map(tag, package) VALUES(1, 1);
"@
[PingetPublisher.Validation.NativeSqlite]::Execute($databasePath, $sql)

$appxTemplate = Get-Content -LiteralPath (Join-Path $fixtureRoot 'AppxManifest.xml.in') -Raw
$escapedPublisher = [System.Security.SecurityElement]::Escape($Publisher)
$appxManifest = $appxTemplate.Replace('{{PUBLISHER}}', $escapedPublisher) -replace "`r`n|`r", "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $stagePath 'AppxManifest.xml'),
    $appxManifest,
    [System.Text.UTF8Encoding]::new($false)
)
$logo = [Convert]::FromHexString(
    '89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000b49444154789c636000020000050001a5f645400000000049454e44ae426082'
)
foreach ($name in @('StoreLogo.png', 'Logo150.png', 'Logo44.png')) {
    [System.IO.File]::WriteAllBytes((Join-Path $stagePath "Assets\$name"), $logo)
}

$packagePath = Join-Path $feedPath 'source2.msix'
$packOutput = & $MakeAppx pack /d $stagePath /p $packagePath /o 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "MakeAppx failed to package the v2 fixture (exit $LASTEXITCODE): $($packOutput -join "`n")"
}

$metadataText = @"
Package=source2.msix
Index=Public/index.db
Manifest=$manifestRelative
Sidecar=$sidecarRelative
SidecarSHA256=$sidecarHash
InstallerURL=$InstallerUrl
InstallerSHA256=$payloadHash
Publisher=$Publisher
"@
[System.IO.File]::WriteAllText(
    (Join-Path $outputPath 'fixture-metadata.txt'),
    $metadataText,
    [System.Text.UTF8Encoding]::new($false)
)

# Independent fixture self-checks: SQLite schema/index, exact native MSZIP bytes,
# and the packaged MSIX/index/manifest/resource contract.
$integrity = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, 'PRAGMA integrity_check')
if ($integrity.Count -ne 1 -or $integrity[0].integrity_check -ne 'ok') {
    throw "Fixture SQLite integrity_check failed: $($integrity | ConvertTo-Json -Compress)"
}
$expectedTables = @('commands2', 'commands2_map', 'metadata', 'norm_names2', 'norm_publishers2', 'packages', 'pfns2', 'productcodes2', 'tags2', 'tags2_map', 'upgradecodes2')
Assert-Equal (Get-SqliteTableNames $databasePath) $expectedTables 'Fixture WinGet v2 table set differs.'
$metadataRows = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, 'SELECT name, value FROM metadata')
$metadataMap = @{}
foreach ($row in $metadataRows) { $metadataMap[[string]$row.name] = [string]$row.value }
if ($metadataMap.majorVersion -ne '2' -or $metadataMap.minorVersion -ne '0' -or
    $metadataMap.databaseIdentifier -notmatch '^\{[0-9A-F-]{36}\}$' -or
    $metadataMap.lastwritetime -notmatch '^\d+$') {
    throw "Fixture SQLite metadata is invalid: $($metadataMap | ConvertTo-Json -Compress)"
}
$package = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, "SELECT id, name, latest_version, hash FROM packages WHERE rowid=1")
if ($package.Count -ne 1 -or $package[0].id -ne 'Pinget.Contract.Singleton' -or
    $package[0].name -ne 'Pinget Contract Fixture' -or $package[0].latest_version -ne '1.0.0' -or
    $package[0].hash.Length -ne 32) {
    throw 'Fixture SQLite package row or sidecar digest is invalid.'
}
$tagMapping = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, 'SELECT tag, package FROM tags2_map')
if ($tagMapping.Count -ne 1 -or $tagMapping[0].tag -ne 1 -or $tagMapping[0].package -ne 1) {
    throw 'Fixture SQLite tag mapping is invalid.'
}
$decodedVersionData = [PingetPublisher.Validation.NativeMsZip]::Decompress([System.IO.File]::ReadAllBytes($sidecarPath))
if (-not (Test-ByteArrayEqual $versionData $decodedVersionData) -or
    (Get-Sha256Hex ([System.IO.File]::ReadAllBytes($sidecarPath))) -ne $sidecarHash -or
    -not (Test-ByteArrayEqual $package[0].hash ([Convert]::FromHexString($sidecarHash)))) {
    throw 'Fixture sidecar bytes, MSZIP round trip, or index hash do not match.'
}
if ($sidecarRelative -ne "packages/Pinget.Contract.Singleton/$($sidecarHash.Substring(0, 8).ToLowerInvariant())/versionData.mszyml") {
    throw 'Fixture sidecar path does not use the lowercase digest prefix.'
}
Write-Output 'PASS Fixture_SqliteV2SchemaAndPackageMetadata'
Write-Output 'PASS Fixture_NativeMsZipAndSidecarHashParity'

$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    foreach ($requiredEntry in @('Public/index.db', 'AppxManifest.xml')) {
        if ($entryNames -notcontains $requiredEntry) { throw "Fixture MSIX is missing $requiredEntry." }
    }
    $indexEntry = $archive.GetEntry('Public/index.db')
    $indexStream = [System.IO.MemoryStream]::new()
    $entryStream = $indexEntry.Open()
    try { $entryStream.CopyTo($indexStream) } finally { $entryStream.Dispose() }
    if (-not (Test-ByteArrayEqual $indexStream.ToArray() ([System.IO.File]::ReadAllBytes($databasePath)))) {
        throw 'Packaged Public/index.db differs from the independently inspected staged database.'
    }
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$packageXml = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $namespace = [System.Xml.XmlNamespaceManager]::new($packageXml.NameTable)
    $namespace.AddNamespace('uap3', 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $extension = $packageXml.SelectSingleNode('//uap3:AppExtension', $namespace)
    if ($null -eq $extension -or $extension.GetAttribute('Name') -ne 'com.microsoft.winget.source' -or
        $extension.GetAttribute('PublicFolder') -ne 'Public') {
        throw 'Packaged MSIX does not declare the WinGet source extension and Public folder.'
    }
    if ($entryNames -contains $manifestRelative -or $entryNames -contains $sidecarRelative) {
        throw 'Hosted manifest or sidecar was incorrectly embedded in the MSIX.'
    }
    foreach ($externalArtifact in @($manifestRelative, $sidecarRelative)) {
        if (-not (Test-Path -LiteralPath (Join-Path $feedPath $externalArtifact.Replace('/', '\')) -PathType Leaf)) {
            throw "Fixture hosted artifact is missing: $externalArtifact"
        }
    }
    if ((Get-FileSha256Hex $payloadPath) -ne $payloadHash) {
        throw 'Fixture harmless payload SHA-256 does not match the recorded metadata.'
    }
}
finally {
    $archive.Dispose()
}
Write-Output 'PASS Fixture_MsixContainsV2IndexAndHostedArtifactLayout'
Write-Output "Fixture output: $outputPath"
