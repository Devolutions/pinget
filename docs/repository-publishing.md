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
- For signed publication, [`psign-tool`](https://github.com/Devolutions/psign)
  **0.8.2 or later**, a cross-platform Authenticode signer: `dotnet tool
  install -g Devolutions.Psign.Tool`. Earlier versions produce MSIX signatures
  that Windows rejects. `repository doctor` reports whether it was found on
  `PATH` or in the .NET global tool directory; `--psign-tool` supplies an
  explicit path. The Windows SDK (`makeappx.exe`, `signtool.exe`) is not used:
  the publisher writes the MSIX container itself.
- A code-signing certificate with its private key in a PFX, and a publisher
  distinguished name matching that certificate's subject.

Building requires Windows because sidecars are compressed with the native
Windows Compression API. `publish`, `verify`, `status` and `diff` also run on
Linux and macOS, so a snapshot built on Windows can be signed and published
elsewhere. Do not infer publishing support from the availability of the CLI on
a platform. A missing signing tool fails rather than silently producing
unsigned output.

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

The PFX password is not stored in publisher state or normal diagnostics. The
publisher hands it to `psign-tool` through a response file in a private
temporary directory, deleted after signing, rather than on the command line.
It is still present briefly on disk and in the signing process, so use a
protected build agent and restrict access to both that agent and the PFX.

After signing, the publisher checks the package digests, that the signer
certificate subject matches `--publisher` and carries the Code Signing usage,
and that the signature chains to a trusted root. On Windows the root must be
trusted by the machine, like `signtool verify /pa`; on Linux and macOS psign's
Microsoft AuthRoot anchors are used. For a private or self-signed root, pass
its certificate with `--trusted-ca` (repeatable) to `publish` and
`verify --destination`.

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

## Vendor static feed workflow (`pinget` + `psign`)

To publish a vendor-owned static WinGet source alongside `microsoft/winget-pkgs`
(including open pull requests that have not merged upstream yet), use the
PowerShell 7.4+ helper scripts under `scripts\`:

1. **Sync merged + pending manifests** (`scripts\Sync-WinGetManifests.ps1`):
   sparse-clones a manifest subtree (default `manifests/d/Devolutions`) from
   `microsoft/winget-pkgs` and applies open pull requests authored by the
   vendor bot (`devolutionsbot` by default) via 3-way git merge (`git apply
   --index --3way`), preserving renames and refusing stale/conflicting hunks.
2. **Build and sign the static site** (`scripts\Build-DevolutionsStaticFeed.ps1`):
   runs `pinget repository build`, `pinget repository verify`, and
   `pinget repository publish` with `psign-tool`. When `-PackageVersion` is
   omitted, it starts at `1.0.0.0`, reuses the existing version for unchanged
   snapshots, and increments the 4-part AppX version automatically when build
   content changes. In isolated test environments, pass `-GenerateTestPki` to
   mint a disposable 2-tier PKI (`root-ca.cer`, `codesign.pfx`, and
   `tls-server.pfx`) via `psign-tool portable generate-root-ca`,
   `issue-code-signing-cert` and `issue-tls-server-cert` (`root-ca.pfx` is
   deleted immediately), then verify `source2.msix` against `--trusted-ca
   root-ca.cer` without altering the build machine's certificate store. Never
   export or trust `-GenerateTestPki` material outside an isolated test machine.
3. **Serve over HTTPS in a test environment** (`scripts\Serve-StaticFeed.ps1`):
   serves the published directory concurrently over TLS (default unprivileged
   port `8443`; pass `-Port 443` when elevated) with `GET`/`HEAD`, `ETag` (`304
   Not Modified`), single-range `206 Partial Content` / `416 Range Not
   Satisfiable`, `Cache-Control` (`no-cache` for `source2.msix`, immutable
   caching for hashed manifests/sidecars), dotfile blocking, and transient TLS
   key cleanup on exit. In an isolated test VM, import only `root-ca.cer` into
   `Cert:\LocalMachine\Root` to trust both HTTPS transport and `source2.msix`
   `AppxSip` / `WinVerifyTrust` verification. In production, upload new
   `manifests/` and `packages/` objects to static HTTPS storage/CDN first, then
   promote `source2.msix` signed with the vendor's publicly trusted code-signing
   certificate.

```powershell
$env:STATIC_FEED_TLS_PASSWORD = '<random-test-secret>'
pwsh -NoLogo -NoProfile -File .\scripts\Build-DevolutionsStaticFeed.ps1 `
    -WorkspaceDirectory .\artifacts\devolutions-feed `
    -GenerateTestPki `
    -TlsPasswordEnvironmentVariable STATIC_FEED_TLS_PASSWORD `
    -TlsDnsNames @('winget.devolutions.test', 'localhost') `
    -TlsIpAddresses @('127.0.0.1')

pwsh -NoLogo -NoProfile -File .\scripts\Serve-StaticFeed.ps1 `
    -Root .\artifacts\devolutions-feed\site `
    -CertificatePath .\artifacts\devolutions-feed\pki\tls-server.pfx `
    -CertificatePasswordEnvironmentVariable STATIC_FEED_TLS_PASSWORD `
    -BindAddress 127.0.0.1 `
    -Port 8443
```

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
the local exact-byte comparison and independent SQLite/MSZIP checks.
Hardened signed outputs from both publishers also passed stock WinGet
**1.11.510** (App Installer **1.26.510.0**) on a disposable GitHub-hosted
Windows runner. That gate covers source add/update, search/show, multi-block
version data, hash-verified download, version bumps, package additions/removals
and cached-index metadata retention. It generates short-lived signing and TLS
credentials under runner temporary storage, then verifies temporary source and
certificate cleanup. No Sandbox or developer-host trust changes are needed.

Failure tests cover packaging/signing errors, update promotion rollback and
destination locking. The .NET publisher also has an end-to-end regression
for initial publication failure after metadata copying: a controlled MSIX
identity mismatch leaves the destination empty and no publication recorded,
then a valid retry succeeds. The Rust regression uses a private deterministic
post-copy callback to verify the copied artifact hashes, inject a failure,
assert complete cleanup and unchanged publication state, then verify a normal
retry. Rust still verifies the staged MSIX before copying metadata; the
public API adds no failure-injection option.

Stock-client acceptance and signature trust checks belong in a fresh Windows
Sandbox or an ephemeral Windows CI runner. They must not register test sources
or trust certificates in a developer's usual profile.

This feature does not define a cross-platform manifest format, macOS package
installation, managed script bundles, assignment policy, a REST source,
background enforcement or new PowerShell cmdlets.

Manifest ingestion accepts an explicit set of `ManifestVersion` values (`1.0.0`
through `1.12.0`, `1.28.0` and `1.31.0`) and checks required fields, document
roles and merged structure. These checks are not a replacement for full upstream
JSON-schema validation of every manifest property. Search normalization reuses
Pinget's existing normalizers; compatibility evidence covers the tested
fixtures, not every possible name or publisher string.

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
