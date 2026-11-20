# Homebrew formula bottle manifest mapping

This is the version 1 Pinget extension contract for curated Homebrew **formula
bottles** on macOS and Linux. It does not describe casks and is not a
Microsoft WinGet-compatible manifest: `Pinget.Homebrew` and
`InstallerType: homebrew-bottle` are Pinget extensions.

The Rust and .NET YAML readers preserve the extension in parsed manifest
objects and reject unsupported extension versions, malformed bottle hashes,
unknown target OS/architecture/tags/relocation values, target-constraint
contradictions, and bottle entries that do not match an installer by URL and
SHA-256. A dedicated verified-source update path is implemented, but native
bottle installation is not.

## Concept mapping

| Homebrew concept | Pinget/WinGet field | Meaning |
| --- | --- | --- |
| Formula display name | `PackageName` | Human-readable catalog name. |
| Formula identity | `PackageIdentifier` and `Pinget.Homebrew.Formula` | The shared Rust/.NET ID helper uses `Pinget.Homebrew.<formula-slug>-<16-hex-digest>`; the readable segment is at most 32 characters and the digest covers the case-normalized tap/formula identity so sanitizing separators cannot collapse distinct formulas or taps. Preserve the exact formula spelling (including `@` version suffixes) in the extension. |
| Tap qualification | `Pinget.Homebrew.Tap` | Exact owner/repository tap, such as `homebrew/core`; do not infer an upstream publisher from it. |
| Upstream author | `Publisher` | Homebrew formula JSON does not establish a package author. The offline importer sets the required publisher field to the literal `Unknown`, never to the tap. |
| Stable formula version | `PackageVersion` | Homebrew `pkg_version`: upstream version plus `_revision` when revision is nonzero. |
| Upstream formula version | `Pinget.Homebrew.UpstreamVersion` | The formula version without the formula revision. |
| Formula revision | `Pinget.Homebrew.Revision` | Homebrew formula revision; independent of bottle rebuild number. |
| Version scheme | `Pinget.Homebrew.VersionScheme` | Homebrew version scheme integer, retained explicitly. |
| Bottle archive | `InstallerUrl`, `InstallerSha256`, and matching `Pinget.Homebrew.Bottles` entry | URL and SHA-256 identify the unchanged bottle archive, never the formula source tarball. Each bottle target matches exactly one installer on URL and hash. |
| Bottle rebuild | `Pinget.Homebrew.Bottles[].Rebuild` | Rebuild number is distinct from formula revision and `PackageVersion`; future receipts/upgrades must include it. |
| Bottle target | `InstallerType`, `Platform`, `Architecture`, `TargetOS`, `Architecture`, `BottleTag` | `Platform` and installer architecture must agree with the explicit Homebrew target. The original bottle tag is retained without guessing an unknown mapping. |
| Formula aliases | `Moniker` and `Pinget.Homebrew.Aliases` | A selected primary alias may be exposed as `Moniker`; retain all upstream aliases in the extension. Alias collisions require an explicit policy. |
| Renamed formula names | `Pinget.Homebrew.OldNames` | Preserve Homebrew old names; do not silently make them Pinget IDs or aliases. |
| Runtime formula dependencies | `Dependencies.PackageDependencies` | Refer to exact Pinget package identifiers and versions. Build/test dependencies are not runtime dependencies of an installed bottle. |
| Keg-only formula | `Pinget.Homebrew.KegOnly` | Do not create default command links for a keg-only formula. |
| Relocation classification | `Pinget.Homebrew.Bottles[].Relocation` | Retains `any`, `any_skip_relocation`, or `fixed`. `any` and `any_skip_relocation` are not interchangeable; fixed-prefix bottles must be rejected unless their actual relocation is supported. |
| macOS product-version requirements | `MinimumOSVersion`, `MaximumOSVersion` | Product version, not Darwin kernel version. These fields are valid only for macOS targets. |
| Linux ABI requirements | `MinimumGlibc`, `Loader` | Retain glibc and loader constraints; these fields are valid only for Linux targets. Unknown requirements are not guessed. |
| SPDX license | `License` | Preserve the upstream SPDX expression when present. |
| Homepage | `PackageUrl` | Formula homepage. |
| Description | `ShortDescription` / `Description` | Formula summary/description. |
| Formula source code | Not an installer field | Source URL/hash must not be substituted for a bottle URL/hash. Pinget does not build from source. |
| Cask metadata | Unsupported | Casks are outside this prototype. |

