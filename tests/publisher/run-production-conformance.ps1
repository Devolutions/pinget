param(
    [string] $RustCli = (Join-Path $PSScriptRoot '..\..\rust\target\release\pinget.exe'),
    [string] $DotnetAssembly = (Join-Path $PSScriptRoot '..\..\dotnet\src\Devolutions.Pinget.Cli\bin\Release\net10.0\pinget.dll'),
    [string] $DotnetExecutable = '',
    [string] $DotnetHost = 'dotnet',
    [string] $InputDirectory = (Join-Path $PSScriptRoot 'fixtures\multi'),
    [string] $WorkDirectory = (Join-Path $env:TEMP ('PingetPublisherConformance-' + [Guid]::NewGuid().ToString('N'))),
    [string] $ExpectedPackageId = 'Pinget.Test.Matrix',
    [string] $Python = 'python',
    [string] $MakeAppxPath = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe',
    [string] $PackageName = 'Pinget.Publisher.Conformance',
    [string] $Publisher = 'CN=Pinget Publisher Conformance Test'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$inputPath = [System.IO.Path]::GetFullPath($InputDirectory)
$workPath = [System.IO.Path]::GetFullPath($WorkDirectory)
$rustPath = [System.IO.Path]::GetFullPath($RustCli)
$dotnetPath = [System.IO.Path]::GetFullPath($DotnetAssembly)
$dotnetExecutablePath = if ([string]::IsNullOrWhiteSpace($DotnetExecutable)) {
    ''
} else {
    [System.IO.Path]::GetFullPath($DotnetExecutable)
}
$validator = Join-Path $PSScriptRoot 'validate_production_snapshot.py'
$comparer = Join-Path $PSScriptRoot 'compare_production_snapshots.py'

foreach ($required in @($inputPath, $rustPath, $validator, $comparer, $MakeAppxPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required publisher conformance input does not exist: $required"
    }
}
if ([string]::IsNullOrWhiteSpace($dotnetExecutablePath) -and -not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Required publisher conformance input does not exist: $dotnetPath"
}
if (-not [string]::IsNullOrWhiteSpace($dotnetExecutablePath) -and
    -not (Test-Path -LiteralPath $dotnetExecutablePath)) {
    throw "Required publisher conformance input does not exist: $dotnetExecutablePath"
}
New-Item -ItemType Directory -Force -Path $workPath | Out-Null
$rustState = Join-Path $workPath 'rust-state'
$dotnetState = Join-Path $workPath 'dotnet-state'

function Invoke-Pinget {
    param(
        [ValidateSet('Rust', 'Dotnet')]
        [string] $Implementation,
        [string[]] $Arguments
    )
    Write-Host ("RUN {0}: {1}" -f $Implementation, ($Arguments -join ' '))
    if ($Implementation -eq 'Rust') {
        $result = & $script:rustPath @Arguments 2>&1
    }
    elseif (-not [string]::IsNullOrWhiteSpace($script:dotnetExecutablePath)) {
        $result = & $script:dotnetExecutablePath @Arguments 2>&1
    }
    else {
        $result = & $script:DotnetHost $script:dotnetPath @Arguments 2>&1
    }
    $code = $LASTEXITCODE
    $text = ($result | ForEach-Object { $_.ToString() }) -join "`n"
    if ($code -ne 0) {
        throw "$Implementation CLI exited with $code for '$($Arguments -join ' ')':`n$text"
    }
    return $text
}

function Invoke-PingetRaw {
    param(
        [ValidateSet('Rust', 'Dotnet')]
        [string] $Implementation,
        [string[]] $Arguments
    )
    if ($Implementation -eq 'Rust') {
        $result = & $script:rustPath @Arguments 2>&1
    }
    elseif (-not [string]::IsNullOrWhiteSpace($script:dotnetExecutablePath)) {
        $result = & $script:dotnetExecutablePath @Arguments 2>&1
    }
    else {
        $result = & $script:DotnetHost $script:dotnetPath @Arguments 2>&1
    }
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($result | ForEach-Object { $_.ToString() }) -join "`n"
    }
}

