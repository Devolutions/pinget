param(
    [string] $WorkRoot = 'C:\WinApp\work\PublisherContract',
    [string] $SourceName = 'pinget-publisher-contract',
    [string] $BaseUrl = 'https://127.0.0.1:8765/',
    [switch] $ReuseExistingSource,
    [string] $SignerCertificatePath,
    [ValidateSet('Sandbox', 'EphemeralHost')]
    [string] $ExecutionTarget = 'EphemeralHost',
    [switch] $AllowEphemeralHostMutations,
    [string] $ExpectedWinGetVersion = '1.29.380.0',
    [string] $ExpectedWinGetClientVersion,
    [string] $PublisherCliPath,
    [string] $PublisherStateDirectory,
    [string] $PublisherInputDirectory,
    [string] $PfxPath,
    [string] $PfxPasswordEnvironmentVariable,
    [string] $RustReaderCliPath,
    [string] $DotnetReaderCliPath,
    [string] $PublisherName = 'Pinget.Publisher.Conformance',
    [string] $PublisherIdentity = 'CN=Pinget Publisher Conformance Test',
    [string] $InitialPackageVersion = '1.0.0.0',
    [string] $MakeAppxPath,
    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'
if ($ExecutionTarget -eq 'EphemeralHost' -and -not $AllowEphemeralHostMutations) {
    throw 'Host stock-source/certificate mutations are disabled. Use -ExecutionTarget Sandbox, or explicitly opt in with -AllowEphemeralHostMutations on a disposable CI runner.'
}
if ($ExecutionTarget -eq 'Sandbox' -and $AllowEphemeralHostMutations) {
    throw '-AllowEphemeralHostMutations is valid only for an explicitly disposable host runner.'
}
$publisherArguments = @(
    $PublisherCliPath, $PublisherStateDirectory, $PublisherInputDirectory,
    $PfxPath, $PfxPasswordEnvironmentVariable, $MakeAppxPath, $SignToolPath
)
if ($publisherArguments.Where({ -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 -and
    $publisherArguments.Where({ [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw 'Production publisher validation requires CLI, state, input, PFX, password environment variable, MakeAppx, and SignTool paths together.'
}
$readerArguments = @($RustReaderCliPath, $DotnetReaderCliPath)
if ($readerArguments.Where({ -not [string]::IsNullOrWhiteSpace($_) }).Count -notin @(0, 2)) {
    throw 'Pinget reader conformance requires both the Rust and .NET CLI paths.'
}
$feed = Join-Path $WorkRoot 'feed'
$serverScript = Join-Path $WorkRoot 'serve-feed.ps1'
$serverCertificate = Join-Path $WorkRoot 'tls-server.pfx'
$downloadDirectory = Join-Path $WorkRoot ('downloaded-' + [Guid]::NewGuid().ToString('N'))
$packageId = 'Pinget.Contract.Singleton'
$addedPackageId = 'Pinget.Contract.Added'
$multiBlockPackageId = 'Pinget.Contract.MultiBlock'
$expectedPayload = 'Harmless Pinget contract fixture. Download only; never execute.' + "`n"
$expectedHash = '96c3a679ab1dc50990f84b7610f41bad666d92a82aceaf2edb8674a53587f5b7'
$script:addedTrustThumbprints = [System.Collections.Generic.List[string]]::new()
$script:sourceAdded = $false

function Add-TestRootCertificate {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return
    }
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($Path)
    $rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    )
    try {
        $rootStore.Open(
            [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        $alreadyTrusted = $rootStore.Certificates |
            Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }
        if ($null -eq $alreadyTrusted) {
            $rootStore.Add($certificate)
            $script:addedTrustThumbprints.Add($certificate.Thumbprint)
        }
    }
    finally {
        $rootStore.Close()
        $certificate.Dispose()
    }
}

function Remove-TestRootCertificates {
    if ($script:addedTrustThumbprints.Count -eq 0) {
        return
    }
    $rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
    )
    try {
        $rootStore.Open(
            [System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        foreach ($thumbprint in $script:addedTrustThumbprints) {
            $matches = @($rootStore.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint })
            foreach ($certificate in $matches) {
                $rootStore.Remove($certificate)
                $certificate.Dispose()
            }
        }
    }
    finally {
        $rootStore.Close()
    }
}

function Invoke-PublisherCommand {
    param([string[]] $Arguments)
    $output = & $script:PublisherCliPath --output json repository @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    if ($exitCode -ne 0) {
        throw "Production publisher CLI exited with $exitCode for '$($Arguments -join ' ')':`n$text"
    }
    try {
        return $text | ConvertFrom-Json
    }
    catch {
        throw "Production publisher CLI did not return valid JSON for '$($Arguments -join ' ')':`n$text"
    }
}

function Publish-ProductionInput {
    param([string] $InputDirectory, [string] $PackageVersion)
    $build = Invoke-PublisherCommand @(
        'build', '--input', $InputDirectory, '--state', $script:PublisherStateDirectory
    )
    if ([string]::IsNullOrWhiteSpace($build.buildId)) {
        throw 'Production publisher build returned no buildId.'
    }
    $published = Invoke-PublisherCommand @(
        'publish', '--state', $script:PublisherStateDirectory,
        '--destination', $script:feed, '--name', $script:PublisherName,
        '--publisher', $script:PublisherIdentity, '--version', $PackageVersion,
        '--pfx', $script:PfxPath, '--password-env', $script:PfxPasswordEnvironmentVariable,
        '--makeappx', $script:MakeAppxPath, '--signtool', $script:SignToolPath,
        '--build', $build.buildId
    )
    if (-not $published.valid) {
        throw "Production publisher did not verify its signed output: $($published.message)"
    }
    return $build.buildId
}

function Invoke-PingetReaderCommand {
    param(
        [string] $CliPath,
        [string] $AppRoot,
        [string[]] $Arguments
    )
    $previousAppRoot = [Environment]::GetEnvironmentVariable('PINGET_APPROOT', 'Process')
    $previousSourceMode = [Environment]::GetEnvironmentVariable('PINGET_SOURCE_MODE', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('PINGET_APPROOT', $AppRoot, 'Process')
        [Environment]::SetEnvironmentVariable('PINGET_SOURCE_MODE', 'private', 'Process')
        $output = & $CliPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
        if ($exitCode -ne 0) {
            throw "Pinget reader CLI exited with $exitCode for '$($Arguments -join ' ')':`n$text"
        }
        return $text
    }
    finally {
        [Environment]::SetEnvironmentVariable('PINGET_APPROOT', $previousAppRoot, 'Process')
        [Environment]::SetEnvironmentVariable('PINGET_SOURCE_MODE', $previousSourceMode, 'Process')
    }
}

function Test_PingetReader_SearchAndShowReadsMultiBlockVersionData {
    if ($script:ReaderCliPaths.Count -eq 0) {
        return
    }

    $winGetSourcesBefore = (Invoke-WinGetChecked @(
        'source', 'list', '--disable-interactivity'
    )).Trim()
    foreach ($reader in $script:ReaderCliPaths) {
        $appRoot = Join-Path $script:WorkRoot "pinget-reader-$($reader.Name.ToLowerInvariant())"
        if (Test-Path -LiteralPath $appRoot) {
            throw "Refusing to overwrite app-private Pinget source root: $appRoot"
        }
        $readerSourceName = "pinget-private-reader-$($reader.Name.ToLowerInvariant())"
        $sourceAdded = $false
        try {
            $null = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                'source', 'add', '--name', $readerSourceName,
                '--arg', $script:BaseUrl, '--type', 'Microsoft.PreIndexed.Package',
                '--trust-level', 'Trusted'
            )
            $sourceAdded = $true
            $null = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                'source', 'update', $readerSourceName
            )
            $search = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                'search', '--id', $script:multiBlockPackageId,
                '--source', $readerSourceName, '--exact'
            )
            if ($search -notmatch [regex]::Escape($script:multiBlockPackageId) -or
                $search -notmatch '(?m)\b1\.0\.499\b') {
                throw "$($reader.Name) search did not return the final multi-block version 1.0.499."
            }

            $show = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                'show', '--id', $script:multiBlockPackageId,
                '--source', $readerSourceName, '--exact'
            )
            if ($show -notmatch [regex]::Escape($script:multiBlockPackageId) -or
                $show -notmatch '(?m)^Version:\s*1\.0\.499\s*$') {
                throw "$($reader.Name) show did not read the final version 1.0.499."
            }

            $versions = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                'show', '--id', $script:multiBlockPackageId,
                '--source', $readerSourceName, '--exact', '--versions'
            )
            $versionLines = @(
                $versions -split '\r?\n' |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { $_ -match '^1\.0\.\d{1,3}$' }
            )
            if ($versionLines.Count -ne 500 -or $versionLines[0] -ne '1.0.499') {
                throw "$($reader.Name) show --versions did not return all 500 descending versions with 1.0.499 first."
            }
        }
        finally {
            if ($sourceAdded) {
                $null = Invoke-PingetReaderCommand $reader.Path $appRoot @(
                    'source', 'remove', $readerSourceName
                )
            }
        }

        $winGetSourcesAfter = (Invoke-WinGetChecked @(
            'source', 'list', '--disable-interactivity'
        )).Trim()
        if ($winGetSourcesAfter -ne $winGetSourcesBefore) {
            throw "$($reader.Name) private source operations changed the stock WinGet source list."
        }
        Write-Output "PASS Test_Pinget_$($reader.Name)_SearchAndShowReadsMultiBlockVersionData"
    }
}

