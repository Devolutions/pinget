param(
    [string]$RustWinget = (Join-Path $PSScriptRoot "..\target\debug\pinget.exe"),
    [string]$DotnetWinget = (Join-Path $PSScriptRoot "..\..\dotnet\src\Devolutions.Pinget.Cli\bin\Debug\net10.0\pinget.exe"),
    [string]$SystemWinget = "winget",
    [string[]]$Cases,
    [switch]$NoDotnet,
    [switch]$NoRust,
    [switch]$NoSystem,
    [switch]$UpdateSources,
    [switch]$FailOnDiff
)

$script:HadDifference = $false

$defaultCases = @(
    @{
        Name = "search-powertoys"
        Args = @("search", "PowerToys", "--count", "5", "--source", "winget")
    },
    @{
        Name = "search-tag-terminal"
        Args = @("search", "--tag", "terminal", "--count", "5", "--source", "winget")
    },
    @{
        Name = "search-exact-id"
        Args = @("search", "--id", "Microsoft.PowerToys", "--exact", "--source", "winget")
    },
    @{
        Name = "search-by-name"
        Args = @("search", "--name", "Remote Desktop Manager", "--count", "3", "--source", "winget")
    },
    @{
        Name = "search-versions"
        Args = @("search", "Microsoft.PowerToys", "--versions", "--source", "winget")
    },
    @{
        Name = "show-powertoys"
        Args = @("show", "Microsoft.PowerToys", "--installer-type", "exe", "--architecture", "x64", "--locale", "en-US", "--source", "winget")
    },
    @{
        Name = "show-rdm"
        Args = @("show", "Devolutions.RemoteDesktopManager", "--source", "winget")
    },
    @{
        Name = "show-versions"
        Args = @("show", "Microsoft.PowerToys", "--versions", "--source", "winget")
    },
    @{
        Name = "show-exact-version"
        Args = @("show", "Microsoft.PowerToys", "--version", "0.87.1", "--source", "winget")
    },
    @{
        Name = "list-count"
        Args = @("list", "--count", "5")
    },
    @{
        Name = "list-powertoys"
        Args = @("list", "Microsoft.PowerToys")
    },
    @{
        Name = "list-upgrade-available"
        Args = @("list", "--upgrade-available", "--count", "3")
    },
    @{
        Name = "upgrade-lazygit"
        Args = @("list", "JesseDuffield.lazygit", "--upgrade-available")
        RustArgs = @("upgrade", "JesseDuffield.lazygit")
    },
    @{
        Name = "version"
        Args = @("--version")
        CompareMode = "exit-code-only"
    },
    @{
        Name = "source-list"
        Args = @("source", "list")
        CompareMode = "exit-code-only"
    },
    @{
        Name = "error-e-fail"
        Args = @("error", "0x80004005")
    },
    @{
        Name = "search-json"
        Args = @("search", "Microsoft.PowerToys", "--count", "1")
        RustArgs = @("search", "Microsoft.PowerToys", "--count", "1", "--output", "json")
        CompareMode = "rust-only"
    },
    @{
        Name = "source-roundtrip"
        Args = @("source", "add", "test-parity", "https://example.com/test", "--type", "rest")
        CompareMode = "rust-only"
        PostSteps = @(
            @{ Args = @("source", "list"); Label = "after-add" },
            @{ Args = @("source", "remove", "test-parity"); Label = "remove" },
            @{ Args = @("source", "list"); Label = "after-remove" }
        )
    },
    @{
        Name = "pin-roundtrip"
        Args = @("pin", "add", "Microsoft.PowerToys", "--version", "0.70.0")
        CompareMode = "rust-only"
        PostSteps = @(
            @{ Args = @("pin", "list"); Label = "after-add" },
            @{ Args = @("pin", "remove", "Microsoft.PowerToys"); Label = "remove" },
            @{ Args = @("pin", "list"); Label = "after-remove" }
        )
    }
)

if (-not $NoRust -and -not (Test-Path $RustWinget)) {
    Write-Warning "Rust winget binary not found at '$RustWinget'. Use -NoRust to skip."
    $NoRust = $true
}
if (-not $NoDotnet -and -not (Test-Path $DotnetWinget)) {
    Write-Warning "Dotnet winget binary not found at '$DotnetWinget'. Use -NoDotnet to skip."
    $NoDotnet = $true
}

