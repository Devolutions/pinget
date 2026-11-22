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
| Formula input | One local object per offline import; bounded live `homebrew/core` dependency-closure importer | Authenticated API snapshots, automatic tap curation, casks, Ruby evaluation |
| Bottle target | Sonoma and Sequoia macOS x64/arm64 and glibc Linux x64/arm64 metadata; native architecture only | Other importer tags, emulation, musl, platform-independent targets |
| Formula metadata | Identity, stable version, revision, aliases, old names, keg-only, description, homepage, SPDX license, runtime dependency identities/minima | Nonzero version schemes, `uses_from_macos`, requirements, conflicts, optional dependencies, variations |
| Bottle classes | `any`, `any_skip_relocation`, and target-matched fixed-cellar paths | Unknown or cross-target fixed cellars |
| Archive handling | Digest verification, unchanged mirroring, bounded extraction, contained regular files/directories/links, executable modes, bounded internal symlink chains | Traversal, escaping/dangling/cyclic or over-31-link chains, special devices/FIFOs, hooks |
| Relocation | UTF-8 text/placeholders; dynamic ELF RUNPATH and host interpreter (static ELF left unchanged); Mach-O dependency names, rpaths, IDs and ad-hoc signing; known fixed-cellar prefixes | Arbitrary embedded binary paths, application bundles, general formula-specific patching |
| Static publishing | Homebrew-only snapshots, mirrored bytes, preindexed DB in signed `brew.msix`, external Psign verification | REST serving, mixed Homebrew/Windows snapshots |
| Source update | `brew.msix` only, no redirects, digest/trust/signer identity/EKU checks before index activation | Windows Homebrew source clients, unsigned sources, implicit private root installation, acceptance of failed updates |
| Client operations | Native install/list/upgrade/repair/uninstall, shared receipts, dependency ordering, journaled per-formula transactions with crash recovery shared by both runtimes, ownership checks | Homebrew/Ruby, system-prefix installation, arbitrary installer arguments, uninstall-previous, atomic dependency-closure transactions |