function ConvertFrom-PingetJson {
    param([string] $Text, [string] $Context)
    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        throw "$Context did not return valid JSON: $Text"
    }
}

function Assert-ReportFields {
    param([object] $Report, [string[]] $ExpectedFields, [string] $Context)
    $actual = @($Report.PSObject.Properties.Name | Sort-Object)
    $expected = @($ExpectedFields | Sort-Object)
    if (($actual -join "`n") -ne ($expected -join "`n")) {
        throw "$Context JSON fields differ. Expected [$($expected -join ', ')], got [$($actual -join ', ')]."
    }
}

function Assert-VerifiedReport {
    param([object] $Report, [string] $BuildId, [string] $Context)
    Assert-ReportFields $Report @('valid', 'subject', 'artifactCount', 'buildId') $Context
    if (-not $Report.valid -or $Report.buildId -ne $BuildId -or $Report.artifactCount -le 0) {
        throw "$Context did not verify build ${BuildId}: $($Report | ConvertTo-Json -Compress)"
    }
}

function Get-BuildId {
    param([string] $StateDirectory)
    $builds = Join-Path $StateDirectory 'builds'
    if (-not (Test-Path -LiteralPath $builds)) {
        throw "Publisher state did not create its builds directory: $builds"
    }
    $ids = @(Get-ChildItem -LiteralPath $builds -Directory |
        Where-Object { $_.Name -match '^[0-9a-f]{64}$' } |
        Select-Object -ExpandProperty Name)
    if ($ids.Count -ne 1) {
        throw "Expected one build snapshot under $builds; found $($ids.Count)."
    }
    return $ids[0]
}

$common = @('--output', 'json', 'repository')
$rustBuildOutput = Invoke-Pinget 'Rust' ($common + @('build', '--input', $inputPath, '--state', $rustState))
$dotnetBuildOutput = Invoke-Pinget 'Dotnet' ($common + @('build', '--input', $inputPath, '--state', $dotnetState))
$rustBuildReport = ConvertFrom-PingetJson $rustBuildOutput 'Rust build'
$dotnetBuildReport = ConvertFrom-PingetJson $dotnetBuildOutput '.NET build'
Assert-ReportFields $rustBuildReport @('buildId', 'snapshotDirectory', 'manifestCount', 'packageCount', 'changedFiles', 'removedFiles') 'Rust build'
Assert-ReportFields $dotnetBuildReport @('buildId', 'snapshotDirectory', 'manifestCount', 'packageCount', 'changedFiles', 'removedFiles') '.NET build'
$rustBuildId = Get-BuildId $rustState
$dotnetBuildId = Get-BuildId $dotnetState
if ($rustBuildId -ne $dotnetBuildId) {
    throw "Cross-language build IDs differ: Rust=$rustBuildId .NET=$dotnetBuildId"
}
if ($rustBuildReport.buildId -ne $rustBuildId -or $dotnetBuildReport.buildId -ne $dotnetBuildId) {
    throw 'Build JSON identifiers did not match the created snapshots.'
}
foreach ($buildReport in @($rustBuildReport, $dotnetBuildReport)) {
    if ($buildReport.packageCount -ne 1 -or $buildReport.manifestCount -ne 2 -or
        $buildReport.changedFiles -ne 0 -or $buildReport.removedFiles -ne 0) {
        throw "Unexpected first-build summary: $($buildReport | ConvertTo-Json -Compress)"
    }
}

