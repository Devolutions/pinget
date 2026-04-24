[CmdletBinding()]
param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\src\Devolutions.Pinget.PowerShell.Cmdlets\bin\Release\net10.0\Pinget.psd1'),
    [string]$SourceArgument = 'https://api.winget.pro/4259fd23-6fcd-46bf-9287-be8833cfbdd5'
)

Import-Module Pester -MinimumVersion 5.0.0

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
