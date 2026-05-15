#requires -Version 7.0
<#
.SYNOPSIS
    Asserts that `pinget upgrade` reports the same set of upgrades as
    `winget upgrade` on the current machine.

.DESCRIPTION
    Runs both tools against the live system state, parses their output into
    a normalized row-set keyed by (PackageId, Source), and reports a
    structured diff: rows that winget surfaces but pinget doesn't (missing),
    rows that pinget surfaces but winget doesn't (extra), and rows that
    appear in both but disagree on installed/available version
    (version mismatch).

    Rows that share the same (id, source, installed_version, available_version)
    are considered semantically equal even if the Name column renders
    differently (winget resolves MSIX `ms-resource:` placeholders and reads
    marketing versions from manifest data; pinget shows the raw ARP values).
    Such display-only differences are reported as advisories, not failures.

    The fixture written to disk is the input to a future cross-machine corpus
    — re-run this on different machines (different installs, different
    catalog states) and compare the JSON outputs to find correlation classes
    pinget still gets wrong. The script is intentionally read-only: it never
    mutates installs, sources, or pins.

.PARAMETER Pinget
    Path to the pinget executable. Defaults to the release build in the
    rust target tree.

.PARAMETER Winget
    Path to the winget executable. Defaults to `winget` on PATH.

.PARAMETER FixturePath
    If set, writes a JSON fixture describing the machine, both raw outputs,
    and the computed diff. Intended for sharing across machines.

.PARAMETER IncludeUnknown
    Pass `--include-unknown` to both tools. On by default; use
    `-IncludeUnknown:$false` to test the stricter default behavior.

.PARAMETER FailOnDiff
    Exit non-zero when any non-cosmetic difference is found. Intended for
    CI use; omit for interactive diagnosis.

.PARAMETER UpdateSources
    Run `source update` against both tools before diffing. Off by default
    to keep the harness side-effect-free.

.EXAMPLE
    .\Test-UpgradeParity.ps1
    # Quick interactive check.

.EXAMPLE
    .\Test-UpgradeParity.ps1 -FixturePath ./parity.json -FailOnDiff
    # Save a fixture for sharing and fail the run on any real diff.
#>
param(
    [string]$Pinget = (Join-Path $PSScriptRoot "..\rust\target\release\pinget.exe"),
    [string]$Winget = "winget",
    [string]$FixturePath,
    [bool]$IncludeUnknown = $true,
    [switch]$FailOnDiff,
    [switch]$UpdateSources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Assert-Path {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -Path $Path)) {
        throw "$Description not found at '$Path'."
    }
}

function Invoke-CaptureLines {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [Parameter(Mandatory = $true)] [string[]]$Arguments
    )

    # winget emits progress spinners on stderr that we don't want polluting
    # the parsed table. Capture both streams (so we can attribute failures)
    # but the parsers below ignore anything that doesn't look like a data row.
    $stdoutLines = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exit = $LASTEXITCODE
    [pscustomobject]@{
        ExitCode = $exit
        Lines    = @($stdoutLines)
    }
}