foreach ($implementation in @('Rust', 'Dotnet')) {
    $statePath = if ($implementation -eq 'Rust') { $rustState } else { $dotnetState }
    foreach ($format in @('json', 'yaml', 'text')) {
        $formatted = Invoke-PingetRaw $implementation @(
            '--output', $format, 'repository', 'status', '--state', $statePath
        )
        if ($formatted.ExitCode -ne 0) {
            throw "$implementation status --output $format failed ($($formatted.ExitCode)): $($formatted.Text)"
        }
        switch ($format) {
            'json' {
                $formattedStatus = ConvertFrom-PingetJson $formatted.Text "$implementation JSON status"
                if ($formattedStatus.lastBuildId -ne $rustBuildId) {
                    throw "$implementation JSON status omitted the current build id."
                }
            }
            'yaml' {
                if ($formatted.Text -notmatch "(?m)^lastBuildId:\s*['""]?$([regex]::Escape($rustBuildId))['""]?\s*$") {
                    throw "$implementation YAML status omitted the current build id: $($formatted.Text)"
                }
            }
            'text' {
                if ($formatted.Text -notmatch [regex]::Escape($rustBuildId)) {
                    throw "$implementation text status omitted the current build id: $($formatted.Text)"
                }
            }
        }
    }
    $invalidFormat = Invoke-PingetRaw $implementation @(
        '--output', 'xml', 'repository', 'status', '--state', $statePath
    )
    if ($invalidFormat.ExitCode -eq 0) {
        throw "$implementation accepted unsupported --output xml."
    }
    Write-Output "PASS Test_PublisherCli_$($implementation)_OutputFormatParity"
}

$dotnetStatusOnRust = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Dotnet' ($common + @('status', '--state', $rustState))) '.NET status on Rust state'
$rustStatusOnDotnet = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Rust' ($common + @('status', '--state', $dotnetState))) 'Rust status on .NET state'
foreach ($statusReport in @($dotnetStatusOnRust, $rustStatusOnDotnet)) {
    Assert-ReportFields $statusReport @('lastBuildId', 'lastPublishedBuildId', 'buildCount', 'lastBuildTimestamp') 'status'
    if ($statusReport.lastBuildId -ne $rustBuildId -or $statusReport.lastPublishedBuildId -ne $null -or
        $statusReport.buildCount -ne 1 -or [string]::IsNullOrWhiteSpace($statusReport.lastBuildTimestamp)) {
        throw "Unexpected cross-open status report: $($statusReport | ConvertTo-Json -Compress)"
    }
}
$dotnetVerifyRust = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Dotnet' ($common + @('verify', '--state', $rustState, '--build', $rustBuildId))) '.NET verify of Rust snapshot'
$rustVerifyDotnet = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Rust' ($common + @('verify', '--state', $dotnetState, '--build', $dotnetBuildId))) 'Rust verify of .NET snapshot'
Assert-VerifiedReport $dotnetVerifyRust $rustBuildId '.NET verify of Rust snapshot'
Assert-VerifiedReport $rustVerifyDotnet $dotnetBuildId 'Rust verify of .NET snapshot'

$rustDoctor = ConvertFrom-PingetJson (Invoke-Pinget 'Rust' ($common + @('doctor'))) 'Rust doctor'
$dotnetDoctor = ConvertFrom-PingetJson (Invoke-Pinget 'Dotnet' ($common + @('doctor'))) '.NET doctor'
foreach ($doctorReport in @($rustDoctor, $dotnetDoctor)) {
    Assert-ReportFields $doctorReport @('operatingSystem', 'makeAppxPath', 'signToolPath', 'nativeMsZipAvailable') 'doctor'
    if ([string]::IsNullOrWhiteSpace($doctorReport.operatingSystem) -or -not $doctorReport.nativeMsZipAvailable) {
        throw "Unexpected doctor report: $($doctorReport | ConvertTo-Json -Compress)"
    }
}

$rustSnapshot = Join-Path (Join-Path $rustState 'builds') $rustBuildId
$dotnetSnapshot = Join-Path (Join-Path $dotnetState 'builds') $dotnetBuildId
foreach ($snapshot in @($rustSnapshot, $dotnetSnapshot)) {
    & $Python -B $validator --snapshot $snapshot --expect-package $ExpectedPackageId
    if ($LASTEXITCODE -ne 0) {
        throw "Independent SQLite/MSZIP snapshot validation failed for $snapshot (exit $LASTEXITCODE)."
    }
}
& $Python -B $comparer --rust-snapshot $rustSnapshot --dotnet-snapshot $dotnetSnapshot
if ($LASTEXITCODE -ne 0) {
    throw "Cross-language logical snapshot comparison failed (exit $LASTEXITCODE)."
}