function Invoke-WingetCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $lines = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        RawLines = @($lines)
        NormalizedLines = @(Format-WingetOutput -Lines $lines)
    }
}

function Invoke-WingetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    Write-Host "Updating sources for $Label..." -ForegroundColor DarkGray
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Label source update failed with exit code $LASTEXITCODE"
    }
}

function Format-WingetOutput {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    foreach ($line in $Lines) {
        $trimmed = $line.TrimEnd()
        if (-not $trimmed) {
            continue
        }
        if ($trimmed -match '^[\s\\/\-|]+$') {
            continue
        }
        if ($trimmed -like 'Failed in attempting to update the source:*') {
            continue
        }
        if ($trimmed -like 'Failed when searching source*') {
            continue
        }
        if ($trimmed -like 'warning: Failed when searching source*') {
            continue
        }
        if ($trimmed -like 'warning:*REST search request failed*') {
            continue
        }
        if ($trimmed -match '[█▒]' -and $trimmed -match '\d+(?:\.\d+)?\s*(?:KB|MB|GB)\s*/\s*\d+(?:\.\d+)?\s*(?:KB|MB|GB)') {
            continue
        }
        if ($trimmed -match '[█▒]' -and $trimmed -match '\d{1,3}%$') {
            continue
        }
        ($trimmed -replace '\s+', ' ')
    }
}

function Select-CaseSet {
    param(
        [string[]]$RequestedCases
    )

    if (-not $RequestedCases -or $RequestedCases.Count -eq 0) {
        return $defaultCases
    }

    $selected = foreach ($requested in $RequestedCases) {
        $match = $defaultCases | Where-Object { $_.Name -eq $requested }
        if (-not $match) {
            throw "Unknown case '$requested'. Available cases: $($defaultCases.Name -join ', ')"
        }
        $match
    }

    return @($selected)
}

if ($UpdateSources) {
    if (-not $NoRust) {
        Invoke-WingetCommand -Executable $RustWinget -Arguments @("source", "update") -Label "Rust"
    }
    if (-not $NoDotnet) {
        Invoke-WingetCommand -Executable $DotnetWinget -Arguments @("source", "update") -Label "Dotnet"
    }
    if (-not $NoSystem) {
        Invoke-WingetCommand -Executable $SystemWinget -Arguments @("source", "update") -Label "System"
    }
}

function Write-CaseReport {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Case,
        $RustResult,
        $DotnetResult,
        $SystemResult,
        [string]$CompareMode = "full"
    )

    $commandText = ($Case.Args | ForEach-Object {
            if ($_ -match '\s') {
                '"' + $_ + '"'
            } else {
                $_
            }
        }) -join ' '

    # Collect all results that were run
    $results = [ordered]@{}
    if ($RustResult)   { $results["RUST"]   = $RustResult }
    if ($DotnetResult) { $results["DOTNET"] = $DotnetResult }
    if ($SystemResult) { $results["SYSTEM"] = $SystemResult }

    if ($results.Count -lt 2) {
        # Only one tool — just display it
        $toolName = $results.Keys | Select-Object -First 1
        $r = $results[$toolName]
        Write-Host ("=" * 80)
        Write-Host ("CASE   : {0}" -f $Case.Name)
        Write-Host ("COMMAND: winget {0}" -f $commandText)
        Write-Host ("STATUS : {0}-ONLY (exit={1})" -f $toolName, $r.ExitCode)
        Write-Host "--- $toolName ---"
        $r.NormalizedLines | ForEach-Object { Write-Host $_ }
        return
    }

    # Compare all pairs
    $allMatch = $true
    $keys = @($results.Keys)
    for ($i = 0; $i -lt $keys.Count; $i++) {
        for ($j = $i + 1; $j -lt $keys.Count; $j++) {
            $a = $results[$keys[$i]]
            $b = $results[$keys[$j]]
            if ($CompareMode -eq "exit-code-only") {
                if ($a.ExitCode -ne $b.ExitCode) { $allMatch = $false }
            } else {
                if ($a.ExitCode -ne $b.ExitCode -or
                    (@($a.NormalizedLines) -join "`n") -ne (@($b.NormalizedLines) -join "`n")) {
                    $allMatch = $false
                }
            }
        }
    }

    Write-Host ("=" * 80)
    Write-Host ("CASE   : {0}" -f $Case.Name)
    Write-Host ("COMMAND: winget {0}" -f $commandText)
    Write-Host ("STATUS : {0}" -f $(if ($allMatch) { "MATCH" } else { "DIFF" }))

    if (-not $allMatch) {
        $script:HadDifference = $true
    }

    foreach ($toolName in $results.Keys) {
        $r = $results[$toolName]
        Write-Host ("{0} EXIT: {1}" -f $toolName, $r.ExitCode)
    }

    foreach ($toolName in $results.Keys) {
        Write-Host "--- $toolName ---"
        if ($r.NormalizedLines.Count -eq 0) {
            Write-Host "<no output>"
        } else {
            $results[$toolName].NormalizedLines | ForEach-Object { Write-Host $_ }
        }
    }

    if (-not $allMatch -and $CompareMode -ne "exit-code-only") {
        # Show diffs between each pair
        for ($i = 0; $i -lt $keys.Count; $i++) {
            for ($j = $i + 1; $j -lt $keys.Count; $j++) {
                $a = $results[$keys[$i]]
                $b = $results[$keys[$j]]
                $aLines = @($a.NormalizedLines)
                $bLines = @($b.NormalizedLines)
                if (($aLines -join "`n") -ne ($bLines -join "`n")) {
                    Write-Host ("--- DIFF: {0} vs {1} ---" -f $keys[$i], $keys[$j])
                    Compare-Object -ReferenceObject $aLines -DifferenceObject $bLines -SyncWindow 0 |
                        ForEach-Object {
                            $side = if ($_.SideIndicator -eq "<=") { $keys[$i] } else { $keys[$j] }
                            Write-Host ("[{0}] {1}" -f $side, $_.InputObject)
                        }
                }
            }
        }
    }
}