function Get-WingetUpgradeRows {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [bool]$IncludeUnknown
    )

    $args = @("upgrade")
    if ($IncludeUnknown) { $args += "--include-unknown" }
    $args += @("--accept-source-agreements", "--disable-interactivity")
    $captured = Invoke-CaptureLines -Executable $Executable -Arguments $args

    # winget upgrade's table looks like:
    #
    #   Name                          Id                Version    Available    Source
    #   ------------------------------------------------------------------------------
    #   JetBrains Rider 2025.3.0.1    JetBrains.Rider   2025.3.0.1 2026.1.1     winget
    #   ...
    #   11 upgrades available.
    #
    # Column starts are fixed-width offsets implied by where each column's
    # header word begins in the header line — we can't split on whitespace
    # because the Name column commonly contains spaces. Find the header by
    # locating the dash separator, then derive the offsets from the line
    # immediately above it.

    $separatorIdx = -1
    for ($i = 0; $i -lt $captured.Lines.Count; $i++) {
        $line = $captured.Lines[$i]
        if ($line -match '^-{10,}$') {
            $separatorIdx = $i
            break
        }
    }
    if ($separatorIdx -lt 1) {
        # No table at all. winget prints "No installed package found matching
        # input criteria." or "No applicable upgrade found." when nothing is
        # upgradable; that's a legitimate empty state, not a parser failure.
        $emptyState = ($captured.Lines | Where-Object {
            $_ -match 'No installed package|No applicable upgrade|No newer package versions'
        }).Count -gt 0
        return @{
            Rows = @()
            ExitCode = $captured.ExitCode
            Raw = $captured.Lines
            Diagnostic = if ($emptyState) { $null } else { "no header/separator found — winget output did not include an upgrade table" }
        }
    }

    $header = $captured.Lines[$separatorIdx - 1]
    $columnDefs = @(
        @{ Key = 'Name';      Token = 'Name' },
        @{ Key = 'Id';        Token = 'Id' },
        @{ Key = 'Version';   Token = 'Version' },
        @{ Key = 'Available'; Token = 'Available' },
        @{ Key = 'Source';    Token = 'Source' }
    )
    $offsets = [ordered]@{}
    foreach ($column in $columnDefs) {
        $pos = $header.IndexOf($column.Token)
        if ($pos -lt 0) {
            return @{
                Rows = @()
                ExitCode = $captured.ExitCode
                Raw = $captured.Lines
                Diagnostic = "header missing column '$($column.Token)'"
            }
        }
        $offsets[$column.Key] = $pos
    }

    function Get-Slice {
        param([string]$Line, [int]$Start, [int]$End)
        if ($Start -ge $Line.Length) { return "" }
        $effectiveEnd = [Math]::Min($End, $Line.Length)
        return $Line.Substring($Start, $effectiveEnd - $Start).TrimEnd().TrimStart()
    }

    $rows = New-Object System.Collections.Generic.List[object]
    $orderedKeys = @($offsets.Keys)
    for ($i = $separatorIdx + 1; $i -lt $captured.Lines.Count; $i++) {
        $line = $captured.Lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '^\d+ (upgrade|package|available)') { break }
        # Skip "The following packages cannot be upgraded" footer banners that
        # winget prints when --include-unknown surfaces packages without a
        # known installed version. They precede a separate table we don't
        # parse here.
        if ($line -match '^The following packages') { break }
        # Defensive: ignore lines that are just spinner/dash noise.
        if ($line -match '^[\s\\/\-|]+$') { continue }

        $row = [ordered]@{}
        for ($c = 0; $c -lt $orderedKeys.Count; $c++) {
            $key = $orderedKeys[$c]
            $start = $offsets[$key]
            $end = if ($c + 1 -lt $orderedKeys.Count) { $offsets[$orderedKeys[$c + 1]] } else { [int]::MaxValue }
            $row[$key] = Get-Slice -Line $line -Start $start -End $end
        }

        # A real upgrade row always has an Id and a Source. Rows from
        # `winget list` could omit Source for uncorrelated installs, but
        # `winget upgrade` filters those out — so a missing Id or Source
        # means we mis-parsed and should skip rather than emit a phantom row.
        if (-not $row.Id -or -not $row.Source) { continue }

        $rows.Add([pscustomobject]$row) | Out-Null
    }

    # Sanity check: if the separator was followed by many non-empty,
    # non-footer lines but we parsed zero rows, the column-offset logic
    # almost certainly drifted (e.g. winget renamed a header). Surface that
    # loudly instead of silently reporting PASS with 0 rows.
    if ($rows.Count -eq 0) {
        $dataLineCount = 0
        for ($i = $separatorIdx + 1; $i -lt $captured.Lines.Count; $i++) {
            $line = $captured.Lines[$i]
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -match '^\d+ (upgrade|package|available)') { break }
            if ($line -match '^The following packages') { break }
            $dataLineCount++
        }
        if ($dataLineCount -gt 0) {
            return @{
                Rows = @()
                ExitCode = $captured.ExitCode
                Raw = $captured.Lines
                Diagnostic = "winget table parsed 0 rows but $dataLineCount data-shaped lines followed the separator — column offsets may have drifted"
            }
        }
    }

    return @{
        Rows = $rows.ToArray()
        ExitCode = $captured.ExitCode
        Raw = $captured.Lines
        Diagnostic = $null
    }
}

