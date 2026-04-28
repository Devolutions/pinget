Set-StrictMode -Version Latest

$edition = $PSVersionTable.PSEdition
if ([string]::IsNullOrWhiteSpace($edition)) {
    $edition = 'Desktop'
}

if ($edition -eq 'Desktop') {
    $implementationRoot = Join-Path $PSScriptRoot 'Desktop\net48'
}
elseif ($edition -eq 'Core') {
    $coreRoot = Join-Path $PSScriptRoot 'Core'
    $preferredFrameworks = @('net10.0', 'net8.0')
    $implementationRoot = $null
    foreach ($framework in $preferredFrameworks) {
        $candidate = Join-Path $coreRoot $framework
        if (Test-Path -LiteralPath (Join-Path $candidate 'Devolutions.Pinget.PowerShell.Cmdlets.dll')) {
            $implementationRoot = $candidate
            break
        }
    }

    if ($null -eq $implementationRoot -and (Test-Path -LiteralPath $coreRoot)) {
        $implementationRoot = Get-ChildItem -LiteralPath $coreRoot -Directory |
            Sort-Object -Property Name -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
else {
    throw "Unsupported PowerShell edition '$edition'."
}

if ([string]::IsNullOrWhiteSpace($implementationRoot)) {
    throw "No Pinget implementation folder was found for PowerShell edition '$edition'."
}

$binaryModule = Join-Path $implementationRoot 'Devolutions.Pinget.PowerShell.Cmdlets.dll'
if (-not (Test-Path -LiteralPath $binaryModule)) {
    throw "Pinget binary module not found: $binaryModule"
}

Import-Module -Name $binaryModule -ErrorAction Stop
Export-ModuleMember -Cmdlet * -Function * -Alias *