function New-AddedPackageManifest {
    return @'
PackageIdentifier: Pinget.Contract.Added
PackageVersion: 1.0.0
PackageLocale: en-US
Publisher: Pinget Test Fixtures
PublisherUrl: https://example.invalid/
PackageName: Pinget Added Fixture
License: MIT
ShortDescription: Harmless added publisher conformance package
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://127.0.0.1:8765/HarmlessPayload.txt
    InstallerSha256: 96c3a679ab1dc50990f84b7610f41bad666d92a82aceaf2edb8674a53587f5b7
    Scope: user
ManifestType: singleton
ManifestVersion: 1.9.0
'@
}

function Initialize-MultiBlockPublisherInput {
    $directory = Join-Path $script:PublisherInputDirectory 'multiblock'
    if (Test-Path -LiteralPath $directory) {
        $existing = @(Get-ChildItem -LiteralPath $directory -Filter '*.yaml' -File)
        if ($existing.Count -ne 500) {
            throw "Expected the existing multi-block fixture to contain exactly 500 manifests; found $($existing.Count)."
        }
        return
    }

    New-Item -ItemType Directory -Path $directory | Out-Null
    for ($i = 0; $i -lt 500; $i++) {
        $version = "1.0.$i"
        $manifest = @"
PackageIdentifier: $script:multiBlockPackageId
PackageVersion: $version
PackageLocale: en-US
Publisher: Pinget Test Fixtures
PublisherUrl: https://example.invalid/
PackageName: Pinget Multi-Block Fixture
License: MIT
ShortDescription: Large harmless version-data fixture
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.invalid/Pinget.MultiBlock/$version.exe
    InstallerSha256: $([string]::new('a', 64))
    Scope: user
ManifestType: singleton
ManifestVersion: 1.9.0
"@
        $path = Join-Path $directory ("version-{0:D3}.yaml" -f $i)
        [System.IO.File]::WriteAllText(
            $path,
            $manifest + "`n",
            [System.Text.UTF8Encoding]::new($false))
    }
}

