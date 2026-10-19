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

### Requirements

The Windows version of Pinget not only works on the same Windows versions as [what Winget does](https://github.com/microsoft/winget-cli#installing-the-client), but since Pinget does not have a hard dependency on MSIX whereas Winget has, Pinget supports several Windows versions that Winget doesn't. However, as with Winget, Pinget has a hard dependency on the [IsWow64Process2](IsWow64Process2) function, and therefore cannot run on for instance Windows 8.1 or earlier, or on ReactOS.

* Windows 11
* Windows 10 ≥1709 x64/ARM64 (1709 and 1803 are Pinget-exclusive)
* Windows Server ≥2019
* Windows Server Core (Pinget-exclusive)
* Wine on Linux (Pinget-exclusive)

The native Linux version of Pinget lacks support for installing apps or for listing installed apps.

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

### .NET tool package

The **`Devolutions.Pinget.Tool`** package installs the C# Pinget CLI as a framework-dependent .NET tool:

```powershell
dotnet tool install -g Devolutions.Pinget.Tool
pinget --help
```

For local tool manifests:

```powershell
dotnet new tool-manifest
dotnet tool install Devolutions.Pinget.Tool
dotnet tool run pinget -- --help
```

This package is install-facing and requires a compatible .NET runtime on the target machine. It is separate from the build-output packages below.

### CLI executable packages

Pinget also publishes build-output packages for .NET projects that need a prebuilt `pinget` executable alongside their own application:

| Package | Executable implementation | Notes |
| --- | --- | --- |
| `Devolutions.Pinget.Cli.Rust` | Rust CLI | Cross-platform native executable assets named `pinget[.exe]` under `runtimes\<rid>\native` |
| `Devolutions.Pinget.Cli.DotNet` | C# CLI | Trimmed NativeAOT executable assets named `pinget.exe`, plus required native sidecars, starting with Windows x64 and arm64 |

These packages are intended for `PackageReference` consumption and copy the executable assets into the consuming project's output/publish output through MSBuild targets. They are not `dotnet tool` packages; use `Devolutions.Pinget.Tool` when you want to install the CLI with `dotnet tool install`.

```powershell
dotnet add package Devolutions.Pinget.Cli.Rust
dotnet add package Devolutions.Pinget.Cli.DotNet
```

Both packages use `pinget[.exe]` inside the NuGet package. If a project intentionally references both implementations, set package-specific output names to add distinguishable copies in the build output:

```xml
<PropertyGroup>
  <DevolutionsPingetRustCliWindowsOutputName>pinget.exe</DevolutionsPingetRustCliWindowsOutputName>
  <DevolutionsPingetDotNetCliWindowsOutputName>pinget-managed.exe</DevolutionsPingetDotNetCliWindowsOutputName>
</PropertyGroup>
```

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

## Storage location and source mode

Three environment variables control where Pinget keeps its state, which sources it resolves
against, and which WinGet it delegates source commands to. All three are read by the Rust CLI and
the C# core.

| Variable | Effect |
| --- | --- |
| `PINGET_APPROOT` | Overrides the per-user storage root (`%LOCALAPPDATA%\Devolutions\Pinget` by default). Useful for embedding Pinget in a portable or sandboxed host. |
| `PINGET_SOURCE_MODE` | Selects the source mode explicitly: `auto`, `private`, or `system-winget-mirror`. |
| `PINGET_WINGET_PATH` | Full path to the `winget.exe` that Pinget runs for source commands. Accepts the executable or the directory holding it. |

Source modes:

- `auto` mirrors the machine's configured WinGet sources, falling back to the private store when
  they cannot be read.
- `private` uses only the source list stored under the app root, seeded with the stock `winget`
  and `msstore` entries.
- `system-winget-mirror` mirrors the machine's WinGet sources with no fallback.

Without `PINGET_SOURCE_MODE`, setting `PINGET_APPROOT` also selects `private`, on the assumption
that a caller supplying its own root wants an isolated instance. A host that relocates storage but
still wants the machine's real source list must set `PINGET_SOURCE_MODE=auto` as well; otherwise
sources the user added to WinGet are silently absent.

Precedence is the same in both implementations: an explicitly chosen mode wins, then
`PINGET_SOURCE_MODE`, then the app-root inference. In the C# API `RepositoryOptions.SourceMode` is
nullable for exactly this reason — leave it unset to let the environment decide, and note that
setting it to `SourceMode.Auto` is an explicit choice that the environment will not override.

### Locating the system WinGet

WinGet source management belongs to WinGet, so the mirror and `system-winget-mirror` modes shell
out to `winget source ...`. WinGet ships as an App Execution Alias, which means a host that
inherited a PATH without `%LOCALAPPDATA%\Microsoft\WindowsApps` cannot spawn it by name at all.
Pinget therefore looks for it in this order:

1. `PINGET_WINGET_PATH`, when set - a host that already knows the location should say so, and a
   value that holds no `winget.exe` is reported rather than silently ignored
2. the directory of the running executable, which is where Windows looks first when a bare
   program name is spawned, so a host that ships its own copy keeps winning
3. the PATH
4. `%LOCALAPPDATA%\Microsoft\WindowsApps`, the execution-alias directory
5. the newest registered `Microsoft.DesktopAppInstaller` package root

Only the last two are new lookups. The rest is the order a bare `winget` already resolved in, so
a machine that could run WinGet before resolves the very same executable.

A query never fails because the source list could not be re-mirrored: `list` and `source list`
keep the mirror Pinget cached earlier and, for `list`, report a warning alongside the results.
Commands that change sources still fail loudly, since they have to see the real source list. The
mirror is re-exported at most every 15 minutes, so a query no longer spawns WinGet every time.

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

## Pinget AgentSkill manifests

Pinget can install an agent skill from a **Pinget-only** manifest with WinGet-style
package identity fields. This does not change the WinGet manifest schema or turn
skills into Windows installers: `winget.exe` cannot consume this manifest type.
The original HTTPS ZIP form remains supported: Pinget verifies its SHA-256
digest and `SKILL.md`, then installs the selected skill into one agent directory.

```yaml
ManifestType: pinget.agentSkill
PingetManifestVersion: 1
PackageIdentifier: Example.AgentSkills.Demo
PackageVersion: 1.0.0
PackageName: demo
AgentSkill:
  ArtifactUrl: https://example.test/demo-1.0.0.zip
  ArtifactSha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
  SkillPath: skills/demo
```

`SkillPath` is optional when `SKILL.md` is at the archive root. `PackageName`
must be a lowercase skill slug matching the `name` in `SKILL.md`. `Publisher` is
optional. For other distribution methods, replace `ArtifactUrl` and
`ArtifactSha256` with a typed `Source` mapping:

```yaml
AgentSkill:
  Source:
    Type: git
    Url: https://github.com/example/agent-skills.git
    Commit: 0123456789abcdef0123456789abcdef01234567
  SkillPath: skills/demo
```

| `Source.Type` | Required source fields | What Pinget fetches |
| --- | --- | --- |
| `archive` | `Url`, `Sha256`, `Format: zip\|tar.gz\|skill-md` | An HTTPS archive or direct `SKILL.md`; SHA-256 is mandatory. |
| `git` | `Url`, `Commit` | A specific 40-character commit from an HTTPS Git repository; only the selected tree is archived. Requires `git` on `PATH`. |
| `notion-skill` | `Id`, `VersionId` | A Notion skill page's versioned directory from `GET /v1/ai/skills/{id}`. |
| `notion-plugin` | `Id`, `VersionId` | A Notion plugin pack's versioned directory from `GET /v1/ai/plugins/{id}`; requires `PluginPath`. |
| `well-known` | `IndexUrl`, `Sha256` | The named artifact in an Agent Skills discovery v0.2.0 JSON index; its digest must match the manifest. |

For Notion sources, use a canonical page or plugin UUID as `Id`, its 64-character
`version_id` as `VersionId`, and set `NOTION_API_KEY` at runtime to a read-only
Notion connection token. The workspace must have the Notion Agent Skills API
enabled and grant the connection Read content. Pinget uses Notion's
[skill-directory](https://developers.notion.com/reference/agent-skills/get-skill-directory)
or [plugin-directory](https://developers.notion.com/reference/agent-skills/get-plugin-directory)
endpoint to request a fresh short-lived signed archive URL for each install;
neither that URL nor the token belongs in the manifest or installed provenance.
Notion directory downloads are `.tar.gz`; single top-level wrapper directories
are normalized.

To select a skill from an [Agent Plugins v1.0.0](https://agent-plugins.org/specification)
directory, add `PluginPath: .` (or a relative plugin directory) and
`SkillPath: skills/<PackageName>` to any applicable source. Pinget checks that
directory's `plugin.json` schema identifier and name, then installs **only the
selected Agent Skill**. Agent Plugins does not define a registry, download
format, or installer; Pinget's source and integrity fields supply those. Pinget
does not install or activate other plugin components.

`--agent` is required; currently supported
values are `github-copilot`, `codex`, and `claude-code`. The default is a
project install in the current working directory; `--scope user` installs to
the agent's user directory. Both implementations record package identity,
source digest, agent, installed file-tree digest, and destination in
`<project-or-home>/.pinget/agent-skills/<agent>/<skill-name>.json`; this
provenance is separate from the standalone skills-manager lock files.

```powershell
pinget install --manifest .\demo.yaml --agent github-copilot
pinget install --manifest .\demo.yaml --agent claude-code --scope user
```

### AgentSkill catalogs and updates

A Pinget-owned `Pinget.AgentSkills` source indexes *manifest versions*, not
downloads. One catalog can point to manifests for Git-backed skills, direct
archives, Notion directories, and Agent Plugins. Publish a UTF-8 JSON index
over HTTPS with this format (substitute real hashes and URLs):

```json
{
  "schemaVersion": 1,
  "packages": [
    {
      "packageIdentifier": "Example.AgentSkills.Demo",
      "packageVersion": "1.0.0",
      "packageName": "demo",
      "manifestUrl": "manifests/demo-1.0.0.yaml",
      "manifestSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ]
}
```

`manifestUrl` resolves relative to the index URL (or is an absolute HTTPS
URL). Its SHA-256 pins the Pinget manifest; that manifest separately pins the
skill source. Catalog and manifest URLs cannot contain query strings or
embedded credentials; keep API keys in runtime configuration instead of
registry entries. Catalogs are limited to 1 MiB and 1,000 versions. Pinget caches
the index until `source update`, so publishing a new version requires a source
refresh before it becomes visible locally. A refresh rejects changes to an
existing package version's name, manifest URL, or digest; publish a new version
instead of repointing an existing one.

```powershell
pinget source add --name skills --arg https://example.org/skills/index.json --type Pinget.AgentSkills
pinget source update skills
pinget search demo --agent github-copilot --source skills
pinget install --id Example.AgentSkills.Demo --agent github-copilot --source skills
pinget upgrade --id Example.AgentSkills.Demo --agent github-copilot --source skills
```

For updates, publish a new `PackageVersion` with its new manifest hash and
immutable source pin, then refresh the catalog. Installing or upgrading an
existing *managed* skill installs a newer version only: the same version is a
no-op, a downgrade is rejected, and an unmanaged destination or locally
modified skill is not overwritten. Pinget validates the replacement in a
staging directory and restores the previous skill and provenance if publication
fails. A skill installed from one catalog cannot be silently replaced from a
different catalog. Older installs without an installed file-tree digest need a manual
remove/reinstall before managed updates.

The existing `Microsoft.Rest` and `Microsoft.PreIndexed.Package` sources remain
application-only. `Pinget.AgentSkills` sources are Pinget-owned, not registered
with `winget.exe`; official WinGet clients cannot read these manifests or
catalogs. This is not skills.sh search or automatic Git/Notion workspace
discovery: manifests and version pins must be published to the catalog.
Installed skill inventory, bulk skill upgrades, and plugin runtime components
remain outside this slice. No `npx` or MCP integration is required.

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