$rustDiff = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Rust' ($common + @('diff', '--input', $inputPath, '--state', $rustState))) 'Rust diff'
$dotnetDiff = ConvertFrom-PingetJson `
    (Invoke-Pinget 'Dotnet' ($common + @('diff', '--input', $inputPath, '--state', $dotnetState))) '.NET diff'
foreach ($diffReport in @($rustDiff, $dotnetDiff)) {
    Assert-ReportFields $diffReport @('lastBuildId', 'published', 'added', 'changed', 'removed') 'diff'
    if ($diffReport.lastBuildId -ne $rustBuildId -or $diffReport.published -or
        @($diffReport.added).Count -ne 0 -or @($diffReport.changed).Count -ne 0 -or
        @($diffReport.removed).Count -ne 0) {
        throw "Unexpected unchanged diff report: $($diffReport | ConvertTo-Json -Compress)"
    }
}

$rustDestination = Join-Path $workPath 'rust-unsigned-feed'
$dotnetDestination = Join-Path $workPath 'dotnet-unsigned-feed'
$rustPublish = ConvertFrom-PingetJson (Invoke-Pinget 'Rust' ($common + @(
    'publish', '--state', $rustState, '--destination', $rustDestination,
    '--name', $PackageName, '--publisher', $Publisher, '--version', '1.0.0.0',
    '--unsigned-development', '--makeappx', $MakeAppxPath
))) 'Rust publish'
$dotnetPublish = ConvertFrom-PingetJson (Invoke-Pinget 'Dotnet' ($common + @(
    'publish', '--state', $dotnetState, '--destination', $dotnetDestination,
    '--name', $PackageName, '--publisher', $Publisher, '--version', '1.0.0.0',
    '--unsigned-development', '--makeappx', $MakeAppxPath
))) '.NET publish'
Assert-VerifiedReport $rustPublish $rustBuildId 'Rust publish'
Assert-VerifiedReport $dotnetPublish $dotnetBuildId '.NET publish'
if ($rustPublish.subject -ne $dotnetPublish.subject -or
    $rustPublish.artifactCount -ne $dotnetPublish.artifactCount) {
    throw 'Rust and .NET publish reports differ in verified subject or artifact count.'
}
$dotnetVerifyRustDestination = ConvertFrom-PingetJson (Invoke-Pinget 'Dotnet' ($common + @(
    'verify', '--destination', $rustDestination
))) '.NET destination verify of Rust output'
$rustVerifyDotnetDestination = ConvertFrom-PingetJson (Invoke-Pinget 'Rust' ($common + @(
    'verify', '--destination', $dotnetDestination
))) 'Rust destination verify of .NET output'
Assert-VerifiedReport $dotnetVerifyRustDestination $rustBuildId '.NET destination verify of Rust output'
Assert-VerifiedReport $rustVerifyDotnetDestination $dotnetBuildId 'Rust destination verify of .NET output'

[pscustomobject]@{
    Result = 'PASS'
    BuildId = $rustBuildId
    RustSnapshot = $rustSnapshot
    DotnetSnapshot = $dotnetSnapshot
    RustDestination = $rustDestination
    DotnetDestination = $dotnetDestination
    WorkDirectory = $workPath
    RequiredArtifacts = @(
        'Rust and .NET status/verify can reopen each other''s state'
        'Python sqlite3 and native Windows MSZIP validation passed for both snapshots'
        'Canonicalized package, correlation, manifest and input-inventory data matched'
        'Rust and .NET reciprocal destination verification passed for unsigned MSIX outputs'
    )
} | ConvertTo-Json -Depth 4