function Invoke-UpdateSource {
    $null = Invoke-WinGetChecked @(
        'source', 'update', '--name', $script:SourceName, '--disable-interactivity'
    )
}

function Invoke-WinGetRaw {
    param([string[]] $Arguments)
    $output = & $script:winget @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Text = ($output | ForEach-Object { $_.ToString() }) -join "`n"
    }
}

function Invoke-WinGetChecked {
    param([string[]] $Arguments)
    Write-Host ('COMMAND: winget ' + ($Arguments -join ' '))
    $output = & $script:winget @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "winget exited with $exitCode for: $($Arguments -join ' ')"
    }
    return ($output -join "`n")
}

function Test_StockWinGet_SourceAddAndUpdate {
    if ($ReuseExistingSource) {
        $existing = Invoke-WinGetChecked @(
            'source', 'list', '--name', $script:SourceName,
            '--disable-interactivity'
        )
        $matchesExpectedSource =
            $existing -match ('(?m)^\s*Name\s+' + [regex]::Escape($script:SourceName) + '\s*$') -and
            $existing -match '(?m)^\s*Type\s+Microsoft\.PreIndexed\.Package\s*$' -and
            $existing -match ('(?m)^\s*Argument\s+' + [regex]::Escape($script:BaseUrl) + '\s*$') -and
            $existing -match '(?m)^\s*Trust Level\s+Trusted\s*$'
        if (-not $matchesExpectedSource) {
            throw "Existing WinGet source '$($script:SourceName)' does not match the requested production feed configuration."
        }
        Write-Output 'PASS Test_StockWinGet_SourceReuseMatchesProductionFeed'
    }
    else {
        $null = Invoke-WinGetChecked @(
            'source', 'add', '--name', $script:SourceName,
            '--arg', $script:BaseUrl, '--type', 'Microsoft.PreIndexed.Package',
            '--trust-level', 'trusted', '--accept-source-agreements'
        )
        $script:sourceAdded = $true
        Write-Output 'PASS Test_StockWinGet_SourceAdd'
    }
    $null = Invoke-WinGetChecked @(
        'source', 'update', '--name', $script:SourceName,
        '--disable-interactivity'
    )
    Write-Output 'PASS Test_StockWinGet_SourceUpdate'
}

