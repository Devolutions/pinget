# Pinget

This directory contains **Pinget**: portable implementations intended to remain compatible with WinGet behavior where practical.

The current goal is to keep the Rust, C#, and PowerShell surfaces aligned while preserving WinGet-compatible behavior. Within this directory, treat paths as if `pinget\` were the repository root.

## Layout

| Path | Language | Output | Notes |
| --- | --- | --- | --- |
| `rust` | Rust | `pinget` CLI + `pinget-core` library | Cargo workspace with a reusable core crate and a CLI crate |
| `dotnet` | C# / .NET 10 | `pinget` CLI + `Devolutions.Pinget.Core` library + `Devolutions.Pinget.Client` PowerShell module | Solution with a reusable core library, CLI app, tests, and a PowerShell engine/cmdlet layer |

## Current scope

Both implementations currently cover a substantial WinGet-like subset:

- `search`, `show`, `list`, `upgrade`
- `source list`, `source update`, `source export`, `source add`, `source edit`, `source remove`, `source reset`
- `cache warm`
- `download`, `hash`, `validate`, `export`, `error`, `settings export`, `settings set`, `settings reset`, `features`
- `pin list`, `pin add`, `pin remove`, `pin reset`
- `install`, `uninstall`, `repair`, `import`

Structured manifest output is also supported:

- `show --output json|yaml`
- `search --manifests --output json|yaml`

The C# implementation also ships the **`Devolutions.Pinget.Client` PowerShell 7 module** that mirrors the upstream `Microsoft.WinGet.Client` cmdlet family with renamed `Pinget` nouns, backed by `Devolutions.Pinget.Core`.

## Pinget CLI

