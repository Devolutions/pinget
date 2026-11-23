# Homebrew static source prototype

Both publishers create a fully static Homebrew-profile feed. Native clients
verify its signed `brew.msix`, activate the enclosed preindexed database, then
download manifests and unchanged bottle bytes from the static host. No REST
server, Homebrew, or Ruby runs on clients. Real jq/oniguruma installation has
been exercised through both CLIs on Linux x64/arm64 and macOS x64/arm64; see the
[compatibility limits](homebrew-bottle-compatibility.md).

## Build the artifacts

Prepare pinned formula JSON and the exact bottle archive outside the output
directories. Import each member of a pinned runtime dependency closure; the
importer performs no network access:

```sh
pinget repository import-homebrew \
  --formula-json inputs/formula.json \
  --bottle-archive inputs/formula.bottle.tar.gz \
  --manifest-output output/manifests \
  --bottle-mirror output/bottles \
  --tag arm64_sonoma --target-os macOS --architecture arm64 \
  --base-url https://packages.example.com/bottles
```

Keep `manifests/` and `bottles/` as siblings under the publisher input root.
For Linux x64 use `--tag x86_64_linux --target-os Linux --architecture x64`
with explicit OCI-derived ABI metadata. Import jq and oniguruma into the same
input root. Imports are single-target; use separate target feed directories,
or explicitly author a combined manifest with matching installer/bottle entries.
Keep publisher state, staging output, and the published destination in
separate directories. Then build and package a Homebrew-only snapshot:

```sh
pinget repository build --input output --state publisher-state
pinget repository verify --state publisher-state
pinget repository package --state publisher-state --destination public \
  --name Pinget.HomebrewSource --publisher 'CN=Example Source Publisher' \
  --version 1.0.0.0 --msix staging/brew.msix
```

Sign the staged file externally with `psign-tool`, then publish it using the
same `brew.msix` filename:

```sh
# Use the appropriate configured signing backend; keep secrets out of the repo.
psign-tool @secrets/sign.rsp
pinget repository publish --state publisher-state --destination public \
  --msix staging/brew.msix
pinget repository verify --destination public
```

Use the signing backend and private trust configuration appropriate for the
environment; do not put credentials in manifests, reports, or hosted files.
The existing publisher can use repeatable `--trusted-ca` arguments when
publishing or verifying a private test chain. It does not install roots into
the host trust store. The full signing options and static-host ordering rules
are in [repository publishing](repository-publishing.md).

Host the entire published directory over ordinary static HTTPS. Publish
hash-addressed manifests, version sidecars, and `bottles/<sha256>.tar.gz`
before replacing `brew.msix`. Preserve those immutable paths and permit
`GET`/`HEAD`. Bottle bytes are mirrored unchanged.

## Registering the Homebrew source

Use the dedicated source type on native macOS/Linux clients; it requests only
`brew.msix`, rejects redirects, and requires an external `psign-tool` for
MSIX digest, signer, and trust verification. `--trusted-ca` provides explicit
MSIX signing trust and a source-scoped TLS root fallback; it never disables
hostname or certificate-chain validation. For the CLI:

```sh
pinget source add --name homebrew --arg https://packages.example.com \
  --type Pinget.PreIndexed.Homebrew \
  --publisher 'CN=Example Source Publisher' \
  --trusted-ca /path/to/test-root.cer
pinget source update homebrew
pinget search jq --source homebrew
pinget install jq --source homebrew --silent --accept-package-agreements
pinget list
pinget upgrade jq --source homebrew
pinget repair --id Pinget.Homebrew.jq-929958fef124d633 --source homebrew
pinget uninstall jq
pinget uninstall oniguruma
```

## Reproducing the real-bottle gate

`tests/homebrew/Run-NativeAcceptance.ps1` pins jq/oniguruma OCI indexes,
authenticates metadata and bottle digests, creates a disposable signing
identity, and checks both publisher/consumer directions against a loopback
static HTTPS host using source-scoped trust configuration. Linux/macOS
additionally check native execution, rebuild-only upgrade, cross-runtime
repair, dependency-protected uninstall, and tampered
source cache preservation. The rebuild transition is synthetic; bottle bytes
are unchanged. Windows checks Rust/.NET publishing and signing; native
Homebrew source clients are disabled on Windows.

