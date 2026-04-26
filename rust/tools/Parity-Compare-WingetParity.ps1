param(
    [string]$RustWinget = (Join-Path $PSScriptRoot "..\target\debug\pinget.exe"),
    [string]$DotnetWinget = (Join-Path $PSScriptRoot "..\..\dotnet\src\Devolutions.Pinget.Cli\bin\Release\net10.0\pinget.exe"),
    [string]$PowerShellModulePath = (Join-Path $PSScriptRoot "..\..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\bin\Release\net10.0\Pinget.psd1"),
    [string]$SystemWinget = "winget",
    [string[]]$ReadOnlyCases = @(
        "show-versions",
        "source-list",
        "version"
    ),
    [string[]]$InstallCandidates = @(
        "JesseDuffield.lazygit",
        "ajeetdsouza.zoxide",
        "JanDeDobbeleer.OhMyPosh"
    ),
    [switch]$SkipReadOnlyParity,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$ConfirmPreference = "None"
$script:SystemPackageNotFoundExitCodes = @(-1978335212)
$script:PackageNameCache = @{}

function Write-Section {
    param([string]$Title)

    Write-Host ""
    Write-Host ("=" * 80)
    Write-Host $Title
    Write-Host ("=" * 80)
}

function Join-Lines {
    param([string[]]$Lines)

    return (@($Lines) -join "`n")
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Contains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    if ($Text -notmatch [regex]::Escape($Needle)) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [string]$Text,
        [string]$Needle,
        [string]$Message
    )

    if ($Text -match [regex]::Escape($Needle)) {
        throw $Message
    }
}

function Invoke-Capture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    $lines = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    $commandText = ($Arguments | ForEach-Object {
            if ($_ -match "\s") {
                '"' + $_ + '"'
            } else {
                $_
            }
        }) -join " "

    Write-Host ("> {0} {1}" -f $Executable, $commandText) -ForegroundColor DarkGray
    if ($lines.Count -gt 0) {
        $lines | ForEach-Object { Write-Host $_ }
    }
    Write-Host ("exit={0}" -f $exitCode) -ForegroundColor DarkGray

    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "Command failed with exit code ${exitCode}: $Executable $commandText"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($lines)
        Output = Join-Lines -Lines $lines
    }
}

function Invoke-BestEffort {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string[][]]$ArgumentSets
    )

    $attempts = @()
    foreach ($argumentSet in $ArgumentSets) {
        $result = $null
        try {
            $result = Invoke-Capture -Executable $Executable -Arguments $argumentSet
            return $result
        }
        catch {
            $attempts += $_.Exception.Message
        }
    }

    throw ($attempts -join "`n")
}

function Get-PackagedSettingsPath {
    return Join-Path $env:LOCALAPPDATA "Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json"
}

function Test-IsProcessElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-IsSourcePermissionFailure {
    param([string]$Message)

    return $Message -match 'requires administrator privileges|Access is denied|UnauthorizedAccessException'
}

function Test-SystemWingetSourceMutationInteropSupported {
    return $false
}

function Test-IsPackageNotInstalledFailure {
    param([string]$Message)

    return $Message -match 'No installed package found matching input criteria\.' -or $Message -match 'exit code -1978335212'
}

function Test-IsMissingSourceDataFailure {
    param([string]$Message)

    return $Message -match '0x8a15000f' -or $Message -match 'Data required by the source is missing'
}

function New-TestSettingsHashtable {
    return [ordered]@{
        experimentalFeatures = [ordered]@{
            pinning = $true
        }
        installBehavior = [ordered]@{
            preferences = [ordered]@{
                scope = "user"
            }
        }
    }
}

function Write-TestSettings {
    param([string]$Path)

    $settings = New-TestSettingsHashtable

    $directory = Split-Path -Path $Path -Parent
    if (-not (Test-Path -Path $directory)) {
        New-Item -Path $directory -ItemType Directory -Force | Out-Null
    }

    $settings | ConvertTo-Json -Depth 20 | Set-Content -Path $Path -Encoding utf8
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        $InputObject,
        [Parameter(Mandatory = $true)]
        [string[]]$Path
    )

    $current = $InputObject
    foreach ($segment in $Path) {
        if ($null -eq $current) {
            return $null
        }

        if ($current -is [System.Collections.IDictionary]) {
            if (-not $current.Contains($segment)) {
                return $null
            }
            $current = $current[$segment]
            continue
        }

        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $null
        }

        $current = $property.Value
    }

    return $current
}

