# Homebrew static source prototype

The publisher can create a Homebrew-profile static feed, and the native
Rust/.NET clients can register its dedicated source type and attempt to verify
and consume `brew.msix`. A successful signed-feed acceptance and bottle
installation have not yet been demonstrated; this remains a partial prototype,
not an end-to-end install procedure.

## Build the artifacts

Prepare pinned formula JSON and the exact bottle archive outside the output
directories. The current importer accepts only a dependency-free
`arm64_sonoma` formula and performs no network access:

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
MSIX digest, signer, and trust verification. Supply the expected certificate
subject and explicit trust anchors. For the CLI:

```sh
pinget source add --name homebrew --argument https://packages.example.com \
  --type Pinget.PreIndexed.Homebrew \
  --publisher 'CN=Example Source Publisher' \
  --trusted-ca /path/to/test-root.cer
pinget source update --name homebrew
pinget search jq --source homebrew
```

In PowerShell, use `Add-PingetSource` with the same `-Type`, `-Publisher`, and
one or more `-TrustedCa` values. Do not register the Homebrew feed as
`Microsoft.PreIndexed.Package`: that source type fetches `source2.msix` /
`source.msix` and does not enforce the Homebrew-specific verification boundary.

The client activates its cache only after verification and schema validation,
and retains the previous cache when an update fails. However, the available
tests currently cover failure paths only: no successful disposable-PKI
signature/trust run has proved this boundary end to end. Native bottle
download/install/list/upgrade/uninstall is not implemented.

The importer is also an offline single-formula prototype: it rejects runtime
dependencies and does not inspect receipts or resolve target-specific bottle
variants. See [bottle compatibility](homebrew-bottle-compatibility.md) for
the exact support matrix and outstanding acceptance requirements.
