# WinGet v2 publisher compatibility evidence

This file records the independently derived v2 source-package contract for the
publisher compatibility gate. The evidence is pinned to read-only upstream
WinGet commit `cf71e38eccd2b9035c03c7408f5328cd0e4fdee8`; it is not a runtime or
build dependency.

## Database package layout

A v2 source is served from a base URL. WinGet requests `source2.msix`; its
package manifest exposes `Public`, and the packaged database must be
`Public/index.db`. The same base URL serves external package artifacts.
`WinGetSourceCreator/WinGetLocalSource.cs` emits this path and package layout.

`index-schema.sql` captures the published data tables retained after
`Microsoft/Schema/2_0/Interface_2_0.cpp::PrepareForPackaging`. In particular:

- The `metadata` table has the fixed WinGet shape: `name TEXT PRIMARY KEY NOT
  NULL`, `value TEXT NOT NULL`, `WITHOUT ROWID`.
- `packages` has an integer `rowid` primary key followed by `id`, `name`,
  nullable `moniker`, `latest_version`, nullable ARP bounds, and nullable
  `hash BLOB`. The package identifier is not the SQL primary key.
- `tags2` and `commands2` use integer-rowid value tables and `*_map` tables
  keyed by `(value-rowid, package-rowid)`.
- Correlation tables `pfns2`, `productcodes2`, `norm_names2`,
  `norm_publishers2`, and `upgradecodes2` use `(value, package)` composite
  primary keys without rowids.
- Writer-side indexes, internal v1 tables, and package-update tracking tables
  are removed during packaging. The candidate schema was corrected to reflect
  those retained shapes; it must not be interpreted as the schema of the
  private mutable writer database.

The required schema markers are `majorVersion=2` and `minorVersion=0` in
`metadata`. Upstream creates a `databaseIdentifier` GUID and writes
`lastwritetime`; both are included in the fixture. The SQLite `user_version`
pragma is not the WinGet schema marker.

## Version data and hosted manifest references

Each package row's `hash` is the 32-byte SHA-256 of the exact compressed
version-data bytes. The lowercase hex form identifies the sidecar directory:
`packages/<PackageIdentifier>/<first-eight-hash>/versionData.mszyml`.
`PackageVersionDataManifest.cpp` defines the YAML keys `sV`, `vD`, `v`,
`aMiV`, `aMaV`, `rP`, and `s256H`, with schema version `"1.0"`. Each version
references a hosted manifest by relative URL path and lowercase hexadecimal
SHA-256 of its exact bytes. The upstream path construction retains the package
identifier and takes the first eight lowercase hash characters.

Upstream calls Windows Compression API `CreateCompressor` with
`COMPRESS_ALGORITHM_MSZIP` and stores the bytes returned by `Compress` directly;
there is no additional application framing in `Compression.cpp`. The fixture
generator uses that Windows API and verifies the result with
`CreateDecompressor`/`Decompress`, then hashes the bytes actually stored.

The exact manifest serialization, version ordering, and search-normalization
values are not inferred from the schema files. The locally authored singleton
fixture was accepted by stock WinGet 1.29.380 in a fresh Windows Sandbox:
source add/update, exact-ID search, show of the hosted manifest, and download
of the harmless payload all succeeded. The downloaded payload's bytes and
SHA-256 matched the locally authored fixture; it was not installed. This
acceptance covers the singleton full-index case only, not delta behavior,
multiple-version ordering, or broad search-normalization equivalence.

## Test fixture and independent acceptance

The fixture is locally authored and deliberately harmless. It is generated
under ignored `artifacts/publisher-contract/` (or an external session path),
never under the source fixture directory. `source2.msix`, the index, sidecar,
signing certificate, payload, and server logs are generated test output, not
committed source assets.

The required independent gate is a fresh Windows Sandbox with stock WinGet.
Trust for the ephemeral signing certificate and the test source registration
are confined to that Sandbox. WinGet's package deployment path required the
fixture signer in the Sandbox machine `Root` store; trusting it only in the
Sandbox user's `TrustedPeople`/`Root` stores was insufficient (`0x800B0109`).
The host certificate stores were not changed. The test adds/updates the source,
searches and shows the fixture package, then downloads the harmless payload
and verifies its expected SHA-256 without installing it. Independent
PowerShell helpers inspect SQLite through the native SQLite API and perform a
Windows Compression API MSZIP round trip. These additional checks do not
substitute for the stock client.

## Non-upstream publisher policy (separate from the independent fixture gate)

The build-state schema, build IDs, manifest canonicalization, snapshot layout,
CLI switches, ownership marker, atomic promotion behavior, and cross-language
state interoperability are Pinget design decisions, not WinGet compatibility
requirements. They are validated separately by the production-publisher
conformance suite, not by the independent fixture gate above.

State database schema is an implementation detail validated through the
production writers and readers. Rust embeds its state schema in the publisher
library; the conformance suite cross-opens and verifies state created by each
implementation rather than maintaining a separate test-side schema copy.

## Pinget publisher command contract

The work directory is always selected with `--state`; `--output` remains the
global result format selector (`text`, `json`, or `yaml`) and must never be
used as a state-directory alias.

```powershell
pinget repository build --input <manifest-directory> --state <work-directory>
pinget repository diff --input <manifest-directory> --state <work-directory>
pinget repository status --state <work-directory>
pinget repository verify --state <work-directory> [--build <build-id>]
pinget repository verify --destination <published-directory>
pinget repository doctor
pinget repository publish --state <work-directory> --destination <published-directory> `
  --name <stable-msix-identity> --publisher <certificate-subject> --version <four-part-version> `
  (--pfx <file> --password-env <environment-variable> | --unsigned-development)
```

