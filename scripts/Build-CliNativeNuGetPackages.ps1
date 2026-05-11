param(
    [ValidateSet('All', 'Rust', 'DotNet')]
    [string]$Package = 'All',

    [string]$Version,

    [string]$Configuration = 'Release',

    [string]$StagingRoot,

    [string]$OutputRoot,

    [string[]]$RustRuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [string[]]$DotNetRuntimeIdentifiers = @('win-x64', 'win-arm64'),

    [switch]$NoBuild,

    [switch]$NoPack,

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($StagingRoot)) {
    $StagingRoot = Join-Path $repoRoot 'artifacts\cli'
}
elseif (-not [System.IO.Path]::IsPathRooted($StagingRoot)) {
    $StagingRoot = Join-Path $repoRoot $StagingRoot
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\cli-nuget'
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList
    )

    Write-Host ">> $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

function Get-SourceVersion {
    $rustCliManifest = Join-Path $repoRoot 'rust\crates\pinget-cli\Cargo.toml'
    $dotNetCliProgram = Join-Path $repoRoot 'dotnet\src\Devolutions.Pinget.Cli\Program.cs'

    $rustMatch = Select-String -Path $rustCliManifest -Pattern '^version = "([^"]+)"$' | Select-Object -First 1
    $dotNetMatch = Select-String -Path $dotNetCliProgram -Pattern '^const string Version = "([^"]+)";$' | Select-Object -First 1

    if (($null -eq $rustMatch) -or ($null -eq $dotNetMatch)) {
        throw 'Unable to detect CLI package version from source files.'
    }

    $rustVersion = $rustMatch.Matches[0].Groups[1].Value
    $dotNetVersion = $dotNetMatch.Matches[0].Groups[1].Value
    if ($rustVersion -ne $dotNetVersion) {
        throw "CLI version mismatch detected: rust=$rustVersion dotnet=$dotNetVersion"
    }

    $rustVersion
}

function Resolve-RustTarget {
    param([Parameter(Mandatory)][string]$RuntimeIdentifier)

    switch ($RuntimeIdentifier) {
        'win-x64' { @{ CargoTarget = 'x86_64-pc-windows-msvc'; SourceBinaryName = 'pinget.exe'; PackageBinaryName = 'pinget.exe' } }
        'win-arm64' { @{ CargoTarget = 'aarch64-pc-windows-msvc'; SourceBinaryName = 'pinget.exe'; PackageBinaryName = 'pinget.exe' } }
        'linux-x64' { @{ CargoTarget = 'x86_64-unknown-linux-gnu'; SourceBinaryName = 'pinget'; PackageBinaryName = 'pinget' } }
        'linux-arm64' { @{ CargoTarget = 'aarch64-unknown-linux-gnu'; SourceBinaryName = 'pinget'; PackageBinaryName = 'pinget' } }
        'osx-x64' { @{ CargoTarget = 'x86_64-apple-darwin'; SourceBinaryName = 'pinget'; PackageBinaryName = 'pinget' } }
        'osx-arm64' { @{ CargoTarget = 'aarch64-apple-darwin'; SourceBinaryName = 'pinget'; PackageBinaryName = 'pinget' } }
        default { throw "Unsupported Rust runtime identifier: $RuntimeIdentifier" }
    }
}

function Get-DotNetBinaryNames {
    param([Parameter(Mandatory)][string]$RuntimeIdentifier)

    switch ($RuntimeIdentifier) {
        'win-x64' { @{ SourceBinaryName = 'pinget.exe'; PackageBinaryName = 'pinget.exe' } }
        'win-arm64' { @{ SourceBinaryName = 'pinget.exe'; PackageBinaryName = 'pinget.exe' } }
        default { throw "Unsupported .NET NativeAOT runtime identifier: $RuntimeIdentifier" }
    }
}

function Assert-FileExists {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        throw "Expected file was not found: $Path"
    }
}

