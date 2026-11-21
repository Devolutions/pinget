# Offline Homebrew formula import

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

Inputs must be pinned local files supplied by the operator. **No API calls,
downloads, DNS, authentication, or other network behavior is implemented.**
The importer accepts one formula object, not a bulk API list or cask.
Supported triples are `arm64_sonoma` / `macOS` / `arm64`, `sonoma` / `macOS` /
`x64`, `x86_64_linux` / `Linux` / `x64`, and `aarch64_linux` / `Linux` / `arm64`.
Sonoma implies macOS product version 14.0. Linux inputs must augment the
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
Only `any` and `any_skip_relocation` bottles are accepted; no relocation or
installation is performed. The archive is treated as opaque bytes, not
extracted, inspected, or repacked.

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
Disabled/deprecated formulas, fixed-cellar bottles, unsupported version schemes
and targets are rejected. Formula aliases are sorted/deduplicated in metadata;
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
a source or authenticate Homebrew metadata. SHA-256 verifies consistency with the operator's pinned JSON,
not the authenticity of that JSON.

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