function Import-PingetPowerShellModule {
    Assert-True -Condition (Test-Path -Path $PowerShellModulePath) -Message "Pinget PowerShell module not found at '$PowerShellModulePath'."

    $resolvedModulePath = (Resolve-Path -Path $PowerShellModulePath).Path
    $loadedModule = Get-Module | Where-Object { $_.Path -eq $resolvedModulePath }
    if ($null -eq $loadedModule) {
        Import-Module $resolvedModulePath -Force
    }
}

function Get-RustSettingsObject {
    $result = Invoke-Capture -Executable $RustWinget -Arguments @("settings", "export", "--output", "json")
    return $result.Output | ConvertFrom-Json
}

function Get-DotnetSettingsObject {
    $result = Invoke-Capture -Executable $DotnetWinget -Arguments @("settings", "export", "--output", "json")
    return $result.Output | ConvertFrom-Json
}

function Get-PowerShellSettingsObject {
    Import-PingetPowerShellModule
    return Get-PingetUserSettings -ErrorAction Stop
}

function Assert-TestSettingsVisible {
    param(
        [Parameter(Mandatory = $true)]
        $SettingsObject,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    Assert-True -Condition ((Get-JsonPropertyValue -InputObject $SettingsObject -Path @("experimentalFeatures", "pinning")) -eq $true) -Message "$Label did not reflect experimentalFeatures.pinning=true."
    Assert-True -Condition ((Get-JsonPropertyValue -InputObject $SettingsObject -Path @("installBehavior", "preferences", "scope")) -eq "user") -Message "$Label did not reflect installBehavior.preferences.scope=user."
}

function Test-PackageVisible {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string]$PackageId,
        [switch]$SystemCli
    )

    $commandArgs = @("list", "--id", $PackageId, "--exact")
    if ($SystemCli) {
        $commandArgs += @("--accept-source-agreements", "--disable-interactivity")
    }

    $allowedExitCodes = if ($SystemCli) {
        @(0) + $script:SystemPackageNotFoundExitCodes
    }
    else {
        @(0)
    }

    $result = Invoke-Capture -Executable $Executable -Arguments $commandArgs -AllowedExitCodes $allowedExitCodes
    if ($result.Output -match 'No installed package found matching input criteria\.') {
        return $false
    }

    return ($result.Output -match [regex]::Escape($PackageId))
}

function Test-PackageVisibleInPowerShell {
    param(
        [string]$PackageId,
        [string]$PackageName
    )

    Import-PingetPowerShellModule
    $packagesById = @(Get-PingetPackage -Id $PackageId -MatchOption EqualsCaseInsensitive -WarningAction SilentlyContinue -ErrorAction Stop)
    if (@($packagesById | Where-Object { $_.Id -eq $PackageId -or $_.Id -like ("*{0}*" -f $PackageId) }).Count -gt 0) {
        return $true
    }

    if ([string]::IsNullOrWhiteSpace($PackageName)) {
        $PackageName = Get-PackageDisplayName -PackageId $PackageId
    }

    if ([string]::IsNullOrWhiteSpace($PackageName)) {
        return $false
    }

    $packagesByName = @(Get-PingetPackage -Name $PackageName -MatchOption EqualsCaseInsensitive -WarningAction SilentlyContinue -ErrorAction Stop)
    return @($packagesByName | Where-Object { $_.Name -eq $PackageName -or $_.Id -like ("*{0}*" -f $PackageId) }).Count -gt 0
}

function Test-SourceVisible {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string]$SourceName
    )

    $result = Invoke-Capture -Executable $Executable -Arguments @("source", "list")
    return $result.Output -match ("(?m)^\s*{0}(\s|$)" -f [regex]::Escape($SourceName))
}

function Test-SourceVisibleInPowerShell {
    param([string]$SourceName)

    Import-PingetPowerShellModule
    $sources = @(Get-PingetSource -ErrorAction Stop)
    return @($sources | Where-Object { $_.Name -eq $SourceName }).Count -gt 0
}

