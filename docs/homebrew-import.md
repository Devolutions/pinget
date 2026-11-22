# Homebrew formula import

Both CLIs provide the same bounded `repository import-homebrew` command:

```sh
pinget repository import-homebrew \
  --formula-json inputs/formula.json \
  --bottle-archive inputs/formula.bottle.tar.gz \
  --manifest-output output/manifests \
  --bottle-mirror output/bottles \
  --tag arm64_sonoma --target-os macOS --architecture arm64 \
  --base-url https://packages.example.invalid/bottles
```

Inputs must be local files supplied by the operator. The offline importer makes
no network requests and accepts one formula object, not a bulk API list or
cask.
Supported triples are `arm64_sonoma` / `macOS` / `arm64`, `sonoma` / `macOS` /
`x64`, `arm64_sequoia` / `macOS` / `arm64`, `sequoia` / `macOS` / `x64`,
`x86_64_linux` / `Linux` / `x64`, and `aarch64_linux` / `Linux` / `arm64`.
Sonoma implies macOS product version 14.0; Sequoia implies 15.0. Linux inputs must augment the
selected bottle file with `minimum_glibc` and `loader: "glibc"` derived from
pinned OCI metadata; those are curated Pinget input fields, not claimed
formula API fields.

The importer validates name/tap/full-name identity, stable version, unsigned
revision/version-scheme/rebuild, aliases, keg-only state, selected bottle
metadata, and the archive SHA-256 against the selected formula bottle.
It reuses the shared collision-resistant Homebrew package-ID helper.
`PackageVersion` is `<pkg_version>.bottle.<rebuild>`, distinct from the archive's
`<formula>/<pkg_version>/` root. Version scheme zero is currently required.
Formula `desc`, `homepage`, and SPDX `license` populate `ShortDescription`,
`PackageUrl`, and `License` when present. Because the formula API does not
provide an upstream author and the manifest publisher field is required by the
source builder, `Publisher` is the literal `Unknown`; the tap is never treated
as an upstream publisher or author.
`any` and `any_skip_relocation` are preserved as distinct metadata values.
Fixed-cellar bottles are accepted only when the cellar exactly matches the
selected native tag (`/opt/homebrew/Cellar` for macOS arm64,
`/usr/local/Cellar` for macOS x64, or `/home/linuxbrew/.linuxbrew/Cellar`
for Linux). The client relocates those known source prefixes to its private
prefix using the same bounded ELF, Mach-O, and text relocation as other
bottles. Unknown or cross-target fixed cellars are rejected. Relocation is
not a general binary rewriting facility; see
[`homebrew-bottle-compatibility.md`](homebrew-bottle-compatibility.md).
The importer verifies and mirrors bottle bytes unchanged; it does not extract
or repack them.

Runtime `dependencies` names map to exact derived Pinget IDs. Import every
member of the curated closure before publishing. Optional
`pinget_runtime_dependencies` input rows contain `full_name`, `pkg_version`,
and `bottle_rebuild`, copied from pinned OCI `sh.brew.tab` runtime metadata;
they establish matching catalog `MinimumVersion` values. When supplied, they
must cover every dependency. The client resolves those IDs from the static
source and installs dependencies before the dependent.

Nonempty recommended, optional, macOS-provided dependency lists, requirements,
conflicts, and target-specific variations are rejected rather than discarded. Build/test
dependency lists do not describe the runtime bottle and are not imported.
Disabled/deprecated formulas, unsupported fixed-cellar paths, unsupported
version schemes and targets are rejected. Formula aliases are sorted/deduplicated in metadata;
no alias is automatically selected as a catalog moniker.

Inputs must be outside both output directories; the manifest and mirror
directories must not overlap. Paths containing symlinks/reparse points or `..`
are rejected. Use trusted, nonconcurrently modified input/output directories.
The static base URL, optional homepage, and upstream selected bottle URL must be HTTPS without
userinfo, query strings, fragments, escapes, or dot segments. This intentionally
excludes authenticated/signed URLs; never put credentials in command arguments.
The upstream URL is validated but is not copied into the output or report.

Outputs:

- Mirror: `<lowercase-sha256>.tar.gz`, exactly the original bottle bytes.
- Manifest: `<package-id>.<pkg-version>.yaml`, a `homebrew-bottle` singleton
  with the [Pinget.Homebrew v1 extension](homebrew-manifest-mapping.md).
