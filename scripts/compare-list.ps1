#requires -Version 7.0
<#
.SYNOPSIS
    Compares `winget list` vs Rust and C# `pinget list` metadata on the current machine.

.DESCRIPTION
    Runs `winget list` (text, parsed), C# `pinget list --output json`, and
    Rust `pinget list --output json`, normalises them into a common schema,
    and reports structured diffs across five classes for each implementation:

    1. CorrelationMiss  — winget shows a catalog Id + source; pinget shows
       a raw ARP/MSIX local Id for the same package (matched by display name).
    2. VersionMismatch  — same effective Id, different InstalledVersion.
    3. SourceMismatch   — same effective Id but pinget reports a different
       source name (or none) compared with winget.
    4. AvailableDelta   — one tool surfaces an upgrade, the other does not.
    5. DuplicateId      — pinget emits the same catalog or local Id more than
       once (raw + correlated row for the same install).
    Plus counts of: rows matching fully, rows only in winget, rows only
    in pinget.

    The script is strictly read-only: it never mutates installs, sources,
    or pins. Designed to be re-run on different machines to build a corpus
    of correlation classes that pinget gets wrong.

.PARAMETER Pinget
    Backward-compatible alias for -PingetCs.

.PARAMETER PingetCs
    Path to the C# pinget executable. Defaults to the Debug build output from this repo.

.PARAMETER PingetRust
    Path to the Rust pinget executable. Defaults to rust\target\debug\pinget.exe.

.PARAMETER Winget
    Path to the winget executable. Defaults to `winget` on PATH.

.PARAMETER FixturePath
    If set, writes a JSON fixture with the full diff and raw captures.

.PARAMETER UseSystemWingetAppRoot
    Point Pinget at Desktop App Installer's LocalState through PINGET_APPROOT
    so Pinget uses the same system WinGet source/pin/settings state as winget.exe.

.PARAMETER PingetAppRoot
    Explicit app root to expose to both Pinget CLIs through PINGET_APPROOT.

.PARAMETER IncludeUnknown
    Kept for upgrade parity with sibling scripts. Plain list does not pass
    `--include-unknown` because both winget and pinget reject it unless
    `--upgrade-available` is also present.

.PARAMETER FailOnDiff
    Exit non-zero when any non-cosmetic difference is found.

.PARAMETER UpdateSources
    Run `source update` before diffing.

.EXAMPLE
    .\compare-list.ps1
    # Quick interactive check.

.EXAMPLE
    .\compare-list.ps1 -FixturePath ./list-diff.json -FailOnDiff
#>
param(
    [string]$Pinget,
    [string]$PingetCs = (Join-Path $PSScriptRoot "..\dotnet\src\Devolutions.Pinget.Cli\bin\Debug\net10.0\pinget.exe"),
    [string]$PingetRust = (Join-Path $PSScriptRoot "..\rust\target\debug\pinget.exe"),
    [string]$Winget = "winget",
    [string]$FixturePath,
    [switch]$UseSystemWingetAppRoot,
    [string]$PingetAppRoot,
    [bool]$IncludeUnknown = $true,
    [switch]$FailOnDiff,
    [switch]$UpdateSources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 80)
    Write-Host $Title
    Write-Host ("=" * 80)
}

function Invoke-CaptureLines {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [Parameter(Mandatory = $true)] [string[]]$Arguments
    )
    try {
        $lines = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }
        $exit = $LASTEXITCODE
        [pscustomobject]@{ ExitCode = $exit; Lines = @($lines) }
    } catch {
        [pscustomobject]@{ ExitCode = -1; Lines = @($_.Exception.Message) }
    }
}

# ---------------------------------------------------------------------------
# Winget list capture + parser
# ---------------------------------------------------------------------------

