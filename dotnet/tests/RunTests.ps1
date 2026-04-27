[CmdletBinding()]
param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\..\dist\powershell-module\Devolutions.Pinget.Client\Devolutions.Pinget.Client.psd1'),
    [string]$SourceArgument = 'https://api.winget.pro/4259fd23-6fcd-46bf-9287-be8833cfbdd5'
)

Import-Module Pester -MinimumVersion 5.0.0

if (-not (Test-Path -Path $ModulePath))
{
    $buildModuleScript = Join-Path $PSScriptRoot '..\..\scripts\Build-PowerShellModule.ps1'
    & $buildModuleScript -NoBuild -OutputRoot (Join-Path $PSScriptRoot '..\..\dist\powershell-module') -Clean | Out-Null
}

$config = New-PesterConfiguration
$config.Run.Path = $PSScriptRoot
$config.Run.PassThru = $true
$config.Output.Verbosity = 'Detailed'
$config.Run.Container = New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pinget.PowerShell.Tests.ps1') -Data @{
    ModulePath = (Resolve-Path $ModulePath).Path
    SourceArgument = $SourceArgument
}

$result = Invoke-Pester -Configuration $config
if ($result.FailedCount -gt 0)
{
    exit 1
}