`PackageVersion` is intentionally independent from bottle rebuild. For example,
upstream version `1.8.1`, revision `1`, and bottle rebuild `3` map to
`PackageVersion: 1.8.1_1` and `Rebuild: 3`.

## Example

This illustrative dependency-chain example shows the manifest contract; the
bounded offline importer below does not yet resolve dependencies.

```yaml
PackageIdentifier: Pinget.Homebrew.jq-929958fef124d633
PackageVersion: 1.8.1_1
PackageName: jq
Publisher: Unknown
PackageUrl: https://jqlang.org/
ShortDescription: Lightweight and flexible command-line JSON processor
License: MIT
PackageLocale: en-US
ManifestType: singleton
ManifestVersion: 1.10.0
Dependencies:
  PackageDependencies:
    - PackageIdentifier: Pinget.Homebrew.oniguruma-e8324275203d772c
Installers:
  - Architecture: arm64
    Platform: [macOS]
    InstallerType: homebrew-bottle
    InstallerUrl: https://packages.example.invalid/bottles/0123456789abcdef.tar.gz
    InstallerSha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
Pinget:
  Homebrew:
    SchemaVersion: 1
    Formula: jq
    Tap: homebrew/core
    UpstreamVersion: 1.8.1
    Revision: 1
    VersionScheme: 0
    Aliases: [jqr]
    OldNames: []
    KegOnly: false
    Bottles:
      - Url: https://packages.example.invalid/bottles/0123456789abcdef.tar.gz
        Sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        TargetOS: macOS
        Architecture: arm64
        BottleTag: arm64_sonoma
        Rebuild: 3
        Relocation: any
        MinimumOSVersion: "14.0"
```

The bottle archive URL and SHA-256 appear in both the ordinary installer
contract and the Pinget target metadata so each target can be correlated with
its bottle tag, rebuild, relocation, and ABI constraints. Readers reject a
manifest if those records disagree.

The v1 readers currently recognize macOS x64 tags `tahoe`, `sequoia`, `sonoma`,
`ventura`, `monterey`, `big_sur`, `catalina`, `mojave`, `high_sierra`,
`sierra`, `el_capitan`, and `yosemite`; the corresponding arm64 tags use the
`arm64_` prefix. Linux tags are explicitly limited to `x86_64_linux`,
and `aarch64_linux`. The declared OS and architecture must match the mapping.
Other tags, including platform-independent tags, are rejected until separately
validated; this list is not a claim that those historical targets are
installable by the current client.

## Current implementation boundary

The model and validation contract is implemented in both core libraries. A
bounded offline importer can generate and mirror one dependency-free
`arm64_sonoma` manifest from pinned local inputs; it is not the complete
Homebrew API/dependency-closure importer. The publisher records a Homebrew
profile for Homebrew-only manifest snapshots and packages them as `brew.msix`;
mixed Homebrew/Windows snapshots are rejected. Rust and .NET now support a
separate `Pinget.PreIndexed.Homebrew` source registration, which requests
`brew.msix` only and validates its expected identity, publisher, signature
trust, and v2 index schema before activating cache data. The client requires
`psign-tool` and explicit trust configuration; successful cryptographic
acceptance has not yet been demonstrated in the current environment.
Homebrew-aware version ordering and install/list/upgrade/uninstall behavior
remain unimplemented and are not implied by successful parsing, publishing,
or source update.
