# Homebrew formula bottle manifest mapping

This is the version 1 Pinget extension contract for curated Homebrew **formula
bottles** on macOS and Linux. It does not describe casks and is not a
Microsoft WinGet-compatible manifest: `Pinget.Homebrew` and
`InstallerType: homebrew-bottle` are Pinget extensions.

The Rust and .NET YAML readers preserve the extension in parsed manifest
objects and reject unsupported extension versions, malformed bottle hashes,
unknown target OS/architecture/tags/relocation values, target-constraint
contradictions, and bottle entries that do not match an installer by URL and
SHA-256. Both clients consume a signed static source and install bottles into a
private prefix without Homebrew, Ruby, source builds, or formula hooks.

## Concept mapping

| Homebrew concept | Pinget/WinGet field | Meaning |
| --- | --- | --- |
| Formula display name | `PackageName` | Human-readable catalog name. |
| Formula identity | `PackageIdentifier` and `Pinget.Homebrew.Formula` | The shared Rust/.NET ID helper uses `Pinget.Homebrew.<formula-slug>-<16-hex-digest>`; the readable segment is at most 32 characters and the digest covers the case-normalized tap/formula identity so sanitizing separators cannot collapse distinct formulas or taps. Preserve the exact formula spelling (including `@` version suffixes) in the extension. |
| Tap qualification | `Pinget.Homebrew.Tap` | Exact owner/repository tap, such as `homebrew/core`; do not infer an upstream publisher from it. |
| Upstream author | `Publisher` | Homebrew formula JSON does not establish a package author. The offline importer sets the required publisher field to the literal `Unknown`, never to the tap. |
| Stable formula version | `PackageVersion` | `<pkg_version>.bottle.<rebuild>`, where `pkg_version` includes `_revision` when nonzero. This makes rebuild-only updates visible to ordinary Pinget upgrade ordering. |
| Upstream formula version | `Pinget.Homebrew.UpstreamVersion` | The formula version without the formula revision. |
| Formula revision | `Pinget.Homebrew.Revision` | Homebrew formula revision; independent of bottle rebuild number. |
| Version scheme | `Pinget.Homebrew.VersionScheme` | Retained explicitly; native imports currently require zero rather than pretending to support epoch resets. |
| Bottle archive | `InstallerUrl`, `InstallerSha256`, and matching `Pinget.Homebrew.Bottles` entry | URL and SHA-256 identify the unchanged bottle archive, never the formula source tarball. Each bottle target matches exactly one installer on URL and hash. |
| Bottle rebuild | `Pinget.Homebrew.Bottles[].Rebuild` | Independent from formula revision, also included in the catalog version and managed receipt. The archive's keg directory still uses only `pkg_version`. |
| Bottle target | `InstallerType`, `Platform`, `Architecture`, `TargetOS`, `Architecture`, `BottleTag` | `Platform` and installer architecture must agree with the explicit Homebrew target. The original bottle tag is retained without guessing an unknown mapping. |
| Formula aliases | `Pinget.Homebrew.Aliases` | Preserved as metadata; not automatically exposed as monikers or command links. |
| Renamed formula names | `Pinget.Homebrew.OldNames` | Preserve Homebrew old names; do not silently make them Pinget IDs or aliases. |
| Runtime formula dependencies | `Installers[].Dependencies.PackageDependencies` | Exact derived package IDs; curated OCI receipt minima use `<pkg_version>.bottle.<bottle_rebuild>`. Dependencies install first and prevent ordinary dependency removal while dependents remain. Build/test dependencies are not installed. |
| Keg-only formula | `Pinget.Homebrew.KegOnly` | Do not create default command links for a keg-only formula. |
| Relocation classification | `Pinget.Homebrew.Bottles[].Relocation` | Retains `any`, `any_skip_relocation`, or `fixed`. `any` and `any_skip_relocation` are not interchangeable; fixed-prefix bottles must be rejected unless their actual relocation is supported. |
| macOS product-version requirements | `MinimumOSVersion`, `MaximumOSVersion` | Product version, not Darwin kernel version. These fields are valid only for macOS targets. |
| Linux ABI requirements | `MinimumGlibc`, `Loader` | Retain glibc and loader constraints; these fields are valid only for Linux targets. Unknown requirements are not guessed. |
| SPDX license | `License` | Preserve the upstream SPDX expression when present. |
| Homepage | `PackageUrl` | Formula homepage. |
| Description | `ShortDescription` / `Description` | Formula summary/description. |
| Formula source code | Not an installer field | Source URL/hash must not be substituted for a bottle URL/hash. Pinget does not build from source. |
| Keg / Cellar | `<AppRoot>/homebrew/Cellar/<formula>/<pkg_version>` | Private package files, never the system Homebrew prefix. |
| `opt` links | `<AppRoot>/homebrew/opt/<formula>` | Managed link to the current keg, including keg-only formulas. |
| Linked commands | `<AppRoot>/homebrew/bin` and `sbin` | Only bottle-provided commands are linked; existing unowned paths are not overwritten. |
| Homebrew installation receipt / OCI tab | Pinget managed JSON receipt | Package/source identity, catalog and keg versions, dependencies, and owned links. OCI `sh.brew.tab` is input metadata; pinned bottles need not contain `INSTALL_RECEIPT.json`. |
| Services, post-install hooks, requirements, conflicts | Unsupported | Never execute Ruby or synthesize those integrations. Nonempty unsupported metadata is rejected by the importer. |
| Cask metadata | Unsupported | Casks are outside this prototype. |

Formula revision and bottle rebuild remain distinct. For example,
upstream version `1.8.1`, revision `1`, and bottle rebuild `3` map to
`PackageVersion: 1.8.1_1.bottle.3`, keg directory `jq/1.8.1_1`, and `Rebuild: 3`.
Earlier prototype manifests without the suffix remain readable, but should be
reimported before relying on rebuild-aware upgrade ordering.

## Example

This is an illustrative manifest, not downloadable package metadata:

```yaml
PackageIdentifier: Pinget.Homebrew.jq-929958fef124d633
PackageVersion: 1.8.1_1.bottle.3
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
      MinimumVersion: 6.9.10.bottle.0
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

The importer maps one pinned target at a time: Sonoma macOS x64/arm64 or glibc
Linux x64/arm64, with runtime dependency identities and optional curated minima.
It does not fetch the formula API, evaluate Ruby, or automatically curate a tap.
The publisher mirrors bottle bytes unchanged and produces a Homebrew-only
`brew.msix`; mixed Windows/Homebrew snapshots are rejected.

Both repositories route native install, list, repair, upgrade, and uninstall
through managed receipts. Linux relocates ELF RUNPATH/interpreters with
`patchelf`; macOS uses `otool`, `install_name_tool`, and ad-hoc `codesign`.
Source updates require `psign-tool`, the expected `Pinget.HomebrewSource`
identity/publisher, and explicit signing trust anchors before cache activation.
Real jq/oniguruma signed-source installation has been executed through both
CLIs on Linux x64 and macOS x64/arm64. Windows acceptance covers publishing
and signature verification, not Homebrew source consumption. See the
[compatibility boundary](homebrew-bottle-compatibility.md).