function Get-PingetUpgradeRows {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [bool]$IncludeUnknown
    )

    $args = @("upgrade", "--output", "json")
    if ($IncludeUnknown) { $args += "--include-unknown" }
    $captured = Invoke-CaptureLines -Executable $Executable -Arguments $args
    $joined = $captured.Lines -join "`n"

    try {
        $parsed = $joined | ConvertFrom-Json -Depth 32 -ErrorAction Stop
    } catch {
        return @{
            Rows = @()
            ExitCode = $captured.ExitCode
            Raw = $captured.Lines
            Diagnostic = "pinget JSON parse failed: $($_.Exception.Message)"
        }
    }

    function Get-MatchField {
        param([Parameter(Mandatory = $true)] $InputObject, [string[]]$Names)
        foreach ($n in $Names) {
            $prop = $InputObject.PSObject.Properties[$n]
            if ($null -ne $prop) { return $prop.Value }
        }
        return $null
    }

    $rows = foreach ($match in @($parsed.matches)) {
        # The Rust CLI emits snake_case keys (installed_version, ...) while
        # the C# CLI emits camelCase (installedVersion, ...). The harness has
        # to talk to either tool, so accept both casings on every field.
        $installed = Get-MatchField $match @('installed_version', 'installedVersion')
        $available = Get-MatchField $match @('available_version', 'availableVersion')
        $sourceName = Get-MatchField $match @('source_name', 'sourceName')
        [pscustomobject]@{
            Name      = Get-MatchField $match @('name')
            Id        = Get-MatchField $match @('id')
            Version   = if ($null -ne $installed) { $installed } else { "" }
            Available = if ($null -ne $available) { $available } else { "" }
            Source    = if ($null -ne $sourceName) { $sourceName } else { "" }
        }
    }

    return @{
        Rows = @($rows)
        ExitCode = $captured.ExitCode
        Raw = $captured.Lines
        Diagnostic = $null
    }
}

function Get-RowKey {
    param([pscustomobject]$Row)
    return "{0}|{1}" -f $Row.Id.ToLowerInvariant(), $Row.Source.ToLowerInvariant()
}

function Test-NamesEquivalent {
    param(
        [Parameter(Mandatory = $true)] [string]$WingetName,
        [Parameter(Mandatory = $true)] [string]$PingetName
    )

    if ($WingetName -eq $PingetName) { return $true }

    # winget truncates the Name column at its fixed display width and marks
    # the cut with a U+2026 horizontal ellipsis. That's a rendering artifact
    # of the table layout, not a real difference between the two tools — if
    # the un-truncated pinget name starts with the visible prefix, treat as
    # identical so the cosmetic-diff bucket only flags substantive
    # rendering disagreements (resource-string placeholders, marketing
    # version vs raw ARP, etc.).
    $ellipsis = [char]0x2026
    if ($WingetName.EndsWith($ellipsis)) {
        $prefix = $WingetName.Substring(0, $WingetName.Length - 1)
        if ($PingetName.StartsWith($prefix)) { return $true }
    }
    return $false
}