function Select-InstallCandidate {
    param([string[]]$Candidates)

    $installedFallback = $null
    foreach ($candidate in $Candidates) {
        $showArgs = @("show", "--id", $candidate, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity")
        $showResult = Invoke-Capture -Executable $SystemWinget -Arguments $showArgs
        if ($showResult.Output -match "No package found") {
            continue
        }

        if (-not (Test-PackageVisible -Executable $SystemWinget -PackageId $candidate -SystemCli)) {
            return $candidate
        }

        if ($null -eq $installedFallback) {
            $installedFallback = $candidate
        }
    }

    if ($null -ne $installedFallback) {
        return $installedFallback
    }

    throw "No usable install candidate was available on the runner."
}

function Get-PackageDisplayName {
    param([string]$PackageId)

    if ($script:PackageNameCache.ContainsKey($PackageId)) {
        return $script:PackageNameCache[$PackageId]
    }

    $showArgs = @("show", "--id", $PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity")
    $showResult = Invoke-Capture -Executable $SystemWinget -Arguments $showArgs
    $normalizedLines = $showResult.Output -split "`r|`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    foreach ($line in $normalizedLines) {
        if ($line -match '^Found\s+(.+)\s+\[(.+)\]$' -and $matches[2] -eq $PackageId) {
            $script:PackageNameCache[$PackageId] = $matches[1]
            return $matches[1]
        }
    }

    throw "Could not determine the package display name for '$PackageId'."
}

function Update-SharedSources {
    Invoke-Capture -Executable $SystemWinget -Arguments @("source", "update") | Out-Null
}

function Ensure-HealthyReadOnlySources {
    try {
        Invoke-Capture -Executable $SystemWinget -Arguments @("show", "--id", "Microsoft.PowerToys", "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity") | Out-Null
    }
    catch {
        if (-not (Test-IsMissingSourceDataFailure -Message $_.Exception.Message)) {
            throw
        }

        Write-Warning "Resetting packaged winget sources because the runner cache is missing source data."
        Invoke-Capture -Executable $SystemWinget -Arguments @("source", "reset", "--force") | Out-Null
    }

    Update-SharedSources
}

function Install-WithWinget {
    param([string]$PackageId)

    Invoke-BestEffort -Executable $SystemWinget -ArgumentSets @(
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--scope", "user", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity", "--silent"),
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity", "--silent")
    ) | Out-Null
}

function Install-WithPinget {
    param([string]$PackageId)

    Invoke-BestEffort -Executable $RustWinget -ArgumentSets @(
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--scope", "user", "--accept-package-agreements", "--silent"),
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--accept-package-agreements", "--silent")
    ) | Out-Null
}

function Install-WithDotnet {
    param([string]$PackageId)

    Invoke-BestEffort -Executable $DotnetWinget -ArgumentSets @(
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--scope", "user", "--accept-package-agreements", "--silent"),
        @("install", "--id", $PackageId, "--exact", "--source", "winget", "--accept-package-agreements", "--silent")
    ) | Out-Null
}

function Install-WithPowerShell {
    param([string]$PackageId)

    Import-PingetPowerShellModule
    Install-PingetPackage -Id $PackageId -Source winget -MatchOption EqualsCaseInsensitive -Scope User -Mode Silent -Confirm:$false -ErrorAction Stop | Out-Null
}

function Uninstall-WithWinget {
    param([string]$PackageId)

    try {
        Invoke-BestEffort -Executable $SystemWinget -ArgumentSets @(
            @("uninstall", "--id", $PackageId, "--exact", "--scope", "user", "--disable-interactivity", "--silent"),
            @("uninstall", "--id", $PackageId, "--exact", "--disable-interactivity", "--silent")
        ) | Out-Null
    }
    catch {
        if (-not (Test-IsPackageNotInstalledFailure -Message $_.Exception.Message)) {
            throw
        }
    }
}

function Uninstall-WithPinget {
    param([string]$PackageId)

    try {
        Invoke-BestEffort -Executable $RustWinget -ArgumentSets @(
            @("uninstall", "--id", $PackageId, "--exact", "--scope", "user", "--silent"),
            @("uninstall", "--id", $PackageId, "--exact", "--silent")
        ) | Out-Null
    }
    catch {
        if (-not (Test-IsPackageNotInstalledFailure -Message $_.Exception.Message)) {
            throw
        }
    }
}