function Get-WingetListRows {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [bool]$IncludeUnknown
    )

    # Note: winget list --include-unknown is only valid with --upgrade-available
    # on this winget build (v1.29.x-preview). Plain list already shows all packages.
    $listArgs = @("list", "--accept-source-agreements", "--disable-interactivity")
    $captured = Invoke-CaptureLines -Executable $Executable -Arguments $listArgs

    # Locate the separator line (a run of dashes) and derive column offsets
    # from the header line immediately above it. winget's list table has
    # either 4 or 5 columns (Available is added when any row has an upgrade).
    #   Name  Id  Version  [Available]  Source
    $separatorIdx = -1
    for ($i = 0; $i -lt $captured.Lines.Count; $i++) {
        if ($captured.Lines[$i] -match '^-{10,}$') {
            $separatorIdx = $i
            break
        }
    }

    if ($separatorIdx -lt 1) {
        $emptyOk = @($captured.Lines | Where-Object {
            $_ -match 'No installed package'
        }).Count -gt 0
        return @{
            Rows       = @()
            ExitCode   = $captured.ExitCode
            Raw        = $captured.Lines
            Diagnostic = if ($emptyOk) { $null } else {
                "no table header/separator found in winget list output"
            }
        }
    }

    $header = $captured.Lines[$separatorIdx - 1]
    # Column tokens in order; Available is optional.
    $columnDefs = @(
        @{ Key = 'Name';      Token = 'Name' },
        @{ Key = 'Id';        Token = 'Id' },
        @{ Key = 'Version';   Token = 'Version' },
        @{ Key = 'Available'; Token = 'Available' },
        @{ Key = 'Source';    Token = 'Source' }
    )
    $offsets = [ordered]@{}
    foreach ($col in $columnDefs) {
        $pos = $header.IndexOf($col.Token)
        if ($pos -ge 0) { $offsets[$col.Key] = $pos }
    }
    # Name and Id are required; warn if absent.
    if (-not $offsets.Contains('Name') -or -not $offsets.Contains('Id')) {
        return @{
            Rows       = @()
            ExitCode   = $captured.ExitCode
            Raw        = $captured.Lines
            Diagnostic = "winget list header is missing Name or Id column"
        }
    }

    function Get-Slice {
        param([string]$Line, [int]$Start, [int]$End)
        if ($Start -ge $Line.Length) { return "" }
        $eff = [Math]::Min($End, $Line.Length)
        return $Line.Substring($Start, $eff - $Start).TrimEnd().TrimStart()
    }

    $orderedKeys = @($offsets.Keys)
    $rows = New-Object System.Collections.Generic.List[object]

    for ($i = $separatorIdx + 1; $i -lt $captured.Lines.Count; $i++) {
        $line = $captured.Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\d+ packages? (listed|found)') { break }
        if ($line -match '^<additional entries') { continue }
        if ($line -match '^[\s\\/\-|]+$') { continue }

        $row = [ordered]@{}
        for ($c = 0; $c -lt $orderedKeys.Count; $c++) {
            $key = $orderedKeys[$c]
            $start = $offsets[$key]
            $end = if ($c + 1 -lt $orderedKeys.Count) { $offsets[$orderedKeys[$c + 1]] } else { [int]::MaxValue }
            $row[$key] = Get-Slice -Line $line -Start $start -End $end
        }
        # Ensure optional columns exist with empty defaults
        foreach ($key in @('Available', 'Source')) {
            if (-not $row.Contains($key)) { $row[$key] = "" }
        }
        # Id is required for a valid data row
        if (-not $row['Id']) { continue }

        $rows.Add([pscustomobject]$row) | Out-Null
    }

    if ($rows.Count -eq 0) {
        $dataLines = 0
        for ($i = $separatorIdx + 1; $i -lt $captured.Lines.Count; $i++) {
            $line = $captured.Lines[$i]
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -match '^\d+ packages? (listed|found)') { break }
            if ($line -match '^<additional entries') { continue }
            $dataLines++
        }
        if ($dataLines -gt 0) {
            return @{
                Rows       = @()
                ExitCode   = $captured.ExitCode
                Raw        = $captured.Lines
                Diagnostic = "winget list parsed 0 rows but $dataLines data-shaped lines followed separator"
            }
        }
    }

    return @{ Rows = $rows.ToArray(); ExitCode = $captured.ExitCode; Raw = $captured.Lines; Diagnostic = $null }
}

# ---------------------------------------------------------------------------
# Pinget list capture + parser
# ---------------------------------------------------------------------------

function Get-PingetListRows {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [bool]$IncludeUnknown
    )

    # Note: pinget list does not support --include-unknown; that flag is for
    # the upgrade/upgrade-available check only. Plain list shows all packages.
    $listArgs = @("list", "--output", "json")
    $captured = Invoke-CaptureLines -Executable $Executable -Arguments $listArgs
    $joined = $captured.Lines -join "`n"

    try {
        $parsed = $joined | ConvertFrom-Json -Depth 32 -ErrorAction Stop
    } catch {
        return @{
            Rows       = @()
            ExitCode   = $captured.ExitCode
            Raw        = $captured.Lines
            Diagnostic = "pinget JSON parse failed: $($_.Exception.Message)"
        }
    }

    function Get-Field {
        param($Obj, [string[]]$Names)
        foreach ($n in $Names) {
            $p = $Obj.PSObject.Properties[$n]
            if ($null -ne $p) { return $p.Value }
        }
        return $null
    }

    $rows = foreach ($m in @($parsed.matches)) {
        [pscustomobject]@{
            Name              = Get-Field $m @('name', 'Name')
            Id                = Get-Field $m @('id', 'Id')
            LocalId           = Get-Field $m @('localId', 'local_id', 'LocalId')
            InstalledVersion  = Get-Field $m @('installedVersion', 'installed_version', 'InstalledVersion') ?? ""
            AvailableVersion  = Get-Field $m @('availableVersion', 'available_version', 'AvailableVersion') ?? ""
            SourceName        = Get-Field $m @('sourceName', 'source_name', 'SourceName') ?? ""
            Publisher         = Get-Field $m @('publisher', 'Publisher') ?? ""
            Scope             = Get-Field $m @('scope', 'Scope') ?? ""
            InstallerCategory = Get-Field $m @('installerCategory', 'installer_category', 'InstallerCategory') ?? ""
            InstallLocation   = Get-Field $m @('installLocation', 'install_location', 'InstallLocation') ?? ""
            PackageFamilyNames = @(Get-Field $m @('packageFamilyNames', 'package_family_names', 'PackageFamilyNames') ?? @())
            ProductCodes      = @(Get-Field $m @('productCodes', 'product_codes', 'ProductCodes') ?? @())
            UpgradeCodes      = @(Get-Field $m @('upgradeCodes', 'upgrade_codes', 'UpgradeCodes') ?? @())
        }
    }

    return @{ Rows = @($rows); ExitCode = $captured.ExitCode; Raw = $captured.Lines; Diagnostic = $null }
}

# ---------------------------------------------------------------------------
# Normalise name for matching (handle winget's ellipsis truncation)
# ---------------------------------------------------------------------------