function Compare-UpgradeRows {
    param(
        [Parameter(Mandatory = $true)] [object[]]$WingetRows,
        [Parameter(Mandatory = $true)] [object[]]$PingetRows
    )

    $wingetByKey = @{}
    foreach ($row in $WingetRows) { $wingetByKey[(Get-RowKey -Row $row)] = $row }

    $pingetByKey = @{}
    foreach ($row in $PingetRows) { $pingetByKey[(Get-RowKey -Row $row)] = $row }

    $missing      = New-Object System.Collections.Generic.List[object]
    $extra        = New-Object System.Collections.Generic.List[object]
    $versionDiff  = New-Object System.Collections.Generic.List[object]
    $cosmeticOnly = New-Object System.Collections.Generic.List[object]
    $matching     = New-Object System.Collections.Generic.List[object]

    foreach ($key in $wingetByKey.Keys) {
        $w = $wingetByKey[$key]
        if (-not $pingetByKey.ContainsKey($key)) {
            $missing.Add($w) | Out-Null
            continue
        }
        $p = $pingetByKey[$key]
        if ($w.Version -ne $p.Version -or $w.Available -ne $p.Available) {
            $versionDiff.Add([pscustomobject]@{
                Id              = $w.Id
                Source          = $w.Source
                WingetVersion   = $w.Version
                PingetVersion   = $p.Version
                WingetAvailable = $w.Available
                PingetAvailable = $p.Available
            }) | Out-Null
        } elseif ((Test-NamesEquivalent -WingetName $w.Name -PingetName $p.Name)) {
            $matching.Add($w) | Out-Null
        } elseif ($w.Name -ne $p.Name) {
            # Same id, same versions, but rendered Name disagrees. Almost
            # always cosmetic (resource-string MSIX display, marketing
            # version in JetBrains DisplayName, column truncation). Surface
            # for visibility but don't count toward the failure verdict.
            $cosmeticOnly.Add([pscustomobject]@{
                Id         = $w.Id
                Source     = $w.Source
                WingetName = $w.Name
                PingetName = $p.Name
            }) | Out-Null
        } else {
            $matching.Add($w) | Out-Null
        }
    }

    foreach ($key in $pingetByKey.Keys) {
        if (-not $wingetByKey.ContainsKey($key)) {
            $extra.Add($pingetByKey[$key]) | Out-Null
        }
    }

    return [pscustomobject]@{
        Missing      = $missing.ToArray()
        Extra        = $extra.ToArray()
        VersionDiff  = $versionDiff.ToArray()
        CosmeticOnly = $cosmeticOnly.ToArray()
        Matching     = $matching.ToArray()
    }
}

function Get-MachineSnapshot {
    param(
        [string]$Pinget,
        [string]$Winget
    )

    $wingetVersion = try { (& $Winget --version 2>&1 | Select-Object -First 1) } catch { "unavailable" }
    $pingetVersion = try { (& $Pinget --version 2>&1 | Select-Object -First 1) } catch { "unavailable" }

    [pscustomobject]@{
        CapturedAt    = (Get-Date).ToUniversalTime().ToString("o")
        OSVersion     = [System.Environment]::OSVersion.VersionString
        OSArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        ProcessArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        WingetVersion = "$wingetVersion".Trim()
        PingetVersion = "$pingetVersion".Trim()
    }
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 80)
    Write-Host $Title
    Write-Host ("=" * 80)
}

function Format-Row {
    param([pscustomobject]$Row)
    return "{0,-40} {1,-22} -> {2,-22}  [{3}]" -f $Row.Id, $Row.Version, $Row.Available, $Row.Source
}

# --- main -----------------------------------------------------------------

Assert-Path -Path $Pinget -Description "pinget binary"

if ($UpdateSources) {
    Write-Host "Updating sources on both tools..."
    & $Winget source update --accept-source-agreements --disable-interactivity | Out-Null
    & $Pinget source update | Out-Null
}

$machine = Get-MachineSnapshot -Pinget $Pinget -Winget $Winget
Write-Section "Machine"
$machine | Format-List | Out-String | Write-Host

Write-Section "Capturing winget upgrade"
$wingetResult = Get-WingetUpgradeRows -Executable $Winget -IncludeUnknown:$IncludeUnknown
Write-Host ("winget exit={0}, parsed {1} row(s)" -f $wingetResult.ExitCode, $wingetResult.Rows.Count)
if ($wingetResult.Diagnostic) {
    Write-Warning $wingetResult.Diagnostic
}

Write-Section "Capturing pinget upgrade"
$pingetResult = Get-PingetUpgradeRows -Executable $Pinget -IncludeUnknown:$IncludeUnknown
Write-Host ("pinget exit={0}, parsed {1} row(s)" -f $pingetResult.ExitCode, $pingetResult.Rows.Count)
if ($pingetResult.Diagnostic) {
    Write-Warning $pingetResult.Diagnostic
}

