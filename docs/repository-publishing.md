# Publishing a static WinGet source

Pinget's repository publisher converts Windows WinGet YAML manifests into a v2
`Microsoft.PreIndexed.Package` source. The Rust `pinget-publisher` crate and
the .NET `Devolutions.Pinget.Publisher` library expose the same workflow through
their respective `pinget repository` commands.

For embedding, use the `Pinget.Publisher` namespace in .NET. The publisher
library is separate from the client `Devolutions.Pinget.Core` package.

This is publishing tooling, not a server. Clients need an ordinary static HTTPS
host, not a REST service, Git checkout or access to the build-state database.
Installer binaries can remain at their immutable vendor or internal storage
URLs; the build does not download or execute them.

## Requirements

- Standard Windows WinGet manifests, either singleton or complete multifile
  sets (version, installer, default locale and optional additional locales).
- Separate directories for inputs, publisher state and published files.
  Do not put one inside another or use symlinks/reparse points in them.
- For publication on Windows, the Windows SDK's `makeappx.exe` and
  `signtool.exe`. `repository doctor` reports tool availability; explicit tool
  paths can be supplied when they are not discoverable.
- A code-signing certificate with its private key in a PFX, and a publisher
  distinguished name matching that certificate.

Building, verification and publication currently require Windows because
sidecars use the native Windows Compression API. `status` and `diff` can
inspect state on other platforms; `doctor` reports the missing capability.
Do not infer publishing support from the availability of the CLI on a platform.
Unsupported toolchains fail rather than silently produce unsigned output.

## Build, inspect and publish

Use the Rust or .NET CLI, not both at once against the same state directory.
The directories below are siblings, outside the input manifest tree:

```powershell
pinget repository doctor
pinget repository build --input .\manifests --state .\publisher-state
pinget repository diff --input .\manifests --state .\publisher-state --output json
pinget repository status --state .\publisher-state --output json
pinget repository verify --state .\publisher-state
```

`--manifests` is an alias for `--input`. `--output` retains its existing meaning
of `text`, `json` or `yaml`; it is not a directory option. `diff` compares raw
input content with the last successful build. Publication is a separate state
transition. Inspection commands do not create missing state.

Builds produce immutable snapshots, not a mutable directory that publication
rescans later. The build identifier is derived from the sorted input inventory
and publisher-format version. A failed build must not replace the previous
successful snapshot. The build-state database is private publisher bookkeeping:
never host it or substitute it for `Public/index.db`.

Each package version produces one merged manifest and one version-data record.
The default locale remains the main manifest; additional locales are retained
under `Localization`, not exposed as duplicate package versions.

The state and snapshots record `artifactFormatVersion=2`. Older publisher
state is rejected without automatic migration; use a new state directory
rather than editing its schema markers. Existing version-1 destination
ownership records can still be verified and updated through normal publication.

State lives in `publisher.db`. Each snapshot is under `builds\<buildId>`,
with `snapshot.json`, `Public\index.db` and hosted files under
`feed\manifests` and `feed\packages`. Use `--build <buildId>` with `publish`
or state verification to select a snapshot other than the latest.

Prepare the PFX outside both the input and published directories. Supply the
password to the process through your CI secret mechanism, not a literal command
line or checked-in file:

```powershell
# SOURCE_PFX_PASSWORD is already populated by the CI secret store.
pinget repository publish --state .\publisher-state --destination .\public `
    --name Pinget.InternalSource --publisher 'CN=Example Source Publisher' `
    --version 1.0.0.0 --pfx .\secrets\source.pfx `
    --password-env SOURCE_PFX_PASSWORD

pinget repository verify --destination .\public
```

The source package version is an explicit **four-component MSIX version**,
separate from any application version. Keep the source identity and publisher
stable, and increase the source version on each changed publication.

For local format development only, replace the signing options with
`--unsigned-development`. This is not a production trust configuration. The
publisher does not install certificates, configure WinGet source trust or
change the caller's sources.

The PFX password is not stored in publisher state or normal diagnostics.
`signtool`'s PFX interface requires passing it to the signing child process;
privileged local process inspection can therefore observe it. Use a protected
build agent and restrict access to both that agent and the PFX. The password
environment-variable indirection is not a guarantee of process-level secret
isolation.

## Host the result

Copy the **entire published directory**, maintaining relative paths:

| Published file | Purpose |
| --- | --- |
| `source2.msix` | Signed source package containing `Public/index.db` |
| `manifests/<sha256>.yaml` | Merged manifest bytes referenced by version data |
| `packages/<id>/<hash-prefix>/versionData.mszyml` | Compressed version metadata referenced by the index |

A static website, reverse proxy, object-storage HTTPS endpoint or CDN can serve
these files. Enable `GET` and `HEAD`; retain `ETag`/`Last-Modified` metadata and
conditional requests. Ensure the host serves `.msix`, `.mszyml` and `.yaml`
rather than rejecting unknown extensions or rewriting them to a website page.
Use `application/octet-stream` when no more specific type is configured.
Range requests for large source packages are useful.

Use a short cache lifetime or revalidation for `source2.msix`. Hash-addressed
manifests and sidecars may be cached for a long time. A CDN must not serve a
new root package before its referenced objects are readable through that CDN.

The local publisher promotes new immutable metadata **before** replacing
`source2.msix`. A later cloud upload is a separate operation: upload new
manifests and sidecars first, verify availability, then replace the root MSIX
last. A recursive sync does not inherently preserve that ordering.

Keep old hash-addressed metadata for clients holding cached indexes. There is
no automatic garbage collection. Do not overwrite an existing content path
with different bytes, delete a destination marker, or mix unrelated website
content into a publisher-owned destination.

## Configure clients and trust

Distribute the public certificate/chain through your normal endpoint-management
process before registering the source. Never distribute the PFX/private key.
A source-signing certificate and the HTTPS server certificate have different
jobs: MSIX signing authenticates source metadata; HTTPS authenticates transport.
Installer hashes and installer Authenticode checks remain separate.

Use a certificate chain trusted by your endpoints. A self-signed development
certificate requires an explicit trust decision in an isolated test machine;
do not add a test root certificate to production machines as a shortcut.

Register the **base URL**, not the build-state directory or a direct MSIX URL:

```powershell
winget source add --name InternalApps --arg https://packages.example.com/apps `
    --type Microsoft.PreIndexed.Package