function Uninstall-WithDotnet {
    param([string]$PackageId)

    try {
        Invoke-BestEffort -Executable $DotnetWinget -ArgumentSets @(
            @("uninstall", "--id", $PackageId, "--exact", "--scope", "user", "--silent"),
            @("uninstall", "--id", $PackageId, "--exact", "--silent")
        ) | Out-Null
    }
    catch {
        if (-not (Test-IsPackageNotInstalledFailure -Message $_.Exception.Message)) {
            throw
        }
    }
}

function Uninstall-WithPowerShell {
    param(
        [string]$PackageId,
        [string]$PackageName
    )

    Import-PingetPowerShellModule
    if ([string]::IsNullOrWhiteSpace($PackageName)) {
        $PackageName = Get-PackageDisplayName -PackageId $PackageId
    }

    Uninstall-PingetPackage -Name $PackageName -MatchOption EqualsCaseInsensitive -Mode Silent -WarningAction SilentlyContinue -Confirm:$false -ErrorAction Stop | Out-Null
}

function Add-SourceWithPowerShell {
    param(
        [string]$SourceName,
        [string]$SourceArgument,
        [string]$SourceType
    )

    Import-PingetPowerShellModule
    Add-PingetSource -Name $SourceName -Argument $SourceArgument -Type $SourceType -ErrorAction Stop
}

function Add-SourceWithRust {
    param(
        [string]$SourceName,
        [string]$SourceArgument,
        [string]$SourceType
    )

    Invoke-Capture -Executable $RustWinget -Arguments @("source", "add", $SourceName, $SourceArgument, "--type", $SourceType) | Out-Null
}

function Add-SourceWithDotnet {
    param(
        [string]$SourceName,
        [string]$SourceArgument,
        [string]$SourceType
    )

    Invoke-Capture -Executable $DotnetWinget -Arguments @("source", "add", "--name", $SourceName, "--arg", $SourceArgument, "--type", $SourceType) | Out-Null
}

function Remove-SourceWithWinget {
    param([string]$SourceName)

    Invoke-Capture -Executable $SystemWinget -Arguments @("source", "remove", "--name", $SourceName) | Out-Null
}

function Remove-SourceWithPowerShell {
    param([string]$SourceName)

    Import-PingetPowerShellModule
    Remove-PingetSource -Name $SourceName -ErrorAction Stop
}

function Remove-SourceIfPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string]$SourceName
    )

    if (Test-SourceVisible -Executable $Executable -SourceName $SourceName) {
        Invoke-Capture -Executable $Executable -Arguments @("source", "remove", $SourceName) | Out-Null
    }
}

function Remove-SourceIfPresentInPowerShell {
    param([string]$SourceName)

    if (Test-SourceVisibleInPowerShell -SourceName $SourceName) {
        Remove-SourceWithPowerShell -SourceName $SourceName
    }
}

function Test-PinVisible {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,
        [Parameter(Mandatory = $true)]
        [string]$PackageId,
        [switch]$SystemCli
    )

    $commandArgs = @("pin", "list", "--id", $PackageId, "--exact")
    if ($SystemCli) {
        $commandArgs += @("--accept-source-agreements", "--disable-interactivity")
    }

    $result = Invoke-Capture -Executable $Executable -Arguments $commandArgs
    return $result.Output -match [regex]::Escape($PackageId)
}

function Remove-PinIfPresent {
    param(
        [string]$Executable,
        [string]$PackageId,
        [switch]$SystemCli
    )

    if (Test-PinVisible -Executable $Executable -PackageId $PackageId -SystemCli:$SystemCli) {
        $argumentSets = @(
            @("pin", "remove", "--id", $PackageId, "--exact", "--installed"),
            @("pin", "remove", "--id", $PackageId, "--exact")
        )

        if ($SystemCli) {
            $argumentSets = @($argumentSets | ForEach-Object {
                    @($_ + @("--accept-source-agreements", "--disable-interactivity"))
                })
        }

        Invoke-BestEffort -Executable $Executable -ArgumentSets $argumentSets | Out-Null
    }
}