$caseSet = Select-CaseSet -RequestedCases $Cases
foreach ($case in $caseSet) {
    $compareMode = if ($case.ContainsKey('CompareMode')) { $case.CompareMode } else { "full" }
    $rustArgs = if ($case.ContainsKey('RustArgs')) { $case.RustArgs } else { $case.Args }
    $dotnetArgs = if ($case.ContainsKey('DotnetArgs')) { $case.DotnetArgs } else { $case.Args }

    $rustResult = $null
    $dotnetResult = $null
    $systemResult = $null

    if (-not $NoRust) {
        $rustResult = Invoke-WingetCapture -Executable $RustWinget -Arguments $rustArgs
    }
    if (-not $NoDotnet -and $compareMode -ne "rust-only") {
        $dotnetResult = Invoke-WingetCapture -Executable $DotnetWinget -Arguments $dotnetArgs
    }
    if (-not $NoSystem -and $compareMode -ne "rust-only") {
        $systemResult = Invoke-WingetCapture -Executable $SystemWinget -Arguments $case.Args
    }

    if ($compareMode -eq "rust-only") {
        # Rust-only feature — show Rust and optionally Dotnet
        if (-not $NoDotnet) {
            $dotnetResult = Invoke-WingetCapture -Executable $DotnetWinget -Arguments $dotnetArgs
        }
        Write-CaseReport -Case $case -RustResult $rustResult -DotnetResult $dotnetResult -CompareMode "full"

        # Run post-steps if defined (for round-trip tests)
        if ($case.ContainsKey('PostSteps')) {
            foreach ($step in $case.PostSteps) {
                if ($rustResult) {
                    $stepRust = Invoke-WingetCapture -Executable $RustWinget -Arguments $step.Args
                    Write-Host ("  [RUST {0}] exit={1}: {2}" -f $step.Label, $stepRust.ExitCode, ($stepRust.NormalizedLines -join " | "))
                }
                if ($dotnetResult) {
                    $stepDotnet = Invoke-WingetCapture -Executable $DotnetWinget -Arguments $step.Args
                    Write-Host ("  [DOTNET {0}] exit={1}: {2}" -f $step.Label, $stepDotnet.ExitCode, ($stepDotnet.NormalizedLines -join " | "))
                }
            }
        }
    } else {
        Write-CaseReport -Case $case -RustResult $rustResult -DotnetResult $dotnetResult -SystemResult $systemResult -CompareMode $compareMode
    }
}

if ($FailOnDiff -and $script:HadDifference) {
    throw "One or more parity cases differed."
}
