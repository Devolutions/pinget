[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Framework = 'net8.0',
    [string]$DesktopFramework = 'net48',
    [string]$Version,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\dist\powershell-module'),
    [string]$ArchivePath,
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$NativeComRuntimeIdentifiers = @(),
    [switch]$NoBuild,
    [switch]$Clean,
    [switch]$SkipWindowsPowerShell
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleName = 'Devolutions.Pinget.Client'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $PSScriptRoot '..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\Devolutions.Pinget.PowerShell.Cmdlets.csproj'
$moduleFilesRoot = Join-Path $PSScriptRoot '..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\ModuleFiles'
$coreBuildOutput = Join-Path $PSScriptRoot "..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\bin\$Configuration\$Framework"
$desktopBuildOutput = Join-Path $PSScriptRoot "..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\bin\$Configuration\$DesktopFramework"
$moduleRoot = Join-Path $OutputRoot $moduleName
$moduleManifest = Join-Path $moduleRoot "$moduleName.psd1"
$nativeComCargoDllName = 'pinget_com.dll'
$nativeComPackageDllName = 'pinget-com.dll'
$coreRuntimeIdentifiersToKeep = @(
    'linux-x64',
    'linux-arm64',
    'osx-x64',
    'osx-arm64',
    'win-x64',
    'win-arm64'
)
$allNativeComTargets = @(
    @{
        Rid = 'win-x64'
        CargoTarget = 'x86_64-pc-windows-msvc'
    },
    @{
        Rid = 'win-arm64'
        CargoTarget = 'aarch64-pc-windows-msvc'
    }
)

function Test-CiBuild {
    return -not [string]::IsNullOrWhiteSpace($env:CI) -or
        -not [string]::IsNullOrWhiteSpace($env:GITHUB_ACTIONS) -or
        -not [string]::IsNullOrWhiteSpace($env:TF_BUILD)
}

function Get-CurrentWindowsRuntimeIdentifier {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { return 'win-x64' }
        'Arm64' { return 'win-arm64' }
        default {
            throw "Native COM packaging is only supported for x64 and ARM64 Windows processes. Current process architecture: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)."
        }
    }
}

function Resolve-NativeComTargets {
    param(
        [string[]]$RequestedRuntimeIdentifiers
    )

    if ($RequestedRuntimeIdentifiers.Count -eq 0) {
        if (Test-CiBuild) {
            $RequestedRuntimeIdentifiers = @('win-x64', 'win-arm64')
        }
        else {
            $RequestedRuntimeIdentifiers = @(Get-CurrentWindowsRuntimeIdentifier)
        }
    }

    $resolvedTargets = @($allNativeComTargets | Where-Object { $RequestedRuntimeIdentifiers -contains $_.Rid })
    if ($resolvedTargets.Count -ne $RequestedRuntimeIdentifiers.Count) {
        throw "Unsupported native COM runtime identifier requested. Supported values: win-x64, win-arm64."
    }

    return $resolvedTargets
}

function Remove-UnusedRuntimeAssets {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string[]]$RuntimeIdentifiersToKeep
    )

    $runtimesRoot = Join-Path $Root 'runtimes'
    if (-not (Test-Path -Path $runtimesRoot)) {
        return
    }

    Get-ChildItem -Path $runtimesRoot -Directory |
        Where-Object { $RuntimeIdentifiersToKeep -notcontains $_.Name } |
        Remove-Item -Recurse -Force
}

function Build-NativeComDll {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CargoTarget
    )

    cargo build -p pinget-com --manifest-path (Join-Path $repoRoot 'rust\Cargo.toml') --release --target $CargoTarget
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed for pinget-com target $CargoTarget with exit code $LASTEXITCODE."
    }
}

function Get-NativeComDllPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CargoTarget
    )

    $releaseNativeComDll = Join-Path $repoRoot "rust\target\$CargoTarget\release\$nativeComCargoDllName"
    if (Test-Path -Path $releaseNativeComDll) {
        return $releaseNativeComDll
    }

    $debugNativeComDll = Join-Path $repoRoot "rust\target\$CargoTarget\debug\$nativeComCargoDllName"
    if (Test-Path -Path $debugNativeComDll) {
        return $debugNativeComDll
    }

    if ($CargoTarget -eq 'x86_64-pc-windows-msvc') {
        $legacyReleaseNativeComDll = Join-Path $repoRoot "rust\target\release\$nativeComCargoDllName"
        if (Test-Path -Path $legacyReleaseNativeComDll) {
            return $legacyReleaseNativeComDll
        }

        $legacyDebugNativeComDll = Join-Path $repoRoot "rust\target\debug\$nativeComCargoDllName"
        if (Test-Path -Path $legacyDebugNativeComDll) {
            return $legacyDebugNativeComDll
        }
    }

    throw "Native COM DLL not found for target $CargoTarget."
}

