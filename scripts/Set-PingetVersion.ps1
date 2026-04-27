[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Set-VersionInFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath,
        [Parameter(Mandatory = $true)]
        [string]$Pattern,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Replacement,
        [int]$ExpectedMatches = 1
    )

    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -Path $path)) {
        throw "Version target not found: $RelativePath"
    }

    $content = [System.IO.File]::ReadAllText($path)
    $matches = [regex]::Matches($content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -ne $ExpectedMatches) {
        throw "Expected $ExpectedMatches version match(es) in $RelativePath but found $($matches.Count)."
    }

    $updated = [regex]::Replace(
        $content,
        $Pattern,
        {
            param($match)
            & $Replacement $match
        },
        [System.Text.RegularExpressions.RegexOptions]::Multiline)

    [System.IO.File]::WriteAllText($path, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated $RelativePath"
}

Set-VersionInFile `
    -RelativePath 'rust\crates\pinget-core\Cargo.toml' `
    -Pattern '^(version\s*=\s*")[^"]+(")' `
    -Replacement { param($match) "$($match.Groups[1].Value)$Version$($match.Groups[2].Value)" }

Set-VersionInFile `
    -RelativePath 'rust\crates\pinget-cli\Cargo.toml' `
    -Pattern '^(version\s*=\s*")[^"]+(")' `
    -Replacement { param($match) "$($match.Groups[1].Value)$Version$($match.Groups[2].Value)" }

Set-VersionInFile `
    -RelativePath 'rust\crates\pinget-cli\Cargo.toml' `
    -Pattern '(pinget-core\s*=\s*\{\s*version\s*=\s*")[^"]+(")' `
    -Replacement { param($match) "$($match.Groups[1].Value)$Version$($match.Groups[2].Value)" }

Set-VersionInFile `
    -RelativePath 'dotnet\src\Devolutions.Pinget.Cli\Program.cs' `
    -Pattern '^(const string Version\s*=\s*")[^"]+(";)' `
    -Replacement { param($match) "$($match.Groups[1].Value)$Version$($match.Groups[2].Value)" }

Set-VersionInFile `
    -RelativePath 'dotnet\src\Devolutions.Pinget.PowerShell.Engine\PowerShellEngineVersion.cs' `
    -Pattern '^(    public const string Current\s*=\s*")[^"]+(";)' `
    -Replacement { param($match) "$($match.Groups[1].Value)$Version$($match.Groups[2].Value)" }

$moduleVersion = if ($Version -match '^(\d+\.\d+\.\d+(?:\.\d+)?)') { $Matches[1] } else { $Version }
Set-VersionInFile `
    -RelativePath 'dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets\ModuleFiles\Devolutions.Pinget.Client.psd1' `
    -Pattern "^(    ModuleVersion\s*=\s*')[^']+(')" `
    -Replacement { param($match) "$($match.Groups[1].Value)$moduleVersion$($match.Groups[2].Value)" }