The prebuilt `pinget` CLI published in [GitHub Releases](https://github.com/Devolutions/pinget/releases) is the Rust build. Download the archive for your platform, extract it, and put the `pinget` executable on your `PATH`.

```powershell
pinget --help
pinget --info
pinget source list
pinget source update
```

Examples below use Devolutions packages from the community WinGet manifests:

```powershell
# Discover packages.
pinget search Devolutions
pinget search --id Devolutions.RemoteDesktopManager --exact
pinget search --id Devolutions.MsRdpEx --exact --versions

# Inspect package metadata.
pinget show --id Devolutions.RemoteDesktopManager --exact
pinget show --id Devolutions.MsRdpEx --exact --versions

# Produce structured manifest output for automation.
pinget show --id Devolutions.RemoteDesktopManager --exact --output json
pinget search Devolutions --manifests --output yaml

# Download installers without installing them.
pinget download --id Devolutions.RemoteDesktopManager --download-directory .\downloads

# Query installed packages and available upgrades.
pinget list Devolutions
pinget upgrade

# Package action commands execute installers on supported platforms.
pinget install --id Devolutions.MsRdpEx --scope user --silent
pinget uninstall --id Devolutions.MsRdpEx --silent
```

## PowerShell module

The **`Devolutions.Pinget.Client`** module is available from the PowerShell Gallery:

```powershell
# Recommended for current PowerShellGet/PSResourceGet environments.
Install-PSResource -Name Devolutions.Pinget.Client -Repository PSGallery

# PowerShellGet v2 alternative.
Install-Module -Name Devolutions.Pinget.Client -Scope CurrentUser
```

The module requires PowerShell 7.4 or later and exposes WinGet-style cmdlets with `Pinget` nouns instead of `WinGet` nouns. It is implemented over `Devolutions.Pinget.Core`.

```powershell
Import-Module Devolutions.Pinget.Client

Get-PingetVersion
Get-Command -Module Devolutions.Pinget.Client
Get-PingetSource
```

Examples below use Devolutions packages from the community WinGet manifests:

```powershell
# Discover Devolutions packages.
Find-PingetPackage -Query Devolutions
Find-PingetPackage -Id Devolutions.RemoteDesktopManager
Find-PingetPackage -Id Devolutions.MsRdpEx

# Inspect package metadata and available versions.
$rdm = Find-PingetPackage -Id Devolutions.RemoteDesktopManager | Select-Object -First 1
$rdm.AvailableVersions | Select-Object -First 5
$rdm.GetPackageVersionInfo($rdm.AvailableVersions[0]) |
    Format-List Id, DisplayName, Version, Publisher, InstallerType, InstallerUrl

# Query installed packages.
Get-PingetPackage -Name Devolutions
Get-PingetPackage -Id Devolutions.RemoteDesktopManager

# Preview package operations. Remove -WhatIf to execute them.
Install-PingetPackage -Id Devolutions.MsRdpEx -Scope User -WhatIf
Update-PingetPackage -Id Devolutions.RemoteDesktopManager -WhatIf
Uninstall-PingetPackage -Id Devolutions.MsRdpEx -WhatIf

# Download installers without installing them.
Export-PingetPackage -Id Devolutions.RemoteDesktopManager -DownloadDirectory .\downloads
```

Useful exported cmdlets include `Find-PingetPackage`, `Get-PingetPackage`, `Install-PingetPackage`, `Update-PingetPackage`, `Uninstall-PingetPackage`, `Repair-PingetPackage`, `Export-PingetPackage`, `Get-PingetSource`, `Add-PingetSource`, `Remove-PingetSource`, `Reset-PingetSource`, and the user-setting cmdlets.

## NuGet package

The **`Devolutions.Pinget.Core`** library is available on [nuget.org](https://www.nuget.org/packages/Devolutions.Pinget.Core) for .NET applications that want to use Pinget package discovery, source caches, manifest metadata, downloads, and installed package state programmatically.

```powershell
dotnet add package Devolutions.Pinget.Core
```

The package targets .NET 8 and .NET 10. `Repository.Open()` is the main entry point; pass `RepositoryOptions` when you want source diagnostics, an isolated app root, or a custom user agent.

`RepositoryOptions.AppRoot = null` is the system WinGet mode on Windows. It uses the real Desktop App Installer / WinGet source state and is intended for embedded apps that need to align with `winget.exe source list` and `winget source export`. Use a custom app root when you want isolated sources and caches for tests, probes, or app-private package catalogs.

```csharp
using Devolutions.Pinget.Core;

var diagnostics = new List<RepositoryWarning>();
using var repository = Repository.Open(new RepositoryOptions
{
    AppRoot = null,
    UserAgent = "my-app/1.0",
    Diagnostics = diagnostics.Add,
});

foreach (var update in repository.UpdateSources())
{
    Console.WriteLine($"{update.Name}: {update.Detail}");
}

var search = repository.Search(new PackageQuery
{
    Query = "Devolutions",
    Count = 10,
});

foreach (var package in search.Matches)
{
    Console.WriteLine($"{package.Id} {package.Version}");
}

var rdm = repository.Show(new PackageQuery
{
    Id = "Devolutions.RemoteDesktopManager",
    Exact = true,
});

Console.WriteLine($"{rdm.Manifest.Name} {rdm.Manifest.Version}");

var serializable = rdm.ToSerializableManifest();
var json = System.Text.Json.JsonSerializer.Serialize(
    serializable,
    PingetJsonContext.Default.SerializableShowManifest);

var (_, installerPath) = repository.DownloadInstaller(
    new PackageQuery
    {
        Id = "Devolutions.MsRdpEx",
        Exact = true,
    },
    Path.Combine(Environment.CurrentDirectory, "downloads"));

Console.WriteLine(installerPath);
```

For embedded `show` and exact package resolution, Core exposes structured diagnostics in result `SourceWarnings`, in `SourceSearchException.Warning`, and through `RepositoryOptions.Diagnostics`. Single-source search failures surface the source name, source kind, source URL, cache path, HTTP status when available, and the original exception message instead of looking like a clean no-match. Multiple-source ambiguity is exposed as `MultiplePackageMatchesException.Matches`, so callers do not need to parse source names out of exception text.

`Repository.ShowManifest(query)` and `ShowResult.ToSerializableManifest()` return the same serializable manifest model used by the C# CLI `show --output json|yaml` path, including all installers and the Core-selected installer. Use `PingetJsonContext` for reflection-free `System.Text.Json` serialization in hosts that set `JsonSerializer.IsReflectionEnabledByDefault` to `false`.

## Non-goals

Pinget intentionally excludes:

- DSC-backed configuration flows (`configure`, `dscv3`, `Microsoft.WinGet.Configuration`)
- `mcp`

When upstream behavior fundamentally depends on those components, Pinget prefers explicit limits over fake-success shims.

## Portability model

Pinget is designed to keep **source-backed functionality** working cross-platform where practical.

- Commands like `search`, `show`, `cache warm`, `download`, `source`, and manifest shaping are intended to work on Windows and Linux.
- On Linux, **installed-state and package-action behavior is best-effort**:
  - `list` and upgrade inventory return empty results with an unsupported warning
  - `install`, `uninstall`, executed `upgrade`, and non-dry-run `import` return explicit no-op results with unsupported warnings

## Custom REST sources

Both implementations support custom REST sources, including third-party services such as `winget.pro`.

```powershell
pinget source add winget.pro https://api.example.test/feed --type Microsoft.Rest
pinget source add -n winget.pro -a https://api.example.test/feed -t Microsoft.Rest --trust-level trusted
```

Notes:

- `Microsoft.Rest` maps to the existing REST source kind in both implementations.
- `Microsoft.PreIndexed.Package` maps to the preindexed source kind.
- `--trust-level`, `--explicit`, and source priority metadata are persisted as source metadata and influence source selection behavior.

## Build and test

From the parent `winget-cli` repository root:

```powershell
Set-Location .\pinget
```

### Rust

```powershell
cargo +nightly fmt --manifest-path rust\Cargo.toml --all
cargo clippy -q --manifest-path rust\Cargo.toml --workspace --tests -- -D warnings
cargo test -p pinget-core --manifest-path rust\Cargo.toml
cargo test -p pinget-cli --manifest-path rust\Cargo.toml
cargo build -p pinget-cli --manifest-path rust\Cargo.toml
```

Run:

```powershell
cargo run -p pinget-cli --manifest-path rust\Cargo.toml -- search WinMerge
```

### C#

```powershell
dotnet format dotnet\Devolutions.Pinget.slnx
dotnet build dotnet\Devolutions.Pinget.slnx -c Release
dotnet test dotnet\src\Devolutions.Pinget.Core.Tests\Devolutions.Pinget.Core.Tests.csproj -c Release
pwsh -NoLogo -NoProfile -File (Resolve-Path 'dotnet\tests\RunTests.ps1')
pwsh -NoLogo -NoProfile -File (Resolve-Path 'scripts\Build-PowerShellModule.ps1') -NoBuild -OutputRoot dist\powershell-module -Clean
```

Run:

```powershell
dotnet run --project dotnet\src\Devolutions.Pinget.Cli\Devolutions.Pinget.Cli.csproj -- search WinMerge
```

## Repository structure

### Rust

- `rust\crates\pinget-core` - `pinget-core` library source
- `rust\crates\pinget-cli` - `pinget` CLI wrapper
- `rust\tools` - parity and comparison helpers

### C#

- `dotnet\src\Devolutions.Pinget.Core` - `Devolutions.Pinget.Core` library
- `dotnet\src\Devolutions.Pinget.Cli` - `Devolutions.Pinget.Cli` wrapper
- `dotnet\src\Devolutions.Pinget.Core.Tests` - unit tests
- `dotnet\src\Devolutions.Pinget.PowerShell.Engine` - PowerShell engine over the C# core
- `dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets` - PowerShell cmdlet implementation for the `Devolutions.Pinget.Client` module
- `dotnet\tests` - Pinget PowerShell Pester coverage

## Compatibility guidance

Pinget is maintained with WinGet compatibility in mind. Current prep rules:

1. Prefer subtree-relative paths in docs and scripts.
2. Keep Pinget-specific documentation under this directory instead of the parent repo when practical.
3. Avoid introducing dependencies on the native WinGet DSC/configuration stack or MCP.
4. Keep Rust and C# toolchain instructions runnable from this directory as a future repository root.

## Status

Pinget is best treated as an **experimental portable winget implementation** focused on:

- source-backed package discovery
- manifest retrieval and shaping
- custom REST source support
- reusable library surfaces in Rust and C#
- a PowerShell automation surface over the C# implementation

It is not intended to be a drop-in, fully complete replacement for the native Windows Package Manager client.