$nativeComTargets = @(Resolve-NativeComTargets -RequestedRuntimeIdentifiers $NativeComRuntimeIdentifiers)
$desktopRuntimeIdentifiersToKeep = @($nativeComTargets | ForEach-Object { $_.Rid })

if (-not $NoBuild) {
    dotnet build $projectPath -c $Configuration -f $Framework --tl:off
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $Framework with exit code $LASTEXITCODE."
    }

    if (-not $SkipWindowsPowerShell) {
        dotnet build $projectPath -c $Configuration -f $DesktopFramework --tl:off
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for $DesktopFramework with exit code $LASTEXITCODE."
        }

        if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
            foreach ($nativeComTarget in $nativeComTargets) {
                Build-NativeComDll -CargoTarget $nativeComTarget.CargoTarget
            }
        }
    }
}

$sourceManifest = Join-Path $moduleFilesRoot "$moduleName.psd1"
if (-not (Test-Path -Path $sourceManifest)) {
    throw "PowerShell module manifest not found: $sourceManifest"
}

$sourceLoader = Join-Path $moduleFilesRoot "$moduleName.psm1"
if (-not (Test-Path -Path $sourceLoader)) {
    throw "PowerShell module loader not found: $sourceLoader"
}

if ($Clean -and (Test-Path -Path $OutputRoot)) {
    Remove-Item -Path $OutputRoot -Recurse -Force
}

New-Item -Path $moduleRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $moduleFilesRoot '*') -Destination $moduleRoot -Recurse -Force

$coreModuleRoot = Join-Path $moduleRoot "Core\$Framework"
New-Item -Path $coreModuleRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $coreBuildOutput '*') -Destination $coreModuleRoot -Recurse -Force
Get-ChildItem -Path $coreModuleRoot -Include '*.psd1', '*.psm1', 'Format.ps1xml' -Recurse |
    Remove-Item -Force
Remove-UnusedRuntimeAssets -Root $coreModuleRoot -RuntimeIdentifiersToKeep $coreRuntimeIdentifiersToKeep

if (-not $SkipWindowsPowerShell) {
    $desktopModuleRoot = Join-Path $moduleRoot "Desktop\$DesktopFramework"
    New-Item -Path $desktopModuleRoot -ItemType Directory -Force | Out-Null
    Copy-Item -Path (Join-Path $desktopBuildOutput '*') -Destination $desktopModuleRoot -Recurse -Force
    Get-ChildItem -Path $desktopModuleRoot -Include '*.psd1', '*.psm1', 'Format.ps1xml' -Recurse |
        Remove-Item -Force
    Remove-UnusedRuntimeAssets -Root $desktopModuleRoot -RuntimeIdentifiersToKeep $desktopRuntimeIdentifiersToKeep

    foreach ($nativeComTarget in $nativeComTargets) {
        $nativeComDllPath = Get-NativeComDllPath -CargoTarget $nativeComTarget.CargoTarget
        $nativeComDestination = Join-Path $desktopModuleRoot "runtimes\$($nativeComTarget.Rid)\native"
        New-Item -Path $nativeComDestination -ItemType Directory -Force | Out-Null
        Copy-Item -Path $nativeComDllPath -Destination (Join-Path $nativeComDestination $nativeComPackageDllName) -Force
    }
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^([0-9]+(?:\.[0-9]+){1,3})(?:-.+)?$') {
        throw "PowerShell module version '$Version' must start with a numeric module version."
    }

    $moduleVersion = $Matches[1]
    $content = Get-Content -Path $moduleManifest -Raw
    $content = $content -replace "ModuleVersion = '[^']+'", "ModuleVersion = '$moduleVersion'"
    Set-Content -Path $moduleManifest -Value $content -Encoding utf8
}

$manifestInfo = Test-ModuleManifest -Path $moduleManifest
if ($manifestInfo.Name -ne $moduleName) {
    throw "Expected module '$moduleName' but manifest resolved to '$($manifestInfo.Name)'."
}

if ($manifestInfo.PowerShellVersion -lt [version]'5.1') {
    throw "Expected the module to require PowerShell 5.1 or newer."
}

if (-not [string]::IsNullOrWhiteSpace($ArchivePath)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $resolvedArchivePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArchivePath)
    $archiveDirectory = Split-Path -Path $resolvedArchivePath -Parent
    New-Item -Path $archiveDirectory -ItemType Directory -Force | Out-Null
    if (Test-Path -Path $resolvedArchivePath) {
        Remove-Item -Path $resolvedArchivePath -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path -Path $OutputRoot).Path,
        $resolvedArchivePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

Write-Output $moduleRoot