function Set-NupkgUnixExecutablePermissions {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::Open($PackagePath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -match '^runtimes/(linux|osx)-[^/]+/native/pinget$') {
                $entry.ExternalAttributes = -2115174400 # 0o100755 << 16 as a signed Int32.
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-SourceVersion
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Package version is empty.'
}

$buildRust = $Package -in @('All', 'Rust')
$buildDotNet = $Package -in @('All', 'DotNet')

if ($Clean) {
    if ($buildRust) {
        Remove-Item -Path (Join-Path $StagingRoot 'rust') -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($buildDotNet) {
        Remove-Item -Path (Join-Path $StagingRoot 'dotnet-nativeaot') -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Path $OutputRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -Path $StagingRoot -ItemType Directory -Force | Out-Null
New-Item -Path $OutputRoot -ItemType Directory -Force | Out-Null

if ($buildRust) {
    $cargoManifest = Join-Path $repoRoot 'rust\Cargo.toml'

    foreach ($rid in $RustRuntimeIdentifiers) {
        $target = Resolve-RustTarget -RuntimeIdentifier $rid
        $stageDir = Join-Path $StagingRoot "rust\$rid"
        New-Item -Path $stageDir -ItemType Directory -Force | Out-Null

        if (-not $NoBuild) {
            Invoke-NativeCommand -FilePath rustup -ArgumentList @('target', 'add', $target['CargoTarget'])
            Invoke-NativeCommand -FilePath cargo -ArgumentList @('build', '--release', '--package', 'pinget-cli', '--bin', 'pinget', '--manifest-path', $cargoManifest, '--target', $target['CargoTarget'])

            $builtBinary = Join-Path $repoRoot "rust\target\$($target['CargoTarget'])\release\$($target['SourceBinaryName'])"
            Assert-FileExists -Path $builtBinary
            Copy-Item -Path $builtBinary -Destination (Join-Path $stageDir $target['PackageBinaryName']) -Force
        }

        Assert-FileExists -Path (Join-Path $stageDir $target['PackageBinaryName'])
    }
}

if ($buildDotNet) {
    $dotNetProject = Join-Path $repoRoot 'dotnet\src\Devolutions.Pinget.Cli\Devolutions.Pinget.Cli.csproj'

    foreach ($rid in $DotNetRuntimeIdentifiers) {
        $binaryNames = Get-DotNetBinaryNames -RuntimeIdentifier $rid
        $stageDir = Join-Path $StagingRoot "dotnet-nativeaot\$rid"
        New-Item -Path $stageDir -ItemType Directory -Force | Out-Null

        if (-not $NoBuild) {
            Invoke-NativeCommand -FilePath dotnet -ArgumentList @(
                'publish',
                $dotNetProject,
                '-c',
                $Configuration,
                '-r',
                $rid,
                '--self-contained',
                'true',
                '-o',
                $stageDir,
                '/p:PublishAot=true',
                '/p:PublishTrimmed=true',
                "/p:Version=$Version",
                "/p:AssemblyVersion=$Version",
                "/p:FileVersion=$Version",
                "/p:InformationalVersion=$Version"
            )

            $sourceBinary = Join-Path $stageDir $binaryNames['SourceBinaryName']
            $packageBinary = Join-Path $stageDir $binaryNames['PackageBinaryName']
            Assert-FileExists -Path $sourceBinary
            if ($sourceBinary -ne $packageBinary) {
                Move-Item -Path $sourceBinary -Destination $packageBinary -Force
            }
        }

        Assert-FileExists -Path (Join-Path $stageDir $binaryNames['PackageBinaryName'])
    }
}

if (-not $NoPack) {
    if ($buildRust) {
        $rustPackageProject = Join-Path $repoRoot 'nuget\Devolutions.Pinget.Cli.Rust\Devolutions.Pinget.Cli.Rust.csproj'
        Invoke-NativeCommand -FilePath dotnet -ArgumentList @('pack', $rustPackageProject, '-c', $Configuration, '-o', $OutputRoot, "/p:Version=$Version", '/p:ContinuousIntegrationBuild=true')

        $rustPackage = Join-Path $OutputRoot "Devolutions.Pinget.Cli.Rust.$Version.nupkg"
        Assert-FileExists -Path $rustPackage
        Set-NupkgUnixExecutablePermissions -PackagePath $rustPackage
    }

    if ($buildDotNet) {
        $dotNetPackageProject = Join-Path $repoRoot 'nuget\Devolutions.Pinget.Cli.DotNet\Devolutions.Pinget.Cli.DotNet.csproj'
        Invoke-NativeCommand -FilePath dotnet -ArgumentList @('pack', $dotNetPackageProject, '-c', $Configuration, '-o', $OutputRoot, "/p:Version=$Version", '/p:ContinuousIntegrationBuild=true')

        Assert-FileExists -Path (Join-Path $OutputRoot "Devolutions.Pinget.Cli.DotNet.$Version.nupkg")
    }
}
