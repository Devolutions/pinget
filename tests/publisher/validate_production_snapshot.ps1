[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Snapshot,
    [string] $ExpectPackage,
    [Parameter(Mandatory = $true)][string] $SqliteLibraryPath,
    [Parameter(Mandatory = $true)][string] $YamlDotNetPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publisher-validation.ps1')
Initialize-PublisherValidation -SqliteLibraryPath $SqliteLibraryPath -YamlDotNetPath $YamlDotNetPath

$snapshotPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Snapshot).Path)
$metadataPath = Join-Path $snapshotPath 'snapshot.json'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw "Snapshot metadata is missing: $metadataPath"
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -AsHashtable
if ($metadata.schemaVersion -ne 1) { throw 'Snapshot schemaVersion is not 1.' }
if ($metadata.artifactFormatVersion -ne 2) { throw 'Snapshot artifactFormatVersion is not 2.' }
if ([string]::IsNullOrWhiteSpace([string]$metadata.buildId)) { throw 'Snapshot buildId is missing.' }
if ($metadata.artifacts -isnot [System.Collections.IDictionary]) {
    throw 'Snapshot artifacts must be a JSON object.'
}

$feedPath = Join-Path $snapshotPath 'feed'
foreach ($relative in $metadata.artifacts.Keys) {
    $artifactPath = Get-ContainedFilePath -Root $feedPath -RelativePath ([string]$relative)
    $actualHash = Get-FileSha256Hex $artifactPath
    if ($actualHash -cne [string]$metadata.artifacts[$relative]) {
        throw "Artifact hash mismatch: $relative"
    }
}

$indexPath = Join-Path $snapshotPath 'Public\index.db'
if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
    throw "Snapshot WinGet index is missing: $indexPath"
}
$indexHash = [string]$metadata.indexHash
if ($indexHash -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileSha256Hex $indexPath) -cne $indexHash) {
    throw 'Snapshot indexHash does not match the exact Public/index.db bytes.'
}
$integrity = [PingetPublisher.Validation.NativeSqlite]::Query($indexPath, 'PRAGMA integrity_check')
if ($integrity.Count -ne 1 -or $integrity[0].integrity_check -ne 'ok') {
    throw "SQLite integrity_check failed: $($integrity | ConvertTo-Json -Compress)"
}
$expectedTables = @('commands2', 'commands2_map', 'metadata', 'norm_names2', 'norm_publishers2', 'packages', 'pfns2', 'productcodes2', 'tags2', 'tags2_map', 'upgradecodes2')
Assert-Equal (Get-SqliteTableNames $indexPath) $expectedTables 'Unexpected WinGet v2 table set.'
$indexMetadata = [PingetPublisher.Validation.NativeSqlite]::Query($indexPath, 'SELECT name, value FROM metadata')
$indexMetadataMap = @{}
foreach ($row in $indexMetadata) { $indexMetadataMap[[string]$row.name] = [string]$row.value }
if ($indexMetadataMap.majorVersion -ne '2' -or $indexMetadataMap.minorVersion -ne '0') {
    throw 'WinGet index schema markers are not majorVersion=2 and minorVersion=0.'
}

$packages = [PingetPublisher.Validation.NativeSqlite]::Query(
    $indexPath,
    'SELECT id, name, latest_version, hash, arp_min_version, arp_max_version FROM packages ORDER BY id COLLATE NOCASE'
)
if ($packages.Count -eq 0) { throw 'Production snapshot contains no packages.' }
if (-not [string]::IsNullOrEmpty($ExpectPackage) -and
    @($packages | Where-Object { $_.id -ceq $ExpectPackage }).Count -eq 0) {
    throw "Expected package '$ExpectPackage' is absent."
}

