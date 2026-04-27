[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Framework = 'net8.0',
    [string]$Version,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\dist\powershell-module'),
    [string]$ArchivePath,
    [switch]$NoBuild,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$moduleName = 'Devolutions.Pinget.Client'
$projectPath = Join-Path $PSScriptRoot '..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\Devolutions.Pinget.PowerShell.Cmdlets.csproj'
$buildOutput = Join-Path $PSScriptRoot "..\dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\bin\$Configuration\$Framework"
$moduleRoot = Join-Path $OutputRoot $moduleName
$moduleManifest = Join-Path $moduleRoot "$moduleName.psd1"

if (-not $NoBuild) {
    dotnet build $projectPath -c $Configuration -f $Framework
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

$builtManifest = Join-Path $buildOutput "$moduleName.psd1"
if (-not (Test-Path -Path $builtManifest)) {
    throw "PowerShell module manifest not found: $builtManifest"
}

if ($Clean -and (Test-Path -Path $OutputRoot)) {
    Remove-Item -Path $OutputRoot -Recurse -Force
}

New-Item -Path $moduleRoot -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $buildOutput '*') -Destination $moduleRoot -Recurse -Force
Get-ChildItem -Path $moduleRoot -Filter '*.psd1' |
    Where-Object { $_.Name -ne "$moduleName.psd1" } |
    Remove-Item -Force

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

if ($manifestInfo.PowerShellVersion -lt [version]'7.4.0') {
    throw "Expected the module to require PowerShell 7.4.0 or newer."
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