winget source update --name InternalApps
winget search --source InternalApps
winget show --id Example.App --exact --source InternalApps
```

The publishing commands never run these client commands automatically.
Hosting authentication and endpoint credentials are the host/client's concern:
this publisher does not add a secret store, token broker or authenticated
REST-source protocol.

## CI and compatibility

In CI, persist the publisher state between runs if you want meaningful diffs,
but keep it separate from deployment output. Run `build`, inspect `diff`, run
`verify`, then publish with the next source version. Upload to a staging
location before index-last promotion. Restrict signing credentials to trusted
branches and protected deployment jobs; do not expose them to pull-request code.

The manual [source-build workflow](../.github/workflows/repository-build.yml)
is a minimal unsigned build example: give it a repository-relative manifest
directory, and it uploads a verified snapshot for inspection. It does not sign,
register a source or deploy a feed. Add signing and index-last deployment only
in a protected job appropriate for your hosting environment.

The shared [format contract](../tests/publisher/contract.md) and independently
generated fixtures under `tests\publisher` record the compatibility baseline.
The initial full-index fixture was accepted by stock WinGet **1.29.380** in
Windows Sandbox for source add/update, search, show and hash-verified download.
That fixture result alone does not certify every publisher code path, older
client version, package manifest field, or signing toolchain.

Current production conformance also covers matching Rust/.NET build identities,
canonical manifest and sidecar bytes, logical SQLite records, reciprocal
state reopening and unsigned destination verification. Before the
artifact-format-v2 review hardening, signed output from both publishers
passed stock WinGet 1.29.380 acceptance, including source
add/update, search/show, hash-verified download, version bumps, package
addition/removal and retention of metadata for cached indexes. Both Pinget
readers consumed both publishers' 500-version feeds and selected `1.0.499`,
exercising multi-block MSZIP sidecars end to end. These results establish the
tested client/toolchain baseline, not universal compatibility with older
clients or every manifest field. Current artifact-format-v2 outputs passed
the local exact-byte comparison and independent SQLite/MSZIP checks, but
have not received a fresh signed stock-client acceptance run.

Failure tests cover packaging/signing errors, update promotion rollback and
destination locking. The .NET publisher also has an end-to-end regression
for initial publication failure after metadata copying: a controlled MSIX
identity mismatch leaves the destination empty and no publication recorded,
then a valid retry succeeds. The equivalent Rust post-copy failure path
remains unverified; Rust verifies the staged MSIX before copying metadata.

Stock-client acceptance and signature trust checks belong in a fresh Windows
Sandbox or an ephemeral Windows CI runner. They must not register test sources
or trust certificates in a developer's usual profile.

This feature does not define a cross-platform manifest format, macOS package
installation, managed script bundles, assignment policy, a REST source,
background enforcement or new PowerShell cmdlets.

Manifest ingestion accepts an explicit set of `ManifestVersion` values from
`1.0.0` through `1.10.0` and checks required fields, document roles and merged
structure. These checks are not a replacement for full upstream JSON-schema
validation of every manifest property. Search normalization reuses Pinget's
existing normalizers; compatibility evidence covers the tested fixtures, not
every possible name or publisher string.

## Building and distributing the libraries

The reusable Rust crate is `rust\crates\pinget-publisher`; the .NET project is
`dotnet\src\Devolutions.Pinget.Publisher`. Both existing CLI packages include
the repository commands. The .NET library targets .NET 8 and .NET 10.

The Rust publisher depends on the matching core's normalization APIs. When
publishing crates, release the updated `pinget-core` before
`pinget-publisher`; building a packaged publisher against an older registry
core with the same version will not find those new APIs. The release-plz
workspace configuration publishes dependencies first. Use
`scripts\Set-PingetVersion.ps1` to keep package versions and dependency
requirements synchronized.
