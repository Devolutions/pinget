[CmdletBinding()]
param(
    [string]$ModulePath,
    [string]$SourceArgument = 'https://api.winget.pro/4259fd23-6fcd-46bf-9287-be8833cfbdd5'
)

try
{
    Import-Module Pester -MinimumVersion 5.0.0 -ErrorAction Stop
}
catch
{
    $powerShellModuleRoots = @(
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell\Modules\Pester')
        (Join-Path $env:ProgramFiles 'PowerShell\Modules\Pester')
    )
    $pesterManifest = $null
    foreach ($powerShellModuleRoot in $powerShellModuleRoots)
    {
        if (Test-Path -Path $powerShellModuleRoot)
        {
            $pesterManifest = Get-ChildItem -Path $powerShellModuleRoot -Filter Pester.psd1 -Recurse |
                Where-Object { [version]$_.Directory.Name -ge [version]'5.0.0' } |
                Sort-Object { [version]$_.Directory.Name } -Descending |
                Select-Object -First 1 -ExpandProperty FullName

            if (-not [string]::IsNullOrWhiteSpace($pesterManifest))
            {
                break
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($pesterManifest))
    {
        throw
    }

    Import-Module $pesterManifest -MinimumVersion 5.0.0 -ErrorAction Stop
}

if ([string]::IsNullOrWhiteSpace($ModulePath))
{
    # Always restage the module from the current build outputs, so a stale or half-staged dist
    # module left by an earlier run is never the one under test.
    $outputRoot = Join-Path $PSScriptRoot '..\..\dist\powershell-module'
    $buildModuleArguments = @{
        NoBuild = $true
        OutputRoot = $outputRoot
        Clean = $true
    }

    # The suite imports the module into this session. PowerShell 7 loads the Core build, so the
    # Windows PowerShell half (net48 plus the native COM DLL) is only required in Windows PowerShell.
    if ($PSVersionTable.PSEdition -eq 'Core')
    {
        $buildModuleArguments.SkipWindowsPowerShell = $true
    }

    $buildModuleScript = Join-Path $PSScriptRoot '..\..\scripts\Build-PowerShellModule.ps1'
    & $buildModuleScript @buildModuleArguments | Out-Null
    $ModulePath = Join-Path $outputRoot 'Devolutions.Pinget.Client\Devolutions.Pinget.Client.psd1'
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