$diff = Compare-UpgradeRows -WingetRows $wingetResult.Rows -PingetRows $pingetResult.Rows

Write-Section "Verdict"
Write-Host ("Matching rows : {0}" -f $diff.Matching.Count)
Write-Host ("Missing       : {0}  (winget reports, pinget does not)" -f $diff.Missing.Count)
Write-Host ("Extra         : {0}  (pinget reports, winget does not)" -f $diff.Extra.Count)
Write-Host ("Version diff  : {0}  (both report; versions disagree)" -f $diff.VersionDiff.Count)
Write-Host ("Cosmetic only : {0}  (same id/versions, different display name)" -f $diff.CosmeticOnly.Count)

if ($diff.Missing.Count -gt 0) {
    Write-Section "MISSING — winget shows, pinget does not"
    $diff.Missing | ForEach-Object { Write-Host (Format-Row -Row $_) }
}
if ($diff.Extra.Count -gt 0) {
    Write-Section "EXTRA — pinget shows, winget does not"
    $diff.Extra | ForEach-Object { Write-Host (Format-Row -Row $_) }
}
if ($diff.VersionDiff.Count -gt 0) {
    Write-Section "VERSION MISMATCH — same id, different versions"
    foreach ($entry in $diff.VersionDiff) {
        Write-Host ("{0}  [{1}]" -f $entry.Id, $entry.Source)
        Write-Host ("    winget: {0,-22} -> {1}" -f $entry.WingetVersion, $entry.WingetAvailable)
        Write-Host ("    pinget: {0,-22} -> {1}" -f $entry.PingetVersion, $entry.PingetAvailable)
    }
}
if ($diff.CosmeticOnly.Count -gt 0) {
    Write-Section "COSMETIC — same id/versions, different display name"
    foreach ($entry in $diff.CosmeticOnly) {
        Write-Host ("{0}  [{1}]" -f $entry.Id, $entry.Source)
        Write-Host ("    winget: {0}" -f $entry.WingetName)
        Write-Host ("    pinget: {0}" -f $entry.PingetName)
    }
}

$failCount = $diff.Missing.Count + $diff.Extra.Count + $diff.VersionDiff.Count
# Parser diagnostics (broken winget table layout, malformed pinget JSON)
# must be treated as failures — otherwise the harness reports a spurious
# PASS the day winget's column headers change.
$parserBroken = [bool]$wingetResult.Diagnostic -or [bool]$pingetResult.Diagnostic
$verdict = if ($failCount -eq 0 -and -not $parserBroken) { "PASS" } else { "FAIL" }

Write-Section "Result: $verdict"

if ($FixturePath) {
    $fixture = [ordered]@{
        schema       = "pinget-parity/upgrade/v1"
        machine      = $machine
        invocation   = [ordered]@{
            pinget         = (Resolve-Path -LiteralPath $Pinget -ErrorAction SilentlyContinue)?.Path
            winget         = $Winget
            includeUnknown = [bool]$IncludeUnknown
            updateSources  = [bool]$UpdateSources
        }
        winget       = [ordered]@{
            exitCode = $wingetResult.ExitCode
            rows     = $wingetResult.Rows
            raw      = $wingetResult.Raw
        }
        pinget       = [ordered]@{
            exitCode = $pingetResult.ExitCode
            rows     = $pingetResult.Rows
            raw      = $pingetResult.Raw
        }
        diff         = [ordered]@{
            matchingCount = $diff.Matching.Count
            missing       = $diff.Missing
            extra         = $diff.Extra
            versionDiff   = $diff.VersionDiff
            cosmeticOnly  = $diff.CosmeticOnly
        }
        verdict      = $verdict
    }
    $fixture | ConvertTo-Json -Depth 32 | Set-Content -Path $FixturePath -Encoding utf8
    Write-Host "Fixture written to: $FixturePath"
}

if ($FailOnDiff -and $verdict -ne "PASS") {
    exit 1
}