The `Homebrew prototype acceptance` workflow runs these gates on Linux and
macOS x64/arm64, and Windows. It uploads non-secret acceptance reports and
generated MSZIP test fixtures and results.
All orchestration uses portable PowerShell 7.4 or later, without an additional
scripting runtime or test-module dependency. Run
`pwsh -NoLogo -NoProfile -File tests/homebrew/Test-CatalogImport.ps1` and
`pwsh -NoLogo -NoProfile -File tests/publisher/Test-MszipInterop.ps1` for the
offline driver tests. `tests/publisher/mszip-interop.ps1 -Mode produce` creates
the four-platform fixtures; `-Mode validate-windows` checks them with the
Windows native Compression API, not the portable decoder.
`workflow_call` is the reusable entry point. The prototype-branch push trigger
is scoped to this work branch and can be removed after platform acceptance.
An approval-blocked workflow is not evidence of platform success.

Local Linux runs require built CLIs, Psign, `patchelf`, OpenSSL, PowerShell 7.4 or later, and
an explicit private temporary parent:

```powershell
pwsh -NoLogo -NoProfile -File tests/homebrew/Run-NativeAcceptance.ps1 `
  -RustCli rust/target/debug/pinget `
  -DotnetAssembly dotnet/src/Devolutions.Pinget.Cli/bin/Release/net10.0/pinget.dll `
  -PsignTool /absolute/path/to/psign-tool `
  -WorkRoot /absolute/private/temporary/parent `
  -Report /absolute/path/to/report.json
```

The disposable certificates come from a self-contained OpenSSL configuration
with subject/authority key identifiers and no duplicate extensions; macOS
rejects a root that is not standards compliant, and the gate checks the server
chain with `security verify-cert` before starting. On Windows, the `dotnet tool`
shim is `psign-tool.cmd`, so the workflow passes the packaged native
`psign-tool.exe` to the gate. Its loopback TLS host reimports the server key
using `UserKeySet` for Schannel compatibility; the key is deleted on certificate
disposal, without installing certificates in a host trust store.

The gate supplies its disposable root explicitly for both MSIX signature and
source-scoped TLS trust; it never modifies host trust stores. Temporary keys,
packages, client state, and the loopback server are cleaned up; no private key
is an artifact or a repository fixture.

In PowerShell, use `Add-PingetSource` with the same `-Type`, `-Publisher`, and
one or more `-TrustedCa` values. Do not register the Homebrew feed as
`Microsoft.PreIndexed.Package`: that source type fetches `source2.msix` /
`source.msix` and does not enforce the Homebrew-specific verification boundary.

The client activates its cache only after signature/trust/signer identity and
schema validation, retaining the previous cache when an update fails.
Successful signing, publication, client verification, native dependency
installation, execution, upgrade, repair, uninstall, and tamper rejection have
been demonstrated on hosted Linux x64/arm64 and macOS x64/arm64 runners;
Windows runners demonstrated Rust/.NET publishing and signature verification.
Use an increasing four-part MSIX package version for each changed publication.

The default private prefix is `<AppRoot>/homebrew`; add its `bin` directory to
your own command environment if desired. Do not point it at an existing
Homebrew installation. ELF relocation needs `patchelf`; macOS relocation uses
the native developer-tool binaries and ad-hoc signing. Psign verifies the source
on clients as well as publishers. Its portable PFX signing backend does not use
the separate `--rust-sip` option.

Receipts and links are shared between Rust and .NET. Upgrade/repair replace the
managed keg, and uninstall honors the managed dependency graph. The prototype
fails closed on unsupported archives, ABI constraints, and unowned/modified
paths. Acceptance covers the pinned jq/oniguruma bottles only; it does not
imply general Homebrew compatibility.
