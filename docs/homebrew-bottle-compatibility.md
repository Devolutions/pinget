# Homebrew bottle compatibility

This is a curated formula-bottle prototype, not general Homebrew compatibility.
Both CLIs have verified a signed static `brew.msix`, installed real jq 1.8.1
and oniguruma 6.9.10 bottles on Linux x64/arm64 and macOS x64/arm64, and
executed jq using private bottle libraries rather than system substitutes.
Both publishers have been consumed by the opposite runtime. Actual CLI checks
also cover a labelled synthetic rebuild-only upgrade, bidirectional
cross-runtime repair, dependency-protected uninstall, and rejection of
tampered signed source updates without changing the active cache.

## Current support matrix

| Area | Supported now | Explicitly not supported |
| --- | --- | --- |
| Formula input | One pinned local formula JSON object per import; explicitly labelled OCI adaptations | API fetching, automatic tap curation, casks, Ruby evaluation |
| Bottle target | Sonoma macOS x64/arm64 and glibc Linux x64/arm64 metadata; native architecture only | Other importer tags, emulation, musl, platform-independent targets |
| Formula metadata | Identity, stable version, revision, aliases, old names, keg-only, description, homepage, SPDX license, runtime dependency identities/minima | Nonzero version schemes, `uses_from_macos`, requirements, conflicts, optional dependencies, variations |
| Bottle classes | `any` and `any_skip_relocation` are preserved as distinct metadata values | Fixed-cellar bottles |
| Archive handling | Digest verification, unchanged mirroring, bounded extraction, contained regular files/directories/links, executable modes | Traversal, escaping/dangling/chained links, special devices/FIFOs, hooks |
| Relocation | UTF-8 text/placeholders; ELF RUNPATH and host interpreter; Mach-O dependency names, rpaths, IDs and ad-hoc signing | Arbitrary embedded binary paths, application bundles, general formula-specific patching |
| Static publishing | Homebrew-only snapshots, mirrored bytes, preindexed DB in signed `brew.msix`, external Psign verification | REST serving, mixed Homebrew/Windows snapshots |
| Source update | `brew.msix` only, no redirects, digest/trust/signer identity/EKU checks before index activation | Windows Homebrew source clients, unsigned sources, implicit private root installation, acceptance of failed updates |
| Client operations | Native install/list/upgrade/repair/uninstall, shared receipts, dependency ordering, normal-failure rollback, ownership checks | Homebrew/Ruby, system-prefix installation, arbitrary installer arguments, uninstall-previous, durable crash recovery |

Manifest readers recognize more historical tags than the importer. That does
not claim those targets or arbitrary bottles are installable. Real execution
has been verified only for Linux x64/arm64 and macOS x64/arm64.

## Import and publication checks

The importer verifies supplied archive bytes against pinned metadata, maps
runtime dependencies, rejects unsupported requirements, and writes deterministic
manifests/reports. It does not authenticate a formula JSON snapshot. The real
acceptance inputs are labelled OCI adaptations, with OCI digest/receipt
provenance and an operator-verified relocation policy; they are not claimed
historical API snapshots.

The publisher requires each Homebrew installer URL and hash to identify one
flat, lowercase SHA-256-named `.tar.gz` file in the sibling `bottles/` input
directory. It rechecks the digest, includes the unchanged bytes in the
immutable snapshot and published destination, and selects `brew.msix` for the
Homebrew-only profile. Mixed Homebrew/Windows snapshots are rejected. The
source update path requires `psign-tool` and explicit publisher/trust
configuration. Successful cryptographic acceptance has been exercised using
a disposable code-signing PKI and a loopback static HTTPS host.

## Native lifecycle limits

The private prefix is `<AppRoot>/homebrew`; downloads use the same private app
root. Normal failures roll back keg/link changes. Receipts record package/source
identity, catalog version, actual archive keg version, dependencies, and owned
links. Modified/unowned links and kegs are not overwritten or removed.
Dependency removal is refused while dependents remain unless explicitly forced.

Extraction limits are 100,000 entries and 2 GiB expanded bytes; relocation
rejects files over 256 MiB. Only contained, already resolvable link targets are
accepted. This deliberately rejects some legitimate Homebrew archives.
Linux requires `getconf`, glibc, and `patchelf`; macOS requires `sw_vers`,
`otool`, `install_name_tool`, and `codesign`. No formula hooks execute.

An exclusive `operations.lock` prevents concurrent lifecycle operations.
There is no durable crash journal: a killed process can leave its lock/staging
data. Operator inspection and recovery are required before another operation;
do not blindly remove locks while an operation may still run. Installing an
entire dependency closure is not one atomic transaction.

## Acceptance and remaining gates

Keep real signed-source and lifecycle checks distinct from synthetic unit
fixtures. Rebuild-only transitions must use increasing catalog rebuild versions
and must not be described as authentic upstream rebuilds when synthesized for
a test. Hosted run
[44925419503](https://github.com/Devolutions/pinget/actions/runs/44925419503),
attempt 2, passed 18 checks each on Linux x64/arm64 and macOS x64/arm64, and
five Windows publishing/signature checks. This covers the pinned jq/oniguruma
chain, not all formulas.

The same run decoded Rust and .NET portable MSZIP fixtures from each Linux
and macOS runner with the Windows Compression API. The separate
[Windows-profile CI run](https://github.com/Devolutions/pinget/actions/runs/44924953057)
passed publisher interoperability and signed stock WinGet acceptance. These
are distinct gates; see the [publisher contract](../tests/publisher/contract.md)
for their coverage and limits.