- Report beside the manifest: `<manifest-name>.import.json`, containing formula
  JSON hash, verified bottle hash, package identity/version, selected tag,
  relative output names, and mapped runtime dependency identities/minima.
  No timestamps, local paths, raw input JSON, or upstream URLs are recorded.

Identical inputs/options give deterministic output. Existing identical files
are reused; conflicting output bytes fail without overwriting existing files.
All validation and conflict checks precede output creation. Files are created
exclusively, but the three output writes are not a transactional publication;
an interrupted write may require a clean output directory. This does not publish
a source or authenticate Homebrew metadata. SHA-256 verifies consistency with
the supplied JSON, not the authenticity of that JSON.

## Automated `homebrew/core` catalog import

For a bounded dependency closure, use the cross-runtime catalog driver:

```powershell
./scripts/Import-HomebrewCatalog.ps1 `
  -Formula jq,ripgrep `
  -Tag x86_64_linux `
  -Runtime rust -RustCli rust/target/release/pinget `
  -ManifestOutput out/manifests -BottleMirror out/bottles `
  -BaseUrl https://packages.example.invalid/bottles `
  -Report out/catalog-import.json
```

Run the script in PowerShell 7.4 or later on Linux, macOS, or Windows.
For .NET use `-Runtime dotnet -DotnetAssembly <path-to-pinget.dll>`.
`-FormulaList` accepts one formula name per line; `-Formula` accepts an array
of additional roots. The tool fetches the public `formulae.brew.sh` formula API,
walks exact runtime dependencies (maximum 256 formulas and depth 64), selects
one native tag, downloads only the matching public GHCR bottle blob, verifies
its SHA-256 (2 GiB total bottle-download budget), adds dependency version pins,
and invokes the selected offline importer. The driver translates Homebrew's
`arm64_linux` and symbolic `:any` / `:any_skip_relocation` spellings to the
Pinget input format. For Linux it reads the matching GHCR OCI index entry by
version, target and bottle digest and records the index hash and its glibc
minimum. A missing or ambiguous match is rejected. No Ruby runs and no Homebrew
install command is used. It is limited to `homebrew/core`; tap crawling and
casks are unsupported.

Formulae the offline importer rejects are reported as skipped; a requested
root that cannot be imported makes the command fail. The JSON report records
formula API, Linux OCI index and bottle hashes for imported entries. The API
and OCI index are fetched live and are not authenticated by a pinned signature,
so retain the report and mirror outputs as an operator
review/audit artifact and review generated manifests before publishing. This
driver is not a trusted unattended catalog sync service: it does not pin an
API commit, approve formula changes, or publish/sign the resulting source.
If a later formula fails, outputs from earlier imports remain in the output
directories; inspect the report and use a clean staging directory before a
retry rather than assuming the multi-formula operation rolled back.

For a publisher snapshot, keep the generated `manifests/` and `bottles/`
directories as siblings under a publisher input root (the example's `output/`
already has this shape) and run `repository build --input output`. The
publisher recognizes only flat, lowercase SHA-256-named `.tar.gz` files in
`bottles/`, hashes them, requires every Homebrew installer URL/hash to resolve
to a mirrored asset, and includes the unchanged bytes in the immutable
snapshot and published destination. The source profile selects `brew.msix`;
name both the unsigned package output and the signed input `brew.msix`.
Publishing still uses the external `psign-tool` flow documented in
`repository-publishing.md`. This creates publisher artifacts for the
`Pinget.PreIndexed.Homebrew` source type. The client update path verifies
`brew.msix` before cache activation. Both CLIs have installed and executed a
real jq/oniguruma chain from signed static feeds on Linux x64 and macOS
x64/arm64. Windows has been tested for publishing and signature verification,
not Homebrew source consumption.

Curated OCI adaptations must be labelled as such: retain pinned OCI digests,
upstream source references, and the provenance of ABI/dependency fields.
OCI does not publish a cellar classification; do not present an operator's
verified relocation policy as an authentic historical formula API snapshot.

`tests/homebrew/` contains a synthetic dependency-free pinned formula and a
base64-encoded empty tar.gz bottle used by both implementations' offline tests;
it is not an upstream package or a dependency-closure demonstration.