Assert-True -Condition (Test-Path -Path $RustWinget) -Message "Rust pinget binary not found at '$RustWinget'."
Assert-True -Condition (Test-Path -Path $DotnetWinget) -Message "C# pinget binary not found at '$DotnetWinget'."
Assert-True -Condition (Test-Path -Path $PowerShellModulePath) -Message "Pinget PowerShell module not found at '$PowerShellModulePath'."
Import-PingetPowerShellModule
Invoke-Capture -Executable $SystemWinget -Arguments @("--version") | Out-Null

$settingsPath = Get-PackagedSettingsPath
$packagedLocalStateSegment = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState"
$settingsBackup = if (Test-Path -Path $settingsPath) { Get-Content -Path $settingsPath -Raw } else { $null }
$packageId = $null
$packageName = $null
$packageInstalled = $false
$pinPackageId = $null
$sourceName = "smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$sourceArgument = "https://example.com/pinget-smoke/" + [Guid]::NewGuid().ToString("N")
$sourceCliType = "rest"
$sourcePowerShellType = "Microsoft.Rest"

try {
    Write-Section "Shared path sanity"
    $rustInfo = Invoke-Capture -Executable $RustWinget -Arguments @("--info")
    $dotnetInfo = Invoke-Capture -Executable $DotnetWinget -Arguments @("--info")
    $wingetInfo = Invoke-Capture -Executable $SystemWinget -Arguments @("--info")
    Assert-Contains -Text $rustInfo.Output -Needle $settingsPath -Message "Rust pinget --info did not report the packaged settings path."
    Assert-Contains -Text $dotnetInfo.Output -Needle "Pure C# subset of Pinget" -Message "C# pinget --info did not report the expected runtime banner."
    Assert-Contains -Text $wingetInfo.Output -Needle $packagedLocalStateSegment -Message "winget --info did not report the packaged LocalState path."
    Assert-True -Condition ($null -ne (Get-PowerShellSettingsObject)) -Message "PowerShell module could not read Pinget user settings."

    if (-not $SkipReadOnlyParity) {
        Write-Section "Source baseline"
        Ensure-HealthyReadOnlySources

        Write-Section "Read-only parity"
        $parityScript = Join-Path $PSScriptRoot "Compare-WingetParity.ps1"
        Assert-True -Condition (Test-Path -Path $parityScript) -Message "Parity helper not found at '$parityScript'."
        & $parityScript -RustWinget $RustWinget -DotnetWinget $DotnetWinget -SystemWinget $SystemWinget -FailOnDiff -Cases $ReadOnlyCases
    }

    if ($ValidateOnly) {
        Write-Section "Validation-only mode"
        Write-Host "Smoke workflow prechecks succeeded."
        return
    }

    Write-Section "Settings roundtrip"
    $testSettings = New-TestSettingsHashtable
    Set-PingetUserSettings -UserSettings $testSettings -ErrorAction Stop | Out-Null
    Assert-True -Condition (Test-Path -Path $settingsPath) -Message "PowerShell settings write did not create the packaged settings file."
    Assert-TestSettingsVisible -SettingsObject (Get-RustSettingsObject) -Label "Rust pinget settings export"
    Assert-TestSettingsVisible -SettingsObject (Get-DotnetSettingsObject) -Label "C# pinget settings export"
    Assert-TestSettingsVisible -SettingsObject (Get-PowerShellSettingsObject) -Label "Pinget PowerShell settings"

    Write-Section "Source coherence"
    Remove-SourceIfPresent -Executable $RustWinget -SourceName $sourceName
    Remove-SourceIfPresent -Executable $DotnetWinget -SourceName $sourceName
    Remove-SourceIfPresent -Executable $SystemWinget -SourceName $sourceName
    Remove-SourceIfPresentInPowerShell -SourceName $sourceName

    if (-not (Test-IsProcessElevated)) {
        Write-Warning "Skipping source mutation checks because this process is not elevated."
    }
    else {
        $validateSystemWingetSourceMutation = Test-SystemWingetSourceMutationInteropSupported
        if (-not $validateSystemWingetSourceMutation) {
            Write-Warning "Skipping packaged winget source mutation assertions because system winget stores sources in WinRT LocalSettings plus secure metadata, and Pinget intentionally stays COM/WinRT-free."
        }

        try {
            Add-SourceWithPowerShell -SourceName $sourceName -SourceArgument $sourceArgument -SourceType $sourcePowerShellType
            Assert-True -Condition (Test-SourceVisible -Executable $RustWinget -SourceName $sourceName) -Message "Rust pinget did not reflect the source added by the PowerShell module."
            Assert-True -Condition (Test-SourceVisible -Executable $DotnetWinget -SourceName $sourceName) -Message "C# pinget did not reflect the source added by the PowerShell module."
            if ($validateSystemWingetSourceMutation) {
                Assert-True -Condition (Test-SourceVisible -Executable $SystemWinget -SourceName $sourceName) -Message "winget did not reflect the source added by the PowerShell module."
            }

            Invoke-Capture -Executable $RustWinget -Arguments @("source", "remove", $sourceName) | Out-Null
            Assert-True -Condition (-not (Test-SourceVisible -Executable $DotnetWinget -SourceName $sourceName)) -Message "C# pinget still reported the source after Rust pinget removed it."
            if ($validateSystemWingetSourceMutation) {
                Assert-True -Condition (-not (Test-SourceVisible -Executable $SystemWinget -SourceName $sourceName)) -Message "winget still reported the source after Rust pinget removed it."
            }
            Assert-True -Condition (-not (Test-SourceVisibleInPowerShell -SourceName $sourceName)) -Message "PowerShell module still reported the source after Rust pinget removed it."

            Add-SourceWithDotnet -SourceName $sourceName -SourceArgument $sourceArgument -SourceType $sourceCliType
            Assert-True -Condition (Test-SourceVisible -Executable $RustWinget -SourceName $sourceName) -Message "Rust pinget did not reflect the source added by the C# CLI."
            if ($validateSystemWingetSourceMutation) {
                Assert-True -Condition (Test-SourceVisible -Executable $SystemWinget -SourceName $sourceName) -Message "winget did not reflect the source added by the C# CLI."
            }
            Assert-True -Condition (Test-SourceVisibleInPowerShell -SourceName $sourceName) -Message "PowerShell module did not reflect the source added by the C# CLI."

            if ($validateSystemWingetSourceMutation) {
                Remove-SourceWithWinget -SourceName $sourceName
                Assert-True -Condition (-not (Test-SourceVisible -Executable $RustWinget -SourceName $sourceName)) -Message "Rust pinget still reported the source after winget removed it."
                Assert-True -Condition (-not (Test-SourceVisible -Executable $DotnetWinget -SourceName $sourceName)) -Message "C# pinget still reported the source after winget removed it."
                Assert-True -Condition (-not (Test-SourceVisibleInPowerShell -SourceName $sourceName)) -Message "PowerShell module still reported the source after winget removed it."
            }
            else {
                Remove-SourceWithPowerShell -SourceName $sourceName
                Assert-True -Condition (-not (Test-SourceVisible -Executable $RustWinget -SourceName $sourceName)) -Message "Rust pinget still reported the source after the PowerShell module removed it."
                Assert-True -Condition (-not (Test-SourceVisible -Executable $DotnetWinget -SourceName $sourceName)) -Message "C# pinget still reported the source after the PowerShell module removed it."
                Assert-True -Condition (-not (Test-SourceVisibleInPowerShell -SourceName $sourceName)) -Message "PowerShell module still reported the source after removing it."
            }
        }
        catch {
            if (Test-IsSourcePermissionFailure -Message $_.Exception.Message) {
                Write-Warning ("Skipping source mutation checks: {0}" -f $_.Exception.Message)
            }
            else {
                throw
            }
        }
    }

    Write-Section "Install and uninstall coherence"
    $packageId = Select-InstallCandidate -Candidates $InstallCandidates
    $packageName = Get-PackageDisplayName -PackageId $packageId
    Write-Host ("Selected install candidate: {0} ({1})" -f $packageId, $packageName)

    if (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli) {
        Write-Host ("Normalizing preinstalled candidate back to baseline: {0}" -f $packageId)
        Uninstall-WithWinget -PackageId $packageId
        Assert-True -Condition (-not (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli)) -Message "winget still detected the candidate after baseline uninstall."
        Assert-True -Condition (-not (Test-PackageVisible -Executable $RustWinget -PackageId $packageId)) -Message "Rust pinget still detected the candidate after baseline uninstall."
        Assert-True -Condition (-not (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId)) -Message "C# pinget still detected the candidate after baseline uninstall."
        Assert-True -Condition (-not (Test-PackageVisibleInPowerShell -PackageId $packageId -PackageName $packageName)) -Message "PowerShell module still detected the candidate after baseline uninstall."
    }

    Install-WithWinget -PackageId $packageId
    $packageInstalled = $true
    Assert-True -Condition (Test-PackageVisible -Executable $RustWinget -PackageId $packageId) -Message "Rust pinget did not detect the package installed by winget."
    Assert-True -Condition (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId) -Message "C# pinget did not detect the package installed by winget."
    Assert-True -Condition (Test-PackageVisibleInPowerShell -PackageId $packageId -PackageName $packageName) -Message "PowerShell module did not detect the package installed by winget."

    Uninstall-WithPinget -PackageId $packageId
    $packageInstalled = $false
    Assert-True -Condition (-not (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli)) -Message "winget still detected the package after Rust pinget uninstall."
    Assert-True -Condition (-not (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId)) -Message "C# pinget still detected the package after Rust pinget uninstall."
    Assert-True -Condition (-not (Test-PackageVisibleInPowerShell -PackageId $packageId -PackageName $packageName)) -Message "PowerShell module still detected the package after Rust pinget uninstall."

    Install-WithDotnet -PackageId $packageId
    $packageInstalled = $true
    Assert-True -Condition (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli) -Message "winget did not detect the package installed by the C# CLI."
    Assert-True -Condition (Test-PackageVisible -Executable $RustWinget -PackageId $packageId) -Message "Rust pinget did not detect the package installed by the C# CLI."
    Assert-True -Condition (Test-PackageVisibleInPowerShell -PackageId $packageId -PackageName $packageName) -Message "PowerShell module did not detect the package installed by the C# CLI."

    Uninstall-WithPowerShell -PackageId $packageId -PackageName $packageName
    $packageInstalled = $false
    Assert-True -Condition (-not (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli)) -Message "winget still detected the package after PowerShell uninstall."
    Assert-True -Condition (-not (Test-PackageVisible -Executable $RustWinget -PackageId $packageId)) -Message "Rust pinget still detected the package after PowerShell uninstall."
    Assert-True -Condition (-not (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId)) -Message "C# pinget still detected the package after PowerShell uninstall."

    Install-WithPowerShell -PackageId $packageId
    $packageInstalled = $true
    Assert-True -Condition (Test-PackageVisible -Executable $SystemWinget -PackageId $packageId -SystemCli) -Message "winget did not detect the package installed by the PowerShell module."
    Assert-True -Condition (Test-PackageVisible -Executable $RustWinget -PackageId $packageId) -Message "Rust pinget did not detect the package installed by the PowerShell module."
    Assert-True -Condition (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId) -Message "C# pinget did not detect the package installed by the PowerShell module."
    $pinPackageId = $packageId

    Write-Section "Pin coherence"
    Remove-PinIfPresent -Executable $RustWinget -PackageId $pinPackageId
    Remove-PinIfPresent -Executable $DotnetWinget -PackageId $pinPackageId
    Remove-PinIfPresent -Executable $SystemWinget -PackageId $pinPackageId -SystemCli

    Invoke-Capture -Executable $SystemWinget -Arguments @("pin", "add", "--id", $pinPackageId, "--exact", "--accept-source-agreements", "--disable-interactivity") | Out-Null
    Assert-True -Condition (Test-PinVisible -Executable $RustWinget -PackageId $pinPackageId) -Message "Rust pinget did not reflect the pin created by winget."
    Assert-True -Condition (Test-PinVisible -Executable $DotnetWinget -PackageId $pinPackageId) -Message "C# pinget did not reflect the pin created by winget."

    Invoke-Capture -Executable $RustWinget -Arguments @("pin", "remove", "--id", $pinPackageId, "--exact") | Out-Null
    Assert-True -Condition (-not (Test-PinVisible -Executable $SystemWinget -PackageId $pinPackageId -SystemCli)) -Message "winget still reported the pin after Rust pinget removed it."
    Assert-True -Condition (-not (Test-PinVisible -Executable $DotnetWinget -PackageId $pinPackageId)) -Message "C# pinget still reported the pin after Rust pinget removed it."

    Invoke-Capture -Executable $SystemWinget -Arguments @("pin", "add", "--id", $pinPackageId, "--exact", "--installed", "--accept-source-agreements", "--disable-interactivity") | Out-Null
    $supportsInstalledPins = Test-PinVisible -Executable $SystemWinget -PackageId $pinPackageId -SystemCli
    Remove-PinIfPresent -Executable $SystemWinget -PackageId $pinPackageId -SystemCli

    if ($supportsInstalledPins) {
        Invoke-Capture -Executable $DotnetWinget -Arguments @("pin", "add", "--id", $pinPackageId, "--exact", "--installed") | Out-Null
        Assert-True -Condition (Test-PinVisible -Executable $RustWinget -PackageId $pinPackageId) -Message "Rust pinget did not reflect the installed pin created by the C# CLI."
        Assert-True -Condition (Test-PinVisible -Executable $SystemWinget -PackageId $pinPackageId -SystemCli) -Message "winget did not reflect the installed pin created by the C# CLI."

        Remove-PinIfPresent -Executable $SystemWinget -PackageId $pinPackageId -SystemCli
        Assert-True -Condition (-not (Test-PinVisible -Executable $RustWinget -PackageId $pinPackageId)) -Message "Rust pinget still reported the installed pin after winget removed it."
        Assert-True -Condition (-not (Test-PinVisible -Executable $DotnetWinget -PackageId $pinPackageId)) -Message "C# pinget still reported the installed pin after winget removed it."
    }
    else {
        Write-Host ("Skipping installed pin coherence for {0} because packaged winget did not persist an installed pin for this package." -f $pinPackageId) -ForegroundColor Yellow
    }

    Uninstall-WithWinget -PackageId $packageId
    $packageInstalled = $false
    Assert-True -Condition (-not (Test-PackageVisible -Executable $RustWinget -PackageId $packageId)) -Message "Rust pinget still detected the package after winget uninstall."
    Assert-True -Condition (-not (Test-PackageVisible -Executable $DotnetWinget -PackageId $packageId)) -Message "C# pinget still detected the package after winget uninstall."
    Assert-True -Condition (-not (Test-PackageVisibleInPowerShell -PackageId $packageId -PackageName $packageName)) -Message "PowerShell module still detected the package after winget uninstall."

    Write-Section "Parity tests completed"
    Write-Host "Windows coherence parity tests passed for winget, Rust, C# CLI, and PowerShell."
}
finally {
    Write-Section "Cleanup"

    try {
        if ($packageInstalled -and $packageId) {
            Uninstall-WithWinget -PackageId $packageId
            $packageInstalled = $false
        }
    }
    catch {
        Write-Warning $_.Exception.Message
    }

    foreach ($cleanupSource in @(
            @{ Kind = "cli"; Executable = $RustWinget },
            @{ Kind = "cli"; Executable = $DotnetWinget },
            @{ Kind = "cli"; Executable = $SystemWinget },
            @{ Kind = "powershell" }
        )) {
        try {
            if ($cleanupSource.Kind -eq "powershell") {
                Remove-SourceIfPresentInPowerShell -SourceName $sourceName
            }
            else {
                Remove-SourceIfPresent -Executable $cleanupSource.Executable -SourceName $sourceName
            }
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }

    foreach ($cleanup in @(
            @{ Executable = $RustWinget; SystemCli = $false },
            @{ Executable = $DotnetWinget; SystemCli = $false },
            @{ Executable = $SystemWinget; SystemCli = $true }
        )) {
        try {
            Remove-PinIfPresent -Executable $cleanup.Executable -PackageId $pinPackageId -SystemCli:([bool]$cleanup.SystemCli)
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }

    if ($null -eq $settingsBackup) {
        if (Test-Path -Path $settingsPath) {
            Remove-Item -Path $settingsPath -Force
        }
    }
    else {
        $settingsBackup | Set-Content -Path $settingsPath -Encoding utf8
    }
}