param(
    [ValidateSet('All', 'Rust', 'DotNet', 'Tool')]
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

function Get-Win32VersionParts {
    param([Parameter(Mandatory)][string]$Version)

    $parts = @($Version.Split('+')[0].Split('-')[0].Split('.') | ForEach-Object {
            if ($_ -notmatch '^\d+$') {
                throw "Version segment is not numeric: $_"
            }

            $value = [int]$_
            if (($value -lt 0) -or ($value -gt 65535)) {
                throw "Version segment is outside the Win32 VERSIONINFO range: $_"
            }

            $value
        })

    while ($parts.Count -lt 4) {
        $parts += 0
    }

    if ($parts.Count -gt 4) {
        throw "Win32 VERSIONINFO supports at most four version segments: $Version"
    }

    $parts
}

function Resolve-WindowsResourceCompiler {
    $rcCommand = Get-Command rc.exe -ErrorAction SilentlyContinue
    if ($null -ne $rcCommand) {
        return $rcCommand.Source
    }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -Path $windowsKitsRoot -PathType Container) {
        $kitVersions = @(Get-ChildItem -Path $windowsKitsRoot -Directory | Sort-Object -Property Name -Descending)
        foreach ($kitVersion in $kitVersions) {
            foreach ($arch in @('x64', 'x86', 'arm64')) {
                $candidate = Join-Path $kitVersion.FullName "$arch\rc.exe"
                if (Test-Path -Path $candidate -PathType Leaf) {
                    return $candidate
                }
            }
        }
    }

    throw 'Unable to locate rc.exe from the Windows SDK.'
}

function New-PingetWindowsVersionResource {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$Version
    )

    if (-not $IsWindows) {
        throw 'Windows version resources can only be generated on Windows.'
    }

    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null

    $versionParts = @(Get-Win32VersionParts -Version $Version)
    $versionCsv = $versionParts -join ','
    $versionString = $versionParts -join '.'
    $resourceScript = Join-Path $OutputDirectory 'pinget-version.rc'
    $resourceFile = Join-Path $OutputDirectory 'pinget-version.res'

    @"
1 VERSIONINFO
 FILEVERSION $versionCsv
 PRODUCTVERSION $versionCsv
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "Devolutions Inc."
      VALUE "FileDescription", "Pinget CLI"
      VALUE "FileVersion", "$versionString"
      VALUE "InternalName", "pinget"
      VALUE "LegalCopyright", "Copyright 2021-2026 Devolutions Inc."
      VALUE "OriginalFilename", "pinget.exe"
      VALUE "ProductName", "Pinget"
      VALUE "ProductVersion", "$versionString"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x0409, 1200
  END
END
"@ | Set-Content -Path $resourceScript -Encoding ascii

    $rc = Resolve-WindowsResourceCompiler
    Invoke-NativeCommand -FilePath $rc -ArgumentList @('/nologo', "/fo$resourceFile", $resourceScript)

    $resourceFile
}

function Add-RustFlag {
    param(
        [string]$RustFlags,
        [Parameter(Mandatory)][string]$Flag
    )

    if ([string]::IsNullOrWhiteSpace($RustFlags)) {
        return $Flag
    }

    if ($RustFlags -match [regex]::Escape($Flag)) {
        return $RustFlags
    }

    "$RustFlags $Flag"
}

function Convert-PeRvaToOffset {
    param(
        [Parameter(Mandatory)][object[]]$Sections,
        [Parameter(Mandatory)][uint32]$Rva
    )

    foreach ($section in $Sections) {
        $size = [Math]::Max($section.VirtualSize, $section.RawDataSize)
        if (($Rva -ge $section.VirtualAddress) -and ($Rva -lt ($section.VirtualAddress + $size))) {
            return [int]($section.RawDataPointer + ($Rva - $section.VirtualAddress))
        }
    }

    $null
}

function Get-NullTerminatedAsciiString {
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][int]$Offset
    )

    $end = $Offset
    while (($end -lt $Bytes.Length) -and ($Bytes[$end] -ne 0)) {
        $end++
    }

    [System.Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

function Get-PeImportedDllNames {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $peOffset = [BitConverter]::ToUInt32($bytes, 0x3c)
    $coffOffset = [int]$peOffset + 4
    $sectionCount = [BitConverter]::ToUInt16($bytes, $coffOffset + 2)
    $optionalHeaderSize = [BitConverter]::ToUInt16($bytes, $coffOffset + 16)
    $optionalHeaderOffset = $coffOffset + 20
    $magic = [BitConverter]::ToUInt16($bytes, $optionalHeaderOffset)
    $dataDirectoryOffset = if ($magic -eq 0x20b) { $optionalHeaderOffset + 112 } elseif ($magic -eq 0x10b) { $optionalHeaderOffset + 96 } else { throw "Unsupported PE optional header magic: 0x$($magic.ToString('x'))" }
    $importDirectoryRva = [BitConverter]::ToUInt32($bytes, $dataDirectoryOffset + 8)

    if ($importDirectoryRva -eq 0) {
        return @()
    }

    $sectionTableOffset = $optionalHeaderOffset + $optionalHeaderSize
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $sectionOffset = $sectionTableOffset + ($index * 40)
        $sections += [pscustomobject]@{
            VirtualSize = [BitConverter]::ToUInt32($bytes, $sectionOffset + 8)
            VirtualAddress = [BitConverter]::ToUInt32($bytes, $sectionOffset + 12)
            RawDataSize = [BitConverter]::ToUInt32($bytes, $sectionOffset + 16)
            RawDataPointer = [BitConverter]::ToUInt32($bytes, $sectionOffset + 20)
        }
    }

    $importDirectoryOffset = Convert-PeRvaToOffset -Sections $sections -Rva $importDirectoryRva
    if ($null -eq $importDirectoryOffset) {
        throw "Unable to resolve import directory RVA for $Path."
    }

    $importedDlls = @()
    for ($descriptorOffset = $importDirectoryOffset; ; $descriptorOffset += 20) {
        $originalFirstThunk = [BitConverter]::ToUInt32($bytes, $descriptorOffset)
        $timeDateStamp = [BitConverter]::ToUInt32($bytes, $descriptorOffset + 4)
        $forwarderChain = [BitConverter]::ToUInt32($bytes, $descriptorOffset + 8)
        $nameRva = [BitConverter]::ToUInt32($bytes, $descriptorOffset + 12)
        $firstThunk = [BitConverter]::ToUInt32($bytes, $descriptorOffset + 16)

        if (($originalFirstThunk -eq 0) -and ($timeDateStamp -eq 0) -and ($forwarderChain -eq 0) -and ($nameRva -eq 0) -and ($firstThunk -eq 0)) {
            break
        }

        $nameOffset = Convert-PeRvaToOffset -Sections $sections -Rva $nameRva
        if ($null -eq $nameOffset) {
            throw "Unable to resolve import name RVA for $Path."
        }

        $importedDlls += Get-NullTerminatedAsciiString -Bytes $bytes -Offset $nameOffset
    }

    $importedDlls
}

function Assert-WindowsExecutableMetadata {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedOriginalFilename
    )

    if (-not $IsWindows) {
        return
    }

    $versionInfo = (Get-Item -Path $Path).VersionInfo
    $requiredFields = @{
        CompanyName = 'Devolutions Inc.'
        FileDescription = 'Pinget CLI'
        OriginalFilename = $ExpectedOriginalFilename
        ProductName = 'Pinget'
    }

    foreach ($field in $requiredFields.GetEnumerator()) {
        $actualValue = $versionInfo.PSObject.Properties[$field.Key].Value
        if ($actualValue -ne $field.Value) {
            throw "Unexpected $($field.Key) in $Path. Expected '$($field.Value)', got '$actualValue'."
        }
    }

    if ([string]::IsNullOrWhiteSpace($versionInfo.FileVersion) -or [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
        throw "Missing Windows file/product version information in $Path."
    }

    $dynamicMsvcRuntimeImports = @(Get-PeImportedDllNames -Path $Path | Where-Object {
            $_ -match '^(vcruntime|msvcp|msvcr|ucrtbase)' -or $_ -match '^api-ms-win-crt-'
        })

    if ($dynamicMsvcRuntimeImports.Count -gt 0) {
        throw "Expected static MSVC/UCRT runtime linkage for $Path, but found dynamic imports: $($dynamicMsvcRuntimeImports -join ', ')"
    }
}