Manifest readers recognize more historical tags than the importer. That does
not claim those targets or arbitrary bottles are installable. The hosted
workflow now attempts at least 20 live catalog formula roots on each supported
Unix runner, but that expanded gate has not passed. Run
[44941756278](https://github.com/Devolutions/pinget/actions/runs/44941756278),
attempt 2, failed on all four Unix runners during live catalog import. The
last verified real execution remains the pinned jq/oniguruma chain on Linux
x64/arm64 and macOS x64/arm64.
Run [44946344342](https://github.com/Devolutions/pinget/actions/runs/44946344342),
attempt 2, also fell short of the 20-root threshold on all four Unix runners.
The live catalog gate still selects the Sonoma bottle tag, which is supported
on the macOS 15 runner, while preserving the pinned Sonoma lifecycle test. The
latest failure exposed chained bottle symlinks in real packages; extraction
now resolves and validates bounded internal link chains before creating links.
The revised gate still needs hosted verification.

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
root. Failures and interruptions roll back keg/link/receipt changes (see
below). Receipts record package/source
identity, catalog version, actual archive keg version, dependencies, and owned
links. Modified/unowned links and kegs are not overwritten or removed.
Dependency removal is refused while dependents remain unless explicitly forced.

Extraction limits are 100,000 entries and 2 GiB expanded bytes; relocation
rejects files over 256 MiB. Only contained, already resolvable link targets are
accepted. This deliberately rejects some legitimate Homebrew archives.
Linux requires `getconf`, glibc, and `patchelf`; macOS requires `sw_vers`,
`otool`, `install_name_tool`, and `codesign`. No formula hooks execute.

## Interrupted operations and recovery

Each install, upgrade, repair, or uninstall of one formula is one journaled
transaction. Before changing any visible state, the client durably writes
`<AppRoot>/homebrew/transaction.json` (file and directory `fsync`). It records
the operation, the staged keg, any same-version keg moved aside as a backup,
an obsolete keg version, the before/after target of every `opt/` and command
link, and the previous and new receipts. The state changes from `Prepared` to
`Committed` only after the keg, links, and receipt are written and synced.

Recovery runs automatically before every lifecycle operation and before `list`
reports packages (unless another operation is active):

- `Prepared` is rolled back: the previous keg, links, and receipt are restored
  exactly, so a failed upgrade or repair leaves the previous working package in
  place, and a failed first install leaves nothing behind.
- `Committed` is finished: backups, staging data, and the obsolete keg are
  removed.
- Every recovery step is idempotent; an interrupted recovery resumes later.
- Rust and .NET share the journal and lock format, so either runtime recovers
  the other's interrupted operation.
- With no journal, leftovers under `.staging/` come from interrupted extraction
  and are removed. Temporary links are created there, never in `bin/` or `opt/`.

Recovery stops with an explicit error, keeps the journal, and removes nothing
when it cannot prove ownership: an unreadable or unsupported journal, a link
changed outside Pinget, or a keg replaced or recreated outside Pinget. Inspect
and repair the prefix manually before retrying. Files that Pinget does not own,
including other entries in `bin/`, are never touched.

`operations.lock` is an advisory OS lock (`flock`) held only by a running
operation. The OS releases it when the process exits, so the remaining file
is not a stale lock and should not be deleted. A concurrent operation fails
with an explicit "in progress" error. On normal completion or a reported
failure, both runtimes explicitly unlock before closing the descriptor; a
concurrently spawned child's inherited descriptor cannot prolong that lock.
The .NET receipt and journal serializers use generated metadata, retaining
the shared JSON format without reflection or runtime code generation.

When an upgrade or repair changes runtime dependencies, the client reports
added dependencies and dependencies that are no longer required. Packages that
are no longer required remain installed; there is no automatic removal.

Limits:

- A dependency closure is a sequence of per-formula transactions, not one
  atomic transaction. If an interruption happens after a dependency commits,
  that dependency remains installed and is listed, and only the requesting
  formula is rolled back.
- Power-loss durability depends on `fsync` semantics of a local filesystem.
  Network filesystems and filesystems without `flock` are not supported.
- Downloaded bottles in `<AppRoot>/homebrew-downloads` are outside the
  transaction.
- `PINGET_HOMEBREW_TEST_INTERRUPT=<formula>:<step>` is a test-only seam. It
  terminates the process with exit code 86 at a named checkpoint, for example
  `jq:install-link`. Do not set it outside tests.

## Acceptance and remaining gates

Keep real signed-source and lifecycle checks distinct from synthetic unit
fixtures. Rebuild-only transitions must use increasing catalog rebuild versions
and must not be described as authentic upstream rebuilds when synthesized for
a test. Hosted run
[44935233295](https://github.com/Devolutions/pinget/actions/runs/44935233295),
attempt 2, passed on code commit `4cc3638`: 26 checks each on Linux x64/arm64
and macOS x64/arm64, and five Windows publishing/signature checks. The Unix
checks include process interruption during initial installation and upgrade,
recovery by the opposite runtime, preservation of committed dependencies, and
execution of the restored previous package. Focused unit tests additionally
cover interrupted repair, uninstall, and recovery, outside modifications,
corrupt journals, and lock exclusivity with inherited descriptors. This covers
the pinned jq/oniguruma chain, not all formulas.

The same run decoded Rust and .NET portable MSZIP fixtures from each Linux
and macOS runner with the Windows Compression API. The separate
[Windows-profile CI run](https://github.com/Devolutions/pinget/actions/runs/44935250942)
passed on the same code commit, including publisher interoperability and signed
stock WinGet acceptance with the native-AOT CLI. The recovery platform and
JSON trimming/AOT warnings are absent from these final runs. These are distinct
gates; see the [publisher contract](../tests/publisher/contract.md) for their
coverage and limits. Target-matched fixed-cellar parsing, importing, and
prefix relocation have focused synthetic tests; no hosted real fixed-cellar
bottle has been confirmed yet. No claim is made that arbitrary formulas
install.
