# Pinget

This directory contains **Pinget**: portable, COM-free WinGet-compatible implementations intended to become a standalone repository later.

The current goal is to keep the subtree self-contained enough that it can be split out with minimal rewriting. Within this directory, treat paths as if `pinget\` were the repository root.

## Layout

| Path | Language | Output | Notes |
| --- | --- | --- | --- |
| `rust` | Rust | `pinget` CLI + `pinget-core` library | Cargo workspace with a reusable core crate and a CLI crate |
| `dotnet` | C# / .NET 10 | `pinget` CLI + `Devolutions.Pinget.Core` library + Pinget PowerShell module | Solution with a reusable core library, CLI app, tests, and a PowerShell engine/cmdlet layer |

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

The C# implementation also ships a **Pinget PowerShell module** that mirrors the upstream `Microsoft.WinGet.Client` cmdlet family with renamed `Pinget` nouns, backed entirely by `Devolutions.Pinget.Core` rather than COM / WinRT APIs.

## Non-goals

Pinget intentionally excludes:

- COM / WinRT / `Microsoft.Management.Deployment`
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
- `dotnet\src\Devolutions.Pinget.PowerShell.Cmdlets` - PowerShell cmdlet implementation
- `dotnet\tests` - Pinget PowerShell Pester coverage

## Extraction prep

This subtree is being prepared for later promotion into its own GitHub repository. Current prep rules:

1. Prefer subtree-relative paths in docs and scripts.
2. Keep Pinget-specific documentation under this directory instead of the parent repo when practical.
3. Avoid introducing dependencies on the native WinGet COM stack, DSC/configuration stack, or MCP.
4. Keep Rust and C# toolchain instructions runnable from this directory as a future repository root.

## Status

Pinget is best treated as an **experimental portable winget implementation** focused on:

- source-backed package discovery
- manifest retrieval and shaping
- custom REST source support
- reusable library surfaces in Rust and C#
- a COM-free PowerShell automation surface over the C# implementation

It is not intended to be a drop-in, fully complete replacement for the native Windows Package Manager client.