The `build` command creates a snapshot; it does not publish or sign an MSIX.
The `publish` command consumes that snapshot and uses `source2.msix` as the
published source-package path. Test harnesses may pass a built CLI executable
or an already-built .NET CLI assembly, but must preserve these argument
meanings. This command contract is a Pinget implementation expectation and is
not part of the upstream WinGet format.

## Production publisher cross-language contract (tested separately)

The current publisher conformance gate exercises the actual Rust and .NET
writers/readers. Both implementations create immutable snapshots at
`<state>/builds/<buildId>/feed/`; the build
snapshot contains the feed artifacts and index plus `snapshot.json`. State
commands are read-only when inspecting missing/uninitialized state.
Equivalent manifest trees yield matching build identities, and Rust and .NET
can each reopen and verify state created by the other.

The current publisher artifact format is version 2, recorded as
`artifactFormatVersion=2` in private state metadata and as
`artifactFormatVersion: 2` in `snapshot.json`. The SQLite `user_version` and
structural `schemaVersion` remain 1. Build identity uses the
`pinget-publisher-state-v2\n` salt. Legacy state or snapshots without the
current artifact-format marker are rejected without mutation; use a fresh
`--state` directory rather than relying on automatic migration. The existing
destination ownership marker remains independently versioned by its existing
schema and does not require this snapshot-format field.

Cross-language comparisons pin canonical manifest bytes, raw input inventory,
logical SQLite index contents, version-data sidecars and their hashes, and the
index digest recorded by each snapshot against that snapshot's exact
`Public/index.db` bytes (the two SQLite files are not required to be bytewise
identical). The helpers independently inspect SQLite tables through the native
SQLite API and validate sidecar bytes with the Windows native MSZIP API. The
YAML helpers use the existing YamlDotNet assembly from the .NET CLI build.
These checks establish the tested outputs and interoperability; the private
state schema remains governed by the embedded production schemas.

In JSON mode, the tested production command reports use these shared field
sets and shapes (nullable values are `null`; command failures are nonzero and
do not return a success report):

| Command | JSON fields |
| --- | --- |
| `repository build` | `buildId`, `snapshotDirectory`, `manifestCount`, `packageCount`, `changedFiles`, `removedFiles` |
| `repository diff` | `lastBuildId`, `published`, `added`, `changed`, `removed` |
| `repository status` | `lastBuildId`, `lastPublishedBuildId`, `buildCount`, `lastBuildTimestamp` |
| `repository verify` / successful `repository publish` | `valid`, `subject`, `artifactCount`, `buildId` |
| `repository doctor` | `operatingSystem`, `psignToolPath`, `nativeMsZipAvailable` |

`doctor.operatingSystem` is informational and is intentionally not compared
between platforms/implementations. `snapshotDirectory` is absolute. Build
report `changedFiles` counts new or modified input files relative to the prior
build (zero on the first or unchanged build); `removedFiles` counts removed
inputs. Destination verification uses `.pinget-publisher.json` as the
ownership record, checks the published MSIX/index/identity and exact artifact
inventory, and rejects untracked destination files rather than adopting or
deleting them. The marker and private state DB are Pinget contracts, not
WinGet-defined structures.

Publication recovers from an interrupted run of either implementation. It
removes publisher temporaries, adopts untracked files whose relative path and
SHA-256 equal the snapshot being published (also in a destination without a
marker that holds nothing else), and refuses snapshot paths that differ only by
casing from a published path. The package swap is a three-step commit: the
marker is first rewritten to the new publication with
`pendingCommit: { previous: <prior marker or null> }`, then `source2.msix` is
replaced, then `pendingCommit` is dropped. A marker with `pendingCommit` is
resolved by the next publish (and read the same way by `verify`): when
`source2.msix` matches the marker it is committed, otherwise `previous` is
restored together with the interrupted run's fully copied artifacts. Both
implementations build the package under `--state`; the only swap leftovers in
the destination are root-level publisher temporaries (`.source2.*.new`,
`source2.msix.<guid>.tmp`, or a legacy Rust `.publish-<buildId>.staging`
directory). `verify` ignores these only while the marker carries
`pendingCommit`, and ignores ownership-marker write temporaries
(`.pinget-publisher.json.new`, `.pinget-publisher.json.<guid>.tmp`) always;
every other untracked file is still rejected, and the next publish deletes the
temporaries. Re-verifying the package about to be replaced checks digests and
signer only, so an expired or rotated signing certificate never blocks the next
publication; the newly signed package must have a currently valid, trusted
certificate.

Index content follows WinGet's manifest model: root `AppsAndFeaturesEntries`,
`ProductCode` and `PackageFamilyName` apply only to installers that lack them
and whose effective type uses them; per-version ARP ranges come from
exe/inno/msi/nullsoft/wix/burn/msix installers; overlapping ranges across
versions fail the build; the package row holds the latest version's range; and
package rows are ordered by `OrdinalIgnoreCase` identifier.

The producer emits Windows Compression API MSZIP framing. Native independent
validation and core reader regression tests cover sidecars spanning multiple
32 KiB blocks, including the version-data record after the first block; this
is separate from the singleton stock-WinGet fixture acceptance above. Legacy
single-block `CK`/raw-deflate input remains a separate reader compatibility
path.