function Get-NamePrefix {
    param([string]$Name)
    $ellipsis = [char]0x2026
    if ($Name.EndsWith($ellipsis)) {
        return $Name.Substring(0, $Name.Length - 1).ToLowerInvariant()
    }
    return $Name.ToLowerInvariant()
}

function Test-NamesMatch {
    param([string]$WingetName, [string]$PingetName)
    if ($WingetName -ieq $PingetName) { return $true }
    $ellipsis = [char]0x2026
    if ($WingetName.EndsWith($ellipsis)) {
        $prefix = $WingetName.Substring(0, $WingetName.Length - 1)
        if ($PingetName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------------------
# Comparison engine
# ---------------------------------------------------------------------------

function Compare-ListRows {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$WingetRows,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$PingetRows
    )

    # Index winget rows by catalog Id (lower-cased)
    $wingetById = @{}
    foreach ($row in $WingetRows) {
        $key = $row.Id.ToLowerInvariant()
        if (-not $wingetById.ContainsKey($key)) { $wingetById[$key] = [System.Collections.Generic.List[object]]::new() }
        $wingetById[$key].Add($row) | Out-Null
    }
    # Index winget rows by normalised display name for fallback matching
    $wingetByName = @{}
    foreach ($row in $WingetRows) {
        $key = Get-NamePrefix -Name $row.Name
        if (-not $wingetByName.ContainsKey($key)) { $wingetByName[$key] = [System.Collections.Generic.List[object]]::new() }
        $wingetByName[$key].Add($row) | Out-Null
    }

    # Index pinget rows by catalog Id (rows where id != localId are correlated)
    $pingetByCatalogId = @{}
    $pingetByLocalId   = @{}
    $pingetByName      = @{}
    foreach ($row in $PingetRows) {
        $catKey   = $row.Id.ToLowerInvariant()
        $localKey = ($row.LocalId ?? $row.Id).ToLowerInvariant()
        $nameKey  = $row.Name.ToLowerInvariant()

        if (-not $pingetByCatalogId.ContainsKey($catKey)) { $pingetByCatalogId[$catKey] = [System.Collections.Generic.List[object]]::new() }
        $pingetByCatalogId[$catKey].Add($row) | Out-Null

        if (-not $pingetByLocalId.ContainsKey($localKey)) { $pingetByLocalId[$localKey] = [System.Collections.Generic.List[object]]::new() }
        $pingetByLocalId[$localKey].Add($row) | Out-Null

        if (-not $pingetByName.ContainsKey($nameKey)) { $pingetByName[$nameKey] = [System.Collections.Generic.List[object]]::new() }
        $pingetByName[$nameKey].Add($row) | Out-Null
    }

    # Result buckets
    $correlationMiss  = [System.Collections.Generic.List[object]]::new()
    $versionMismatch  = [System.Collections.Generic.List[object]]::new()
    $sourceMismatch   = [System.Collections.Generic.List[object]]::new()
    $availableDelta   = [System.Collections.Generic.List[object]]::new()
    $duplicateId      = [System.Collections.Generic.List[object]]::new()
    $onlyInWinget     = [System.Collections.Generic.List[object]]::new()
    $cosmeticDiff     = [System.Collections.Generic.List[object]]::new()
    $matching         = [System.Collections.Generic.List[object]]::new()

    # Track which pinget rows were matched so we can find unmatched (extra)
    $matchedPingetIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($w in $WingetRows) {
        $wId    = $w.Id.ToLowerInvariant()
        $wName  = Get-NamePrefix -Name $w.Name
        $wHasSource = $w.Source -and $w.Source.Trim()

        # Attempt 1: match by catalog Id directly (both correlated to same catalog entry)
        if ($pingetByCatalogId.ContainsKey($wId)) {
            $pList = $pingetByCatalogId[$wId]
            $p = $pList | Where-Object { $_.InstalledVersion -eq $w.Version } | Select-Object -First 1
            if ($null -eq $p) { $p = $pList[0] }
            $matchedPingetIds.Add($p.Id.ToLowerInvariant()) | Out-Null

            # Version check
            if ($w.Version -ne $p.InstalledVersion) {
                $versionMismatch.Add([pscustomobject]@{
                    Id             = $w.Id
                    WingetVersion  = $w.Version
                    PingetVersion  = $p.InstalledVersion
                    WingetSource   = $w.Source
                    PingetSource   = $p.SourceName
                }) | Out-Null
            } else {
                # Source name check (only meaningful when winget has a source)
                if ($wHasSource -and $w.Source -ne $p.SourceName) {
                    $sourceMismatch.Add([pscustomobject]@{
                        Id           = $w.Id
                        Version      = $w.Version
                        WingetSource = $w.Source
                        PingetSource = $p.SourceName
                    }) | Out-Null
                }
                # Available version check
                $wAvail = $w.Available ?? ""
                $pAvail = $p.AvailableVersion ?? ""
                if (($wAvail -and -not $pAvail) -or (-not $wAvail -and $pAvail)) {
                    $availableDelta.Add([pscustomobject]@{
                        Id             = $w.Id
                        WingetAvail    = $wAvail
                        PingetAvail    = $pAvail
                        WingetSource   = $w.Source
                    }) | Out-Null
                } else {
                    $matching.Add($w) | Out-Null
                }
            }
            continue
        }

        # Attempt 2: winget has a catalog Id but pinget shows local Id — match by name
        if ($wHasSource) {
            # winget correlates this package; look in pinget by name
            $nameMatches = @()
            if ($pingetByName.ContainsKey($wName)) {
                $nameMatches = @($pingetByName[$wName] | Where-Object { $_.Id -ne $w.Id })
            }
            # Also try prefix match for ellipsis-truncated names
            if ($nameMatches.Count -eq 0) {
                foreach ($pKey in $pingetByName.Keys) {
                    if ($wName.Length -gt 0 -and $pKey.StartsWith($wName)) {
                        $nameMatches += $pingetByName[$pKey]
                    }
                }
            }

            if ($nameMatches.Count -gt 0) {
                $p = $nameMatches[0]
                $matchedPingetIds.Add($p.Id.ToLowerInvariant()) | Out-Null
                # Correlation miss: winget resolved catalog ID, pinget still has raw ARP id
                $correlationMiss.Add([pscustomobject]@{
                    Name          = $w.Name
                    WingetId      = $w.Id
                    PingetId      = $p.Id
                    PingetLocalId = $p.LocalId
                    WingetVersion = $w.Version
                    PingetVersion = $p.InstalledVersion
                    WingetSource  = $w.Source
                    PingetSource  = $p.SourceName
                }) | Out-Null
            } else {
                $onlyInWinget.Add($w) | Out-Null
            }
            continue
        }

        # Attempt 3: both uncorrelated — match by local ARP/MSIX Id
        if ($pingetByLocalId.ContainsKey($wId)) {
            $pList = $pingetByLocalId[$wId]
            $p = $pList[0]
            $matchedPingetIds.Add($p.Id.ToLowerInvariant()) | Out-Null
            if ($w.Version -ne $p.InstalledVersion) {
                $versionMismatch.Add([pscustomobject]@{
                    Id            = $w.Id
                    WingetVersion = $w.Version
                    PingetVersion = $p.InstalledVersion
                    WingetSource  = $w.Source
                    PingetSource  = $p.SourceName
                }) | Out-Null
            } else {
                $matching.Add($w) | Out-Null
            }
            continue
        }

        # Attempt 4: uncorrelated, try name match
        if ($pingetByName.ContainsKey($wName)) {
            $p = $pingetByName[$wName][0]
            $matchedPingetIds.Add($p.Id.ToLowerInvariant()) | Out-Null
            if ($w.Version -ne $p.InstalledVersion) {
                $versionMismatch.Add([pscustomobject]@{
                    Id            = $w.Id
                    WingetVersion = $w.Version
                    PingetVersion = $p.InstalledVersion
                    WingetSource  = $w.Source
                    PingetSource  = $p.SourceName
                }) | Out-Null
            } else {
                $matching.Add($w) | Out-Null
            }
            continue
        }

        $onlyInWinget.Add($w) | Out-Null
    }

    # Packages only in pinget (not matched to any winget row)
    $onlyInPinget = [System.Collections.Generic.List[object]]::new()
    foreach ($p in $PingetRows) {
        if (-not $matchedPingetIds.Contains($p.Id.ToLowerInvariant())) {
            $onlyInPinget.Add($p) | Out-Null
        }
    }

    # Duplicate IDs in pinget (same catalog Id appearing more than once).
    # Shared duplicate IDs are common for side-by-side packages and are not a
    # pinget-only discrepancy; report them separately from pinget-only extras.
    $wingetSeen = @{}
    foreach ($row in $WingetRows) {
        $key = $row.Id.ToLowerInvariant()
        if (-not $wingetSeen.ContainsKey($key)) { $wingetSeen[$key] = 0 }
        $wingetSeen[$key]++
    }

    $seen = @{}
    foreach ($row in $PingetRows) {
        $key = $row.Id.ToLowerInvariant()
        if ($seen.ContainsKey($key)) {
            if ($seen[$key] -eq 1) {
                # First time we notice a dup — add original too
                $duplicateId.Add([pscustomobject]@{ Id = $row.Id; Occurrences = 0 }) | Out-Null
            }
            $seen[$key]++
        } else {
            $seen[$key] = 1
        }
    }
    # Build proper duplicate report
    $dupReport = [System.Collections.Generic.List[object]]::new()
    $sharedDupReport = [System.Collections.Generic.List[object]]::new()
    $dupCounts  = @{}
    foreach ($row in $PingetRows) {
        $key = $row.Id.ToLowerInvariant()
        if ($seen[$key] -gt 1) {
            if (-not $dupCounts.ContainsKey($key)) { $dupCounts[$key] = 0 }
            $dupCounts[$key]++
        }
    }
    foreach ($key in ($dupCounts.Keys | Sort-Object)) {
        $rows = @($PingetRows | Where-Object { $_.Id.ToLowerInvariant() -eq $key })
        $entry = [pscustomobject]@{
            Id          = $rows[0].Id
            Occurrences = $dupCounts[$key]
            Rows        = $rows
        }
        if ($wingetSeen.ContainsKey($key) -and $wingetSeen[$key] -gt 1) {
            $sharedDupReport.Add($entry) | Out-Null
        } else {
            $dupReport.Add($entry) | Out-Null
        }
    }

    return [pscustomobject]@{
        CorrelationMiss = $correlationMiss.ToArray()
        VersionMismatch = $versionMismatch.ToArray()
        SourceMismatch  = $sourceMismatch.ToArray()
        AvailableDelta  = $availableDelta.ToArray()
        DuplicateId     = $dupReport.ToArray()
        SharedDuplicateId = $sharedDupReport.ToArray()
        OnlyInWinget    = $onlyInWinget.ToArray()
        OnlyInPinget    = $onlyInPinget.ToArray()
        CosmeticDiff    = $cosmeticDiff.ToArray()
        Matching        = $matching.ToArray()
    }
}

# ---------------------------------------------------------------------------
# Pinget implementation drift comparison
# ---------------------------------------------------------------------------

function Get-PingetIdentityKey {
    param($Row)
    $localId = $Row.LocalId ?? ""
    if (-not [string]::IsNullOrWhiteSpace($localId)) { return $localId.ToLowerInvariant() }
    $id = $Row.Id ?? ""
    if (-not [string]::IsNullOrWhiteSpace($id)) { return $id.ToLowerInvariant() }
    return ("{0}|{1}" -f ($Row.Name ?? ""), ($Row.InstalledVersion ?? "")).ToLowerInvariant()
}

function Convert-MetadataValue {
    param($Value)
    if ($null -eq $Value) { return "" }
    if ($Value -is [string]) { return $Value.Trim() }
    if ($Value -is [System.Collections.IEnumerable]) {
        return (@($Value | ForEach-Object { $_.ToString() }) | Sort-Object) -join ";"
    }
    return $Value.ToString()
}

function Compare-PingetImplementations {
    param(
        [Parameter(Mandatory = $true)] [string]$LeftLabel,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$LeftRows,
        [Parameter(Mandatory = $true)] [string]$RightLabel,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$RightRows
    )

    $leftByKey = @{}
    foreach ($row in $LeftRows) {
        $key = Get-PingetIdentityKey $row
        if (-not $leftByKey.ContainsKey($key)) { $leftByKey[$key] = [System.Collections.Generic.List[object]]::new() }
        $leftByKey[$key].Add($row) | Out-Null
    }

    $rightByKey = @{}
    foreach ($row in $RightRows) {
        $key = Get-PingetIdentityKey $row
        if (-not $rightByKey.ContainsKey($key)) { $rightByKey[$key] = [System.Collections.Generic.List[object]]::new() }
        $rightByKey[$key].Add($row) | Out-Null
    }

    $fields = @(
        'Name', 'Id', 'LocalId', 'InstalledVersion', 'AvailableVersion',
        'SourceName', 'Publisher', 'Scope', 'InstallerCategory',
        'InstallLocation', 'PackageFamilyNames', 'ProductCodes', 'UpgradeCodes'
    )
    $metadataMismatch = [System.Collections.Generic.List[object]]::new()
    $onlyInLeft = [System.Collections.Generic.List[object]]::new()
    $onlyInRight = [System.Collections.Generic.List[object]]::new()
    $duplicateLeft = [System.Collections.Generic.List[object]]::new()
    $duplicateRight = [System.Collections.Generic.List[object]]::new()
    $matching = [System.Collections.Generic.List[object]]::new()
    $matchedRightKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($key in ($leftByKey.Keys | Sort-Object)) {
        $leftList = $leftByKey[$key]
        if ($leftList.Count -gt 1) {
            $duplicateLeft.Add([pscustomobject]@{ Key = $key; Occurrences = $leftList.Count; Rows = $leftList.ToArray() }) | Out-Null
        }
        if (-not $rightByKey.ContainsKey($key)) {
            foreach ($row in $leftList) { $onlyInLeft.Add($row) | Out-Null }
            continue
        }

        $rightList = $rightByKey[$key]
        if ($rightList.Count -gt 1) {
            $duplicateRight.Add([pscustomobject]@{ Key = $key; Occurrences = $rightList.Count; Rows = $rightList.ToArray() }) | Out-Null
        }
        $matchedRightKeys.Add($key) | Out-Null

        $left = $leftList[0]
        $right = $rightList[0]
        $fieldDiffs = [System.Collections.Generic.List[object]]::new()
        foreach ($field in $fields) {
            $leftValue = Convert-MetadataValue ($left.$field)
            $rightValue = Convert-MetadataValue ($right.$field)
            if ($leftValue -ne $rightValue) {
                $fieldDiffs.Add([pscustomobject]@{
                    Field      = $field
                    $LeftLabel = $leftValue
                    $RightLabel = $rightValue
                }) | Out-Null
            }
        }

        if ($fieldDiffs.Count -gt 0) {
            $metadataMismatch.Add([pscustomobject]@{
                Key       = $key
                Name      = $left.Name
                Id        = $left.Id
                LocalId   = $left.LocalId
                Fields    = $fieldDiffs.ToArray()
            }) | Out-Null
        } else {
            $matching.Add($left) | Out-Null
        }
    }

    foreach ($key in ($rightByKey.Keys | Sort-Object)) {
        if (-not $matchedRightKeys.Contains($key)) {
            foreach ($row in $rightByKey[$key]) { $onlyInRight.Add($row) | Out-Null }
        }
    }

    return [pscustomobject]@{
        LeftLabel        = $LeftLabel
        RightLabel       = $RightLabel
        MetadataMismatch = $metadataMismatch.ToArray()
        OnlyInLeft       = $onlyInLeft.ToArray()
        OnlyInRight      = $onlyInRight.ToArray()
        DuplicateLeft    = $duplicateLeft.ToArray()
        DuplicateRight   = $duplicateRight.ToArray()
        Matching         = $matching.ToArray()
    }
}

# ---------------------------------------------------------------------------
# Report helpers
# ---------------------------------------------------------------------------

function Get-WingetParityFailureCount {
    param($Diff)
    return $Diff.CorrelationMiss.Count + $Diff.VersionMismatch.Count +
        $Diff.SourceMismatch.Count + $Diff.AvailableDelta.Count +
        $Diff.DuplicateId.Count + $Diff.OnlyInWinget.Count +
        $Diff.OnlyInPinget.Count
}

function Get-ImplementationDriftFailureCount {
    param($Diff)
    return $Diff.MetadataMismatch.Count + $Diff.OnlyInLeft.Count +
        $Diff.OnlyInRight.Count + $Diff.DuplicateLeft.Count +
        $Diff.DuplicateRight.Count
}

function Write-WingetParityReport {
    param([string]$Label, $Diff)

    Write-Section "$Label Verdict"
    Write-Host ("Matching         : {0}" -f $Diff.Matching.Count)
    Write-Host ("CorrelationMiss  : {0}  (winget catalog Id; $Label raw ARP/MSIX Id)" -f $Diff.CorrelationMiss.Count)
    Write-Host ("VersionMismatch  : {0}  (same Id, different InstalledVersion)" -f $Diff.VersionMismatch.Count)
    Write-Host ("SourceMismatch   : {0}  (same Id, different SourceName)" -f $Diff.SourceMismatch.Count)
    Write-Host ("AvailableDelta   : {0}  (upgrade reported by one tool only)" -f $Diff.AvailableDelta.Count)
    Write-Host ("DuplicateId      : {0}  (same Id in $Label more than once, not duplicated by winget)" -f $Diff.DuplicateId.Count)
    Write-Host ("SharedDuplicateId: {0}  (same Id duplicated by both winget and $Label)" -f $Diff.SharedDuplicateId.Count)
    Write-Host ("OnlyInWinget     : {0}  (present in winget, absent in $Label)" -f $Diff.OnlyInWinget.Count)
    Write-Host ("OnlyInPinget     : {0}  (present in $Label, absent in winget)" -f $Diff.OnlyInPinget.Count)

    if ($Diff.CorrelationMiss.Count -gt 0) {
        Write-Section ("$Label CORRELATION MISS ({0}) — winget has catalog Id; $Label uses raw ARP/MSIX Id" -f $Diff.CorrelationMiss.Count)
        foreach ($entry in ($Diff.CorrelationMiss | Sort-Object Name)) {
            Write-Host ("{0}" -f $entry.Name)
            Write-Host ("    winget : {0,-50} [{1}]" -f $entry.WingetId, $entry.WingetSource)
            Write-Host ("    $Label : {0,-50} [{1}]" -f $entry.PingetId, $entry.PingetSource)
            if ($entry.WingetVersion -ne $entry.PingetVersion) {
                Write-Host ("    version: winget={0}  $Label={1}" -f $entry.WingetVersion, $entry.PingetVersion)
            }
        }
    }

    if ($Diff.VersionMismatch.Count -gt 0) {
        Write-Section ("$Label VERSION MISMATCH ({0}) — same Id, different InstalledVersion" -f $Diff.VersionMismatch.Count)
        foreach ($entry in ($Diff.VersionMismatch | Sort-Object Id)) {
            Write-Host ("{0}  [{1}]" -f $entry.Id, $entry.WingetSource)
            Write-Host ("    winget : {0}" -f $entry.WingetVersion)
            Write-Host ("    $Label : {0}" -f $entry.PingetVersion)
        }
    }

    if ($Diff.SourceMismatch.Count -gt 0) {
        Write-Section ("$Label SOURCE MISMATCH ({0}) — same Id, different SourceName" -f $Diff.SourceMismatch.Count)
        foreach ($entry in ($Diff.SourceMismatch | Sort-Object Id)) {
            Write-Host ("{0} v{1}" -f $entry.Id, $entry.Version)
            Write-Host ("    winget : [{0}]" -f $entry.WingetSource)
            Write-Host ("    $Label : [{0}]" -f $entry.PingetSource)
        }
    }

    if ($Diff.AvailableDelta.Count -gt 0) {
        Write-Section ("$Label AVAILABLE DELTA ({0}) — upgrade reported by one tool only" -f $Diff.AvailableDelta.Count)
        foreach ($entry in ($Diff.AvailableDelta | Sort-Object Id)) {
            Write-Host ("{0}  [{1}]" -f $entry.Id, $entry.WingetSource)
            Write-Host ("    winget avail : {0}" -f $entry.WingetAvail)
            Write-Host ("    $Label avail : {0}" -f $entry.PingetAvail)
        }
    }

    if ($Diff.DuplicateId.Count -gt 0) {
        Write-Section ("$Label-ONLY DUPLICATE ID ({0})" -f $Diff.DuplicateId.Count)
        foreach ($entry in ($Diff.DuplicateId | Sort-Object Id)) {
            Write-Host ("{0}  ({1}x)" -f $entry.Id, $entry.Occurrences)
            foreach ($row in $entry.Rows) {
                Write-Host ("    localId={0}  version={1}  source={2}" -f $row.LocalId, $row.InstalledVersion, $row.SourceName)
            }
        }
    }

    if ($Diff.SharedDuplicateId.Count -gt 0) {
        Write-Section ("SHARED DUPLICATE ID IN WINGET AND $Label ({0})" -f $Diff.SharedDuplicateId.Count)
        foreach ($entry in ($Diff.SharedDuplicateId | Sort-Object Id)) {
            Write-Host ("{0}  ({1}x in $Label)" -f $entry.Id, $entry.Occurrences)
        }
    }

    if ($Diff.OnlyInWinget.Count -gt 0) {
        Write-Section ("ONLY IN WINGET VS $Label ({0})" -f $Diff.OnlyInWinget.Count)
        foreach ($entry in ($Diff.OnlyInWinget | Sort-Object Id)) {
            Write-Host ("{0,-50} {1,-25} [{2}]" -f $entry.Id, $entry.Version, $entry.Source)
        }
    }

    if ($Diff.OnlyInPinget.Count -gt 0) {
        Write-Section ("ONLY IN $Label VS WINGET ({0})" -f $Diff.OnlyInPinget.Count)
        foreach ($entry in ($Diff.OnlyInPinget | Sort-Object Id)) {
            Write-Host ("{0,-50} {1,-25} [{2}]" -f $entry.Id, $entry.InstalledVersion, $entry.SourceName)
        }
    }
}

function Write-ImplementationDriftReport {
    param($Diff)

    $left = $Diff.LeftLabel
    $right = $Diff.RightLabel
    Write-Section "$left vs $right Implementation Drift"
    Write-Host ("Matching          : {0}" -f $Diff.Matching.Count)
    Write-Host ("MetadataMismatch  : {0}" -f $Diff.MetadataMismatch.Count)
    Write-Host ("OnlyIn$left       : {0}" -f $Diff.OnlyInLeft.Count)
    Write-Host ("OnlyIn$right      : {0}" -f $Diff.OnlyInRight.Count)
    Write-Host ("DuplicateIn$left  : {0}" -f $Diff.DuplicateLeft.Count)
    Write-Host ("DuplicateIn$right : {0}" -f $Diff.DuplicateRight.Count)

    if ($Diff.MetadataMismatch.Count -gt 0) {
        Write-Section ("$left vs $right METADATA MISMATCH ({0})" -f $Diff.MetadataMismatch.Count)
        foreach ($entry in ($Diff.MetadataMismatch | Sort-Object Name, Id)) {
            Write-Host ("{0}  [{1}]" -f ($entry.Name ?? $entry.LocalId), $entry.Id)
            foreach ($field in $entry.Fields) {
                $leftValue = $field.PSObject.Properties[$left].Value
                $rightValue = $field.PSObject.Properties[$right].Value
                Write-Host ("    {0}: {1}={2}  {3}={4}" -f $field.Field, $left, $leftValue, $right, $rightValue)
            }
        }
    }

    if ($Diff.OnlyInLeft.Count -gt 0) {
        Write-Section ("ONLY IN $left VS $right ({0})" -f $Diff.OnlyInLeft.Count)
        foreach ($entry in ($Diff.OnlyInLeft | Sort-Object Name, Id)) {
            Write-Host ("{0,-50} {1,-25} [{2}]" -f $entry.Id, $entry.InstalledVersion, $entry.SourceName)
        }
    }

    if ($Diff.OnlyInRight.Count -gt 0) {
        Write-Section ("ONLY IN $right VS $left ({0})" -f $Diff.OnlyInRight.Count)
        foreach ($entry in ($Diff.OnlyInRight | Sort-Object Name, Id)) {
            Write-Host ("{0,-50} {1,-25} [{2}]" -f $entry.Id, $entry.InstalledVersion, $entry.SourceName)
        }
    }
}

# ---------------------------------------------------------------------------
# Machine snapshot
# ---------------------------------------------------------------------------

function Get-ToolVersion {
    param([string]$Executable)
    try { (& $Executable --version 2>&1 | Select-Object -First 1).Trim() } catch { "unavailable: $($_.Exception.Message)" }
}

function Get-SystemWingetAppRoot {
    if (-not $IsWindows) {
        throw "-UseSystemWingetAppRoot requires Windows."
    }

    return Join-Path $env:LOCALAPPDATA "Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState"
}

function Get-MachineSnapshot {
    param([string]$PingetCs, [string]$PingetRust, [string]$Winget, [string]$PingetAppRoot)
    [pscustomobject]@{
        CapturedAt          = (Get-Date).ToUniversalTime().ToString("o")
        OSVersion           = [System.Environment]::OSVersion.VersionString
        OSArchitecture      = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        WingetVersion       = Get-ToolVersion $Winget
        PingetCsVersion     = Get-ToolVersion $PingetCs
        PingetRustVersion   = Get-ToolVersion $PingetRust
        PingetAppRoot       = $PingetAppRoot
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if (-not [string]::IsNullOrWhiteSpace($Pinget)) {
    $PingetCs = $Pinget
}

if ($UseSystemWingetAppRoot -and -not [string]::IsNullOrWhiteSpace($PingetAppRoot)) {
    throw "Use either -UseSystemWingetAppRoot or -PingetAppRoot, not both."
}

if ($UseSystemWingetAppRoot) {
    $PingetAppRoot = Get-SystemWingetAppRoot
}

if (-not [string]::IsNullOrWhiteSpace($PingetAppRoot)) {
    $env:PINGET_APPROOT = $PingetAppRoot
}

$machine = Get-MachineSnapshot -PingetCs $PingetCs -PingetRust $PingetRust -Winget $Winget -PingetAppRoot $env:PINGET_APPROOT
Write-Section "Machine"
$machine | Format-List | Out-String | Write-Host

if ($UpdateSources) {
    Write-Host "Updating sources..."
    & $Winget source update --accept-source-agreements --disable-interactivity 2>&1 | Out-Null
    & $PingetCs source update 2>&1 | Out-Null
    & $PingetRust source update 2>&1 | Out-Null
}

Write-Section "Capturing winget list"
$wingetResult = Get-WingetListRows -Executable $Winget -IncludeUnknown:$IncludeUnknown
Write-Host ("winget exit={0}, parsed {1} row(s)" -f $wingetResult.ExitCode, $wingetResult.Rows.Count)
if ($wingetResult.Diagnostic) { Write-Warning $wingetResult.Diagnostic }

Write-Section "Capturing C# pinget list"
$csResult = Get-PingetListRows -Executable $PingetCs -IncludeUnknown:$IncludeUnknown
Write-Host ("C# pinget exit={0}, parsed {1} row(s)" -f $csResult.ExitCode, $csResult.Rows.Count)
if ($csResult.Diagnostic) { Write-Warning $csResult.Diagnostic }

Write-Section "Capturing Rust pinget list"
$rustResult = Get-PingetListRows -Executable $PingetRust -IncludeUnknown:$IncludeUnknown
Write-Host ("Rust pinget exit={0}, parsed {1} row(s)" -f $rustResult.ExitCode, $rustResult.Rows.Count)
if ($rustResult.Diagnostic) { Write-Warning $rustResult.Diagnostic }

$csDiff = Compare-ListRows -WingetRows $wingetResult.Rows -PingetRows $csResult.Rows
$rustDiff = Compare-ListRows -WingetRows $wingetResult.Rows -PingetRows $rustResult.Rows
$implementationDiff = Compare-PingetImplementations -LeftLabel "Rust" -LeftRows $rustResult.Rows -RightLabel "CSharp" -RightRows $csResult.Rows

Write-WingetParityReport -Label "CSharp" -Diff $csDiff
Write-WingetParityReport -Label "Rust" -Diff $rustDiff
Write-ImplementationDriftReport -Diff $implementationDiff

$csFailCount = Get-WingetParityFailureCount $csDiff
$rustFailCount = Get-WingetParityFailureCount $rustDiff
$implementationFailCount = Get-ImplementationDriftFailureCount $implementationDiff
$failCount = $csFailCount + $rustFailCount + $implementationFailCount
$parserBroken = [bool]$wingetResult.Diagnostic -or [bool]$csResult.Diagnostic -or [bool]$rustResult.Diagnostic
$verdict = if ($failCount -eq 0 -and -not $parserBroken) { "PASS" } else { "FAIL" }
Write-Section "Result: $verdict ($failCount non-cosmetic differences)"
Write-Host ("CSharp vs winget : {0}" -f $csFailCount)
Write-Host ("Rust vs winget   : {0}" -f $rustFailCount)
Write-Host ("Rust vs CSharp   : {0}" -f $implementationFailCount)

if ($FixturePath) {
    $fixture = [ordered]@{
        schema     = "pinget-parity/list/v2"
        machine    = $machine
        invocation = [ordered]@{
            pingetCs       = $PingetCs
            pingetRust     = $PingetRust
            winget         = $Winget
            pingetAppRoot  = $env:PINGET_APPROOT
            useSystemWingetAppRoot = [bool]$UseSystemWingetAppRoot
            includeUnknown = [bool]$IncludeUnknown
        }
        winget     = [ordered]@{ exitCode = $wingetResult.ExitCode; rows = $wingetResult.Rows; raw = $wingetResult.Raw }
        pingetCs   = [ordered]@{ exitCode = $csResult.ExitCode; rows = $csResult.Rows; raw = $csResult.Raw }
        pingetRust = [ordered]@{ exitCode = $rustResult.ExitCode; rows = $rustResult.Rows; raw = $rustResult.Raw }
        diff       = [ordered]@{
            csharpVsWinget = [ordered]@{
                correlationMiss = $csDiff.CorrelationMiss
                versionMismatch = $csDiff.VersionMismatch
                sourceMismatch  = $csDiff.SourceMismatch
                availableDelta  = $csDiff.AvailableDelta
                duplicateId     = $csDiff.DuplicateId
                sharedDuplicateId = $csDiff.SharedDuplicateId
                onlyInWinget    = $csDiff.OnlyInWinget
                onlyInPinget    = $csDiff.OnlyInPinget
                matching        = $csDiff.Matching.Count
            }
            rustVsWinget = [ordered]@{
                correlationMiss = $rustDiff.CorrelationMiss
                versionMismatch = $rustDiff.VersionMismatch
                sourceMismatch  = $rustDiff.SourceMismatch
                availableDelta  = $rustDiff.AvailableDelta
                duplicateId     = $rustDiff.DuplicateId
                sharedDuplicateId = $rustDiff.SharedDuplicateId
                onlyInWinget    = $rustDiff.OnlyInWinget
                onlyInPinget    = $rustDiff.OnlyInPinget
                matching        = $rustDiff.Matching.Count
            }
            rustVsCsharp = [ordered]@{
                metadataMismatch = $implementationDiff.MetadataMismatch
                onlyInRust       = $implementationDiff.OnlyInLeft
                onlyInCSharp     = $implementationDiff.OnlyInRight
                duplicateInRust  = $implementationDiff.DuplicateLeft
                duplicateInCSharp = $implementationDiff.DuplicateRight
                matching         = $implementationDiff.Matching.Count
            }
        }
    }
    $fixture | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $FixturePath -Encoding UTF8
    Write-Host "Fixture written to: $FixturePath"
}

if ($FailOnDiff -and ($failCount -gt 0 -or $parserBroken)) {
    throw "compare-list: $failCount non-cosmetic differences found."
}