$decodedVersions = [ordered]@{}
foreach ($package in $packages) {
    $packageId = [string]$package.id
    if ($package.hash -isnot [byte[]] -or $package.hash.Length -ne 32) {
        throw "Package $packageId has no 32-byte sidecar hash."
    }
    $sidecar = Get-PackageSidecar -FeedPath $feedPath -PackageId $packageId -HashBytes $package.hash
    if ((Get-FileSha256Hex $sidecar.Path) -cne $sidecar.Hash) {
        throw "Package $packageId sidecar hash/path is invalid."
    }
    $rawVersionData = [PingetPublisher.Validation.NativeMsZip]::Decompress(
        [System.IO.File]::ReadAllBytes($sidecar.Path)
    )
    $versionText = [System.Text.UTF8Encoding]::new($false, $true).GetString($rawVersionData)
    if (-not $versionText.StartsWith("sV: `"1.0`"`n", [StringComparison]::Ordinal) -and
        -not $versionText.StartsWith("sV: `"1.0`"`r`n", [StringComparison]::Ordinal)) {
        throw "Package $packageId has an unexpected version sidecar header."
    }
    $versionDocuments = ConvertFrom-YamlDocuments $versionText
    if ($versionDocuments.Count -ne 1 -or $versionDocuments[0] -isnot [System.Collections.IDictionary]) {
        throw "Package $packageId version sidecar is not one mapping document."
    }
    $versionDocument = $versionDocuments[0]
    if ($versionDocument.sV -cne '1.0' -or $versionDocument.vD -isnot [System.Array] -or
        $versionDocument.vD.Length -eq 0) {
        throw "Package $packageId sidecar has an invalid schema marker or empty vD list."
    }

    $versions = [System.Collections.Generic.List[string]]::new()
    $arpMinimums = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $arpMaximums = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in $versionDocument.vD) {
        if ($record -isnot [System.Collections.IDictionary]) {
            throw "Package $packageId sidecar contains a non-object vD record."
        }
        foreach ($field in @('v', 'rP', 's256H')) {
            if ($record[$field] -isnot [string]) {
                throw "Package $packageId sidecar record lacks string $field."
            }
        }
        $version = [string]$record.v
        $manifestRelative = [string]$record.rP
        $manifestHash = [string]$record.s256H
        if ($manifestHash -cnotmatch '^[0-9a-f]{64}$') {
            throw "Package $packageId sidecar contains invalid lowercase SHA-256 for $version."
        }
        if (-not $versions.Contains($version)) {
            $versions.Add($version)
        } else {
            throw "Package $packageId sidecar contains duplicate version $version."
        }
        $manifestPath = Get-ContainedFilePath -Root $feedPath -RelativePath $manifestRelative
        if ((Get-FileSha256Hex $manifestPath) -cne $manifestHash) {
            throw "Package $packageId manifest hash mismatch for $version."
        }
        if ($record.ContainsKey('aMiV') -and $record.aMiV -is [string]) { [void]$arpMinimums.Add([string]$record.aMiV) }
        if ($record.ContainsKey('aMaV') -and $record.aMaV -is [string]) { [void]$arpMaximums.Add([string]$record.aMaV) }
    }
    if ($versions[0] -cne [string]$package.latest_version) {
        throw "Package $packageId sidecar does not place latest version first."
    }
    if ($null -ne $package.arp_min_version -and -not $arpMinimums.Contains([string]$package.arp_min_version)) {
        throw "Minimum ARP version is missing from sidecar for $packageId."
    }
    if ($null -ne $package.arp_max_version -and -not $arpMaximums.Contains([string]$package.arp_max_version)) {
        throw "Maximum ARP version is missing from sidecar for $packageId."
    }
    if ($packageId -ceq 'Pinget.Test.Matrix') {
        $actualArp = @{}
        foreach ($record in $versionDocument.vD) {
            $actualArp[[string]$record.v] = @($record.aMiV, $record.aMaV)
        }
        $expectedArp = @{
            '1.9.0' = @('1.9.0', '1.9.0')
            '1.10.0' = @('1.10.0', '1.10.0')
        }
        Assert-Equal $actualArp $expectedArp 'Per-version ARP metadata is incorrect for Pinget.Test.Matrix.'
        if ($package.arp_min_version -cne '1.9.0' -or $package.arp_max_version -cne '1.10.0') {
            throw 'Package-level ARP bounds are incorrect for Pinget.Test.Matrix.'
        }
    }
    $decodedVersions[$packageId] = @($versions.ToArray())
}

$report = [ordered]@{
    snapshot = $snapshotPath
    buildId = [string]$metadata.buildId
    packages = [ordered]@{}
    versions = $decodedVersions
    artifacts = $metadata.artifacts.Count
    sqlite = 'ok (native independent SQLite API)'
    mszip = 'native Windows Compression API'
}
foreach ($package in $packages) { $report.packages[[string]$package.id] = [string]$package.latest_version }
Write-Output ($report | ConvertTo-Json -Depth 20 -Compress)