function Test_StockWinGet_SearchFindsFixture {
    $output = Invoke-WinGetChecked @(
        'search', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($output -notmatch [regex]::Escape($script:packageId)) {
        throw 'Search output did not contain the exact fixture package identifier.'
    }
    Write-Output 'PASS Test_StockWinGet_SearchFindsFixture'
}

function Test_StockWinGet_ShowReadsFixtureManifest {
    $output = Invoke-WinGetChecked @(
        'show', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($output -notmatch 'Pinget Contract Fixture' -or
        $output -notmatch '1\.0\.0') {
        throw 'Show output did not expose the expected name and version.'
    }
    Write-Output 'PASS Test_StockWinGet_ShowReadsFixtureManifest'
}

function Test_StockWinGet_ShowReadsMultiBlockVersionData {
    $output = Invoke-WinGetChecked @(
        'show', '--id', $script:multiBlockPackageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($output -notmatch [regex]::Escape($script:multiBlockPackageId) -or
        $output -notmatch [regex]::Escape('1.0.499') -or
        $output -notmatch 'Pinget Multi-Block Fixture') {
        throw 'Show output did not expose the last version from the multi-block version-data sidecar.'
    }
    Write-Output 'PASS Test_StockWinGet_ShowReadsMultiBlockVersionData'
}

function Test_StockWinGet_DownloadVerifiesHarmlessPayload {
    if (Test-Path $script:downloadDirectory) {
        throw "Refusing to overwrite download evidence at $script:downloadDirectory"
    }
    New-Item -ItemType Directory -Path $script:downloadDirectory | Out-Null
    $null = Invoke-WinGetChecked @(
        'download', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--download-directory',
        $script:downloadDirectory, '--accept-source-agreements',
        '--accept-package-agreements', '--disable-interactivity'
    )
    $payload = @(
        Get-ChildItem -LiteralPath $script:downloadDirectory -File |
            Where-Object {
                (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq
                    $script:expectedHash
            }
    )
    if ($payload.Count -ne 1) {
        throw "Expected one downloaded file with the fixture payload hash; found $($payload.Count)."
    }
    $actualHash = (Get-FileHash -LiteralPath $payload.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $script:expectedHash) {
        throw "Downloaded payload hash mismatch: $actualHash"
    }
    $actualText = [System.IO.File]::ReadAllText($payload.FullName)
    if ($actualText -ne $script:expectedPayload) {
        throw 'Downloaded payload bytes did not match the locally authored fixture.'
    }
    Write-Output 'PASS Test_StockWinGet_DownloadVerifiesHarmlessPayload'
}

function Test_StockWinGet_SourceVersionBumpAddRemove {
    if ([string]::IsNullOrWhiteSpace($script:PublisherCliPath)) {
        return
    }

    $bumpInput = Join-Path $script:WorkRoot 'version-bump-input'
    $removeInput = Join-Path $script:WorkRoot 'remove-package-input'
    foreach ($path in @($bumpInput, $removeInput)) {
        if (Test-Path -LiteralPath $path) {
            throw "Refusing to overwrite publisher update fixture directory: $path"
        }
    }
    Copy-Item -LiteralPath $script:PublisherInputDirectory -Destination $bumpInput -Recurse
    $baseManifest = Join-Path $bumpInput 'Pinget.Contract.Singleton.yaml'
    $manifestText = [System.IO.File]::ReadAllText($baseManifest)
    $manifestText = [regex]::Replace($manifestText, '(?m)^PackageVersion: 1\.0\.0\r?$', 'PackageVersion: 1.1.0')
    $manifestText = [regex]::Replace($manifestText, '(?m)^PackageName: Pinget Contract Fixture\r?$', 'PackageName: Pinget Contract Fixture Updated')
    $manifestText = [regex]::Replace($manifestText, '(?m)^ShortDescription: .*$', 'ShortDescription: Updated harmless production publisher conformance fixture')
    [System.IO.File]::WriteAllText(
        $baseManifest,
        $manifestText,
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $bumpInput 'Pinget.Contract.Added.yaml'),
        (New-AddedPackageManifest),
        [System.Text.UTF8Encoding]::new($false))

    $null = Publish-ProductionInput $bumpInput '1.0.0.1'
    $cached = Invoke-WinGetChecked @(
        'show', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($cached -notmatch '1\.0\.0' -or
        $cached -notmatch 'Pinget Contract Fixture' -or
        $cached -match '1\.1\.0') {
        throw 'Cached source metadata changed before an explicit source update during publication.'
    }
    Write-Output 'PASS Test_StockWinGet_CachedPreviousIndexRetainsMetadataDuringPublish'

    $null = Invoke-WinGetChecked @(
        'source', 'update', '--name', $script:SourceName, '--disable-interactivity'
    )
    $updated = Invoke-WinGetChecked @(
        'show', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($updated -notmatch '1\.1\.0' -or
        $updated -notmatch 'Pinget Contract Fixture Updated') {
        throw 'Updated source did not expose the bumped package version and metadata.'
    }
    $added = Invoke-WinGetChecked @(
        'search', '--id', $script:addedPackageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($added -notmatch [regex]::Escape($script:addedPackageId)) {
        throw 'Source update did not expose the newly added package.'
    }
    Write-Output 'PASS Test_StockWinGet_SourceVersionBumpAndPackageAddition'

    Copy-Item -LiteralPath $bumpInput -Destination $removeInput -Recurse
    Remove-Item -LiteralPath (Join-Path $removeInput 'Pinget.Contract.Singleton.yaml')
    $null = Publish-ProductionInput $removeInput '1.0.0.2'
    $cachedBeforeRemoval = Invoke-WinGetChecked @(
        'show', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($cachedBeforeRemoval -notmatch '1\.1\.0' -or
        $cachedBeforeRemoval -notmatch 'Pinget Contract Fixture Updated') {
        throw 'Cached prior-index metadata was not available while the removal publication was staged.'
    }

    $null = Invoke-WinGetChecked @(
        'source', 'update', '--name', $script:SourceName, '--disable-interactivity'
    )
    $removed = Invoke-WinGetRaw @(
        'search', '--id', $script:packageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($removed.Text -match [regex]::Escape($script:packageId)) {
        throw 'Removed package remained searchable after the source update.'
    }
    $stillPresent = Invoke-WinGetChecked @(
        'search', '--id', $script:addedPackageId, '--exact',
        '--source', $script:SourceName, '--accept-source-agreements',
        '--disable-interactivity'
    )
    if ($stillPresent -notmatch [regex]::Escape($script:addedPackageId)) {
        throw 'Unremoved package disappeared from the updated source.'
    }

    $marker = Get-Content -LiteralPath (Join-Path $script:feed '.pinget-publisher.json') -Raw |
        ConvertFrom-Json
    $retainedOldManifest = $marker.artifacts.PSObject.Properties.Name |
        Where-Object { $_ -like 'manifests/Pinget.Contract.Singleton/1.0.0/*' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($retainedOldManifest) -or
        -not (Test-Path -LiteralPath (Join-Path $script:feed $retainedOldManifest.Replace('/', '\')))) {
        throw 'The removal publication did not retain the previously published immutable manifest.'
    }
    $verified = Invoke-PublisherCommand @(
        'verify', '--destination', $script:feed, '--signtool', $script:SignToolPath
    )
    if (-not $verified.valid) {
        throw 'Published destination did not verify after add/remove updates.'
    }
    Write-Output 'PASS Test_StockWinGet_SourceRemovalDropsPackageAndRetainsPublishedArtifacts'
}

$script:WorkRoot = $WorkRoot
$script:feed = $feed
$script:PublisherCliPath = $PublisherCliPath
$script:PublisherStateDirectory = $PublisherStateDirectory
$script:PublisherInputDirectory = $PublisherInputDirectory
$script:PfxPath = $PfxPath
$script:PfxPasswordEnvironmentVariable = $PfxPasswordEnvironmentVariable
$script:ReaderCliPaths = @(
    if (-not [string]::IsNullOrWhiteSpace($RustReaderCliPath)) {
        [pscustomobject]@{ Name = 'Rust'; Path = $RustReaderCliPath }
    }
    if (-not [string]::IsNullOrWhiteSpace($DotnetReaderCliPath)) {
        [pscustomobject]@{ Name = 'Dotnet'; Path = $DotnetReaderCliPath }
    }
)
$script:PublisherName = $PublisherName
$script:PublisherIdentity = $PublisherIdentity
$script:MakeAppxPath = $MakeAppxPath
$script:SignToolPath = $SignToolPath
$script:SourceName = $SourceName
$script:BaseUrl = $BaseUrl
$script:packageId = $packageId
$script:addedPackageId = $addedPackageId
$script:multiBlockPackageId = $multiBlockPackageId
$server = $null

try {
    $publisherArguments = @(
        $script:PublisherCliPath, $script:PublisherStateDirectory, $script:PublisherInputDirectory,
        $script:PfxPath, $script:PfxPasswordEnvironmentVariable, $script:MakeAppxPath, $script:SignToolPath
    )
    $presentCount = @($publisherArguments | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    if ($presentCount -ne 0 -and $presentCount -ne $publisherArguments.Count) {
        throw 'Production publisher validation requires CLI, state, input, PFX, password environment variable, MakeAppx, and SignTool paths together.'
    }

    foreach ($requiredPath in @($serverScript, $serverCertificate)) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Required stock WinGet fixture server input is missing: $requiredPath"
        }
    }
    foreach ($readerPath in @($RustReaderCliPath, $DotnetReaderCliPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (-not (Test-Path -LiteralPath $readerPath)) {
            throw "Required Pinget reader CLI does not exist: $readerPath"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($script:PublisherCliPath)) {
        if ([string]::IsNullOrWhiteSpace($SignerCertificatePath)) {
            throw 'Signed production publishing requires -SignerCertificatePath so the test certificate can be trusted only on the selected test machine.'
        }
        if (-not (Test-Path -LiteralPath $script:PublisherInputDirectory)) {
            throw "Production publisher input is missing: $script:PublisherInputDirectory"
        }
        if (-not (Test-Path -LiteralPath $script:PfxPath)) {
            throw "Production publisher PFX is missing: $script:PfxPath"
        }
        if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($script:PfxPasswordEnvironmentVariable))) {
            throw "The PFX password environment variable '$script:PfxPasswordEnvironmentVariable' is not set."
        }
        if (-not (Test-Path -LiteralPath $script:MakeAppxPath) -or
            -not (Test-Path -LiteralPath $script:SignToolPath)) {
            throw 'The supplied MakeAppx or SignTool path does not exist on the selected test machine.'
        }
        if (Test-Path -LiteralPath (Join-Path $feed 'source2.msix')) {
            throw "Refusing to publish over an existing feed: $feed"
        }
        New-Item -ItemType Directory -Force -Path $feed | Out-Null
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $feed 'source2.msix'))) {
        throw "Fixture source2.msix not found under $feed"
    }

    if (-not [string]::IsNullOrWhiteSpace($SignerCertificatePath)) {
        Add-TestRootCertificate $SignerCertificatePath
        Write-Output 'Trusted the ephemeral production signer only in the selected test machine Root store.'
    }
    Add-TestRootCertificate (Join-Path $WorkRoot 'tls-server.cer')

    if (-not [string]::IsNullOrWhiteSpace($script:PublisherCliPath)) {
        Initialize-MultiBlockPublisherInput
        $null = Publish-ProductionInput $script:PublisherInputDirectory $InitialPackageVersion
    }

    $appInstaller = Get-AppxPackage -Name Microsoft.DesktopAppInstaller
    if ($null -eq $appInstaller -or $appInstaller.Version.ToString() -ne $ExpectedWinGetVersion) {
        $actualVersion = if ($null -eq $appInstaller) { '<missing>' } else { $appInstaller.Version.ToString() }
        throw "Expected stock WinGet $ExpectedWinGetVersion, found $actualVersion"
    }
    $script:winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (-not (Test-Path $script:winget)) {
        $command = Get-Command winget.exe -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            throw 'WinGet package registered but winget.exe execution alias is unavailable.'
        }
        $script:winget = $command.Source
    }
    $versionOutput = & $script:winget --version 2>&1
    $expectedClientVersion = if ([string]::IsNullOrWhiteSpace($ExpectedWinGetClientVersion)) {
        $ExpectedWinGetVersion -replace '\.0$', ''
    } else {
        $ExpectedWinGetClientVersion
    }
    if ($LASTEXITCODE -ne 0 -or ($versionOutput -join '') -notmatch [regex]::Escape($expectedClientVersion)) {
        throw "Unexpected WinGet client version: $($versionOutput -join ' ')"
    }
    Write-Output "Stock WinGet: $($versionOutput -join ' ')"

    $serverStdout = Join-Path $WorkRoot 'server.stdout.txt'
    $serverStderr = Join-Path $WorkRoot 'server.stderr.txt'
    $harmlessPayloadPath = Join-Path $WorkRoot 'HarmlessPayload.txt'
    [System.IO.File]::WriteAllText(
        $harmlessPayloadPath,
        $expectedPayload,
        [System.Text.UTF8Encoding]::new($false)
    )
    $server = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', $serverScript, '-Root', $feed, '-CertificatePath',
            $serverCertificate, '-Port', '8765', '-HarmlessPayloadPath',
            $harmlessPayloadPath) `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $serverStdout -RedirectStandardError $serverStderr
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri ($script:BaseUrl + 'source2.msix') `
                -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) {
        $serverError = if (Test-Path $serverStderr) {
            Get-Content -LiteralPath $serverStderr -Raw
        }
        else {
            '<no server stderr file>'
        }
        throw "Sandbox-local fixture server did not become ready. Server stderr: $serverError"
    }
    Write-Output 'PASS Test_StockWinGet_FeedServedOnSandboxLoopback'
    Test_PingetReader_SearchAndShowReadsMultiBlockVersionData
    Test_StockWinGet_SourceAddAndUpdate
    Test_StockWinGet_SearchFindsFixture
    Test_StockWinGet_ShowReadsFixtureManifest
    if (-not [string]::IsNullOrWhiteSpace($script:PublisherCliPath)) {
        Test_StockWinGet_ShowReadsMultiBlockVersionData
    }
    Test_StockWinGet_DownloadVerifiesHarmlessPayload
    Test_StockWinGet_SourceVersionBumpAddRemove
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id
    }
    if ($script:sourceAdded) {
        try {
            & $script:winget source remove --name $SourceName --disable-interactivity 2>&1 | Out-Null
        }
        catch {
            Write-Warning "Could not remove temporary WinGet source '$SourceName': $($_.Exception.Message)"
        }
    }
    try {
        Remove-TestRootCertificates
    }
    catch {
        Write-Warning "Could not remove test-added certificate trust: $($_.Exception.Message)"
    }
}
