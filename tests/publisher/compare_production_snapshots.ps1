[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $RustSnapshot,
    [Parameter(Mandatory = $true)][string] $DotnetSnapshot,
    [Parameter(Mandatory = $true)][string] $SqliteLibraryPath,
    [Parameter(Mandatory = $true)][string] $YamlDotNetPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publisher-validation.ps1')
Initialize-PublisherValidation -SqliteLibraryPath $SqliteLibraryPath -YamlDotNetPath $YamlDotNetPath

function Get-SnapshotMetadata {
    param([string] $SnapshotPath)
    $snapshot = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SnapshotPath).Path)
    $metadataPath = Join-Path $snapshot 'snapshot.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Snapshot metadata is missing: $metadataPath"
    }
    return [pscustomobject]@{
        Path = $snapshot
        Metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -AsHashtable
    }
}

function Get-MappedValues {
    param(
        [string] $DatabasePath, [string] $PackageId,
        [string] $ValueTable, [string] $ValueColumn,
        [string] $MapTable, [string] $MapColumn
    )
    $query = "SELECT valuesTable.$ValueColumn AS value FROM $ValueTable AS valuesTable " +
        "JOIN $MapTable AS valueMap ON valueMap.$MapColumn=valuesTable.rowid " +
        'JOIN packages AS package ON package.rowid=valueMap.package ' +
        "WHERE package.id='$($PackageId.Replace("'", "''"))' ORDER BY valuesTable.$ValueColumn"
    return @([PingetPublisher.Validation.NativeSqlite]::Query($DatabasePath, $query) |
        ForEach-Object { [string]$_.value })
}

function Get-CorrelationValues {
    param([string] $DatabasePath, [string] $PackageId, [string] $Table, [string] $Column)
    $query = "SELECT value.$Column AS value FROM $Table AS value " +
        "JOIN packages AS package ON package.rowid=value.package " +
        "WHERE package.id='$($PackageId.Replace("'", "''"))' ORDER BY value.$Column"
    return @([PingetPublisher.Validation.NativeSqlite]::Query($DatabasePath, $query) |
        ForEach-Object { [string]$_.value })
}

function Read-LogicalIndex {
    param([string] $SnapshotPath)
    $databasePath = Join-Path $SnapshotPath 'Public\index.db'
    $rows = [PingetPublisher.Validation.NativeSqlite]::Query(
        $databasePath,
        'SELECT id, name, moniker, latest_version, arp_min_version, arp_max_version FROM packages'
    )
    $packages = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $id = [string]$row.id
        $packages.Add($id, @{
            name = $row.name
            moniker = $row.moniker
            latestVersion = $row.latest_version
            arpMinimum = $row.arp_min_version
            arpMaximum = $row.arp_max_version
            tags = @(Get-MappedValues $databasePath $id 'tags2' 'tag' 'tags2_map' 'tag')
            commands = @(Get-MappedValues $databasePath $id 'commands2' 'command' 'commands2_map' 'command')
            pfns = @(Get-CorrelationValues $databasePath $id 'pfns2' 'pfn')
            productCodes = @(Get-CorrelationValues $databasePath $id 'productcodes2' 'productcode')
            upgradeCodes = @(Get-CorrelationValues $databasePath $id 'upgradecodes2' 'upgradecode')
            normalizedNames = @(Get-CorrelationValues $databasePath $id 'norm_names2' 'norm_name')
            normalizedPublishers = @(Get-CorrelationValues $databasePath $id 'norm_publishers2' 'norm_publisher')
        })
    }
    return $packages
}

function Read-CompressedSidecars {
    param([string] $SnapshotPath)
    $databasePath = Join-Path $SnapshotPath 'Public\index.db'
    $feedPath = Join-Path $SnapshotPath 'feed'
    $rows = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, 'SELECT id, hash FROM packages')
    $sidecars = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $packageId = [string]$row.id
        if ($row.hash -isnot [byte[]] -or $row.hash.Length -ne 32) {
            throw "Package $packageId has an invalid sidecar hash in the SQLite index."
        }
        $sidecar = Get-PackageSidecar -FeedPath $feedPath -PackageId $packageId -HashBytes $row.hash
        $bytes = [System.IO.File]::ReadAllBytes($sidecar.Path)
        if ((Get-Sha256Hex $bytes) -cne $sidecar.Hash) {
            throw "Package $packageId compressed sidecar hash does not match the SQLite index."
        }
        $sidecars.Add($packageId, [pscustomobject]@{ Hash = $sidecar.Hash; Bytes = $bytes })
    }
    return $sidecars
}

function Read-VersionDocuments {
    param([string] $SnapshotPath)
    $databasePath = Join-Path $SnapshotPath 'Public\index.db'
    $feedPath = Join-Path $SnapshotPath 'feed'
    $rows = [PingetPublisher.Validation.NativeSqlite]::Query($databasePath, 'SELECT id, hash FROM packages')
    $versions = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        $sidecar = Get-PackageSidecar -FeedPath $feedPath -PackageId ([string]$row.id) -HashBytes $row.hash
        $raw = [PingetPublisher.Validation.NativeMsZip]::Decompress([System.IO.File]::ReadAllBytes($sidecar.Path))
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($raw)
        $docs = ConvertFrom-YamlDocuments $text
        if ($docs.Count -ne 1 -or $docs[0] -isnot [System.Collections.IDictionary]) {
            throw "Package $($row.id) version-data stream is not one mapping document."
        }
        $versions.Add([string]$row.id, $docs[0])
    }
    return $versions
}

function Get-ManifestIdentity {
    param([System.Collections.IDictionary] $Manifest)
    return (@(
        [string]$Manifest['PackageIdentifier'],
        [string]$Manifest['PackageVersion'],
        [string]$Manifest['PackageLocale']
    ) -join '|')
}