function Get-SourceVersion {
    $rustCliManifest = Join-Path $repoRoot 'rust\crates\pinget-cli\Cargo.toml'
    $dotNetCliProgram = Join-Path $repoRoot 'dotnet\src\Devolutions.Pinget.Cli\Consts.cs'

    $rustMatch = Select-String -Path $rustCliManifest -Pattern '^version = "([^"]+)"$' | Select-Object -First 1
    $dotNetMatch = Select-String -Path $dotNetCliProgram -Pattern '^\s*internal const string Version = "([^"]+)";$' | Select-Object -First 1

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

function Assert-DotNetToolPackage {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -match '\.nuspec$' } | Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "Unable to find nuspec entry in $PackagePath."
        }

        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try {
            $nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        if ($nuspec -notmatch '<packageType name="DotnetTool"') {
            throw "Expected DotnetTool package type in $PackagePath."
        }

        if (-not ($entryNames | Where-Object { $_ -match '^tools/[^/]+/any/DotnetToolSettings\.xml$' })) {
            throw "Expected DotnetToolSettings.xml in the framework-dependent tool package: $PackagePath."
        }

        $ridSpecificEntries = @($entryNames | Where-Object { $_ -match '^runtimes/' -or $_ -match '^tools/[^/]+/(win|linux|osx)-' })
        if ($ridSpecificEntries.Count -gt 0) {
            throw "Framework-dependent tool package should not contain RID-specific native payload entries: $($ridSpecificEntries -join ', ')"
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
$buildTool = $Package -in @('All', 'Tool')

if ($Clean) {
    if ($buildRust) {
        Remove-Item -Path (Join-Path $StagingRoot 'rust') -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($buildDotNet) {
        Remove-Item -Path (Join-Path $StagingRoot 'dotnet-nativeaot') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $StagingRoot 'dotnet-nativeaot-obj') -Recurse -Force -ErrorAction SilentlyContinue
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
            $previousRustFlags = $env:RUSTFLAGS
            try {
                if ($target['CargoTarget'] -like '*-windows-msvc') {
                    $env:RUSTFLAGS = Add-RustFlag -RustFlags $env:RUSTFLAGS -Flag '-C target-feature=+crt-static'
                }

                Invoke-NativeCommand -FilePath cargo -ArgumentList @('build', '--release', '--package', 'pinget-cli', '--bin', 'pinget', '--manifest-path', $cargoManifest, '--target', $target['CargoTarget'])
            }
            finally {
                $env:RUSTFLAGS = $previousRustFlags
            }

            $builtBinary = Join-Path $repoRoot "rust\target\$($target['CargoTarget'])\release\$($target['SourceBinaryName'])"
            Assert-FileExists -Path $builtBinary
            Copy-Item -Path $builtBinary -Destination (Join-Path $stageDir $target['PackageBinaryName']) -Force
        }

        $stagedBinary = Join-Path $stageDir $target['PackageBinaryName']
        Assert-FileExists -Path $stagedBinary
        if ($target['PackageBinaryName'] -eq 'pinget.exe') {
            Assert-WindowsExecutableMetadata -Path $stagedBinary -ExpectedOriginalFilename 'pinget.exe'
        }
    }
}

if ($buildDotNet) {
    $dotNetProject = Join-Path $repoRoot 'dotnet\src\Devolutions.Pinget.Cli\Devolutions.Pinget.Cli.csproj'

    foreach ($rid in $DotNetRuntimeIdentifiers) {
        $binaryNames = Get-DotNetBinaryNames -RuntimeIdentifier $rid
        $stageDir = Join-Path $StagingRoot "dotnet-nativeaot\$rid"
        New-Item -Path $stageDir -ItemType Directory -Force | Out-Null

        if (-not $NoBuild) {
            $publishArguments = @(
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

            if ($rid -like 'win-*') {
                $resourceFile = New-PingetWindowsVersionResource -OutputDirectory (Join-Path $StagingRoot "dotnet-nativeaot-obj\$rid") -Version $Version
                $publishArguments += "/p:Win32Resource=$resourceFile"
            }

            Invoke-NativeCommand -FilePath dotnet -ArgumentList $publishArguments

            $sourceBinary = Join-Path $stageDir $binaryNames['SourceBinaryName']
            $packageBinary = Join-Path $stageDir $binaryNames['PackageBinaryName']
            Assert-FileExists -Path $sourceBinary
            if ($sourceBinary -ne $packageBinary) {
                Move-Item -Path $sourceBinary -Destination $packageBinary -Force
            }
        }

        $stagedBinary = Join-Path $stageDir $binaryNames['PackageBinaryName']
        Assert-FileExists -Path $stagedBinary
        if ($binaryNames['PackageBinaryName'] -eq 'pinget.exe') {
            Assert-WindowsExecutableMetadata -Path $stagedBinary -ExpectedOriginalFilename 'pinget.exe'
        }
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

    if ($buildTool) {
        $toolPackageProject = Join-Path $repoRoot 'dotnet\src\Devolutions.Pinget.Cli\Devolutions.Pinget.Cli.csproj'
        Invoke-NativeCommand -FilePath dotnet -ArgumentList @('pack', $toolPackageProject, '-c', $Configuration, '-o', $OutputRoot, "/p:Version=$Version", '/p:ContinuousIntegrationBuild=true')

        $toolPackage = Join-Path $OutputRoot "Devolutions.Pinget.Tool.$Version.nupkg"
        Assert-FileExists -Path $toolPackage
        Assert-DotNetToolPackage -PackagePath $toolPackage
    }
}