function Read-MergedManifests {
    param([string] $SnapshotPath)
    $databasePath = Join-Path $SnapshotPath 'Public\index.db'
    $feedPath = Join-Path $SnapshotPath 'feed'
    $versions = Read-VersionDocuments $SnapshotPath
    $manifests = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($packageId in $versions.Keys) {
        foreach ($record in $versions[$packageId].vD) {
            if ($record -isnot [System.Collections.IDictionary] -or
                $record.rP -isnot [string]) {
                throw "Package $packageId contains a version record without a string rP."
            }
            $manifestPath = Get-ContainedFilePath -Root $feedPath -RelativePath ([string]$record.rP)
            $manifestText = [System.Text.UTF8Encoding]::new($false, $true).GetString(
                [System.IO.File]::ReadAllBytes($manifestPath)
            )
            foreach ($manifest in (ConvertFrom-YamlDocuments $manifestText)) {
                if ($manifest -isnot [System.Collections.IDictionary]) { continue }
                $identity = Get-ManifestIdentity $manifest
                if ($manifests.ContainsKey($identity)) {
                    throw "Duplicate merged manifest identity: $identity"
                }
                $manifests.Add($identity, $manifest)
            }
        }
    }
    return $manifests
}

function Read-RawMergedManifestBytes {
    param([string] $SnapshotPath)
    $feedPath = Join-Path $SnapshotPath 'feed'
    $manifestRoot = Join-Path $feedPath 'manifests'
    $manifests = [System.Collections.Generic.Dictionary[string, byte[]]]::new([StringComparer]::Ordinal)
    if (-not (Test-Path -LiteralPath $manifestRoot -PathType Container)) {
        throw "Merged manifest directory is missing: $manifestRoot"
    }
    foreach ($file in Get-ChildItem -LiteralPath $manifestRoot -Filter '*.yaml' -File -Recurse) {
        $relative = [System.IO.Path]::GetRelativePath($feedPath, $file.FullName).Replace('\', '/')
        $containedPath = Get-ContainedFilePath -Root $feedPath -RelativePath $relative
        $bytes = [System.IO.File]::ReadAllBytes($containedPath)
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        $documents = ConvertFrom-YamlDocuments $text
        if ($documents.Count -ne 1 -or $documents[0] -isnot [System.Collections.IDictionary]) {
            throw "Merged manifest is not one YAML mapping: $($file.FullName)"
        }
        $identity = Get-ManifestIdentity $documents[0]
        if ($manifests.ContainsKey($identity)) {
            throw "Duplicate merged manifest identity: $identity"
        }
        $manifests.Add($identity, $bytes)
    }
    return $manifests
}

$rust = Get-SnapshotMetadata $RustSnapshot
$dotnet = Get-SnapshotMetadata $DotnetSnapshot
if ($rust.Metadata.artifactFormatVersion -ne 2 -or $dotnet.Metadata.artifactFormatVersion -ne 2) {
    throw 'Both snapshots must use artifactFormatVersion 2.'
}
if ([string]$rust.Metadata.buildId -cne [string]$dotnet.Metadata.buildId) {
    throw 'Rust and .NET build identifiers differ for identical inputs.'
}
Assert-Equal $rust.Metadata.inventory $dotnet.Metadata.inventory 'Canonical raw input inventories differ.'

$rustIndex = Read-LogicalIndex $rust.Path
$dotnetIndex = Read-LogicalIndex $dotnet.Path
Assert-Equal $rustIndex $dotnetIndex 'WinGet logical database rows or correlation mappings differ.'

$rustSidecars = Read-CompressedSidecars $rust.Path
$dotnetSidecars = Read-CompressedSidecars $dotnet.Path
Assert-Equal $rustSidecars.Keys $dotnetSidecars.Keys 'Package sets differ in compressed sidecar maps.'
foreach ($packageId in $rustSidecars.Keys) {
    if ($rustSidecars[$packageId].Hash -cne $dotnetSidecars[$packageId].Hash -or
        -not (Test-ByteArrayEqual $rustSidecars[$packageId].Bytes $dotnetSidecars[$packageId].Bytes)) {
        throw "Compressed version sidecar bytes or package hash differ for $packageId."
    }
}

$rustVersionData = Read-VersionDocuments $rust.Path
$dotnetVersionData = Read-VersionDocuments $dotnet.Path
Assert-Equal $rustVersionData $dotnetVersionData 'WinGet version sidecar data differs between implementations.'

$rustManifests = Read-MergedManifests $rust.Path
$dotnetManifests = Read-MergedManifests $dotnet.Path
Assert-Equal $rustManifests $dotnetManifests 'Canonical merged manifest data differs.'

$rustManifestBytes = Read-RawMergedManifestBytes $rust.Path
$dotnetManifestBytes = Read-RawMergedManifestBytes $dotnet.Path
Assert-Equal $rustManifestBytes.Keys $dotnetManifestBytes.Keys 'Merged manifest identities differ.'
foreach ($identity in $rustManifestBytes.Keys) {
    if (-not (Test-ByteArrayEqual $rustManifestBytes[$identity] $dotnetManifestBytes[$identity])) {
        throw "Raw merged manifest bytes differ for $identity."
    }
}

$report = [ordered]@{
    buildId = [string]$rust.Metadata.buildId
    packages = $rustIndex.Count
    manifests = $rustManifests.Count
    logicalIndex = 'equal'
    compressedSidecarBytes = 'equal'
    versionData = 'equal'
    canonicalManifests = 'equal'
    rawMergedManifestBytes = 'equal'
    rawInputInventory = 'equal'
}
Write-Output ($report | ConvertTo-Json -Depth 20 -Compress)
