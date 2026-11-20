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
Only `arm64_sonoma` / `macOS` / `arm64` is supported in this first slice.
The bottle tag implies macOS 14.0 as the minimum product version.

The importer validates name/tap/full-name identity, stable version, unsigned
revision/version-scheme/rebuild, aliases, keg-only state, selected bottle
metadata, and the archive SHA-256 against the selected formula bottle.
It reuses the shared collision-resistant Homebrew package-ID helper.
`PackageVersion` includes a nonzero formula revision, not the bottle rebuild.
Formula `desc`, `homepage`, and SPDX `license` populate `ShortDescription`,
`PackageUrl`, and `License` when present. Because the formula API does not
provide an upstream author and the manifest publisher field is required by the
source builder, `Publisher` is the literal `Unknown`; the tap is never treated
as an upstream publisher or author.
Only `any` and `any_skip_relocation` bottles are accepted; no relocation or
installation is performed. The archive is treated as opaque bytes, not
extracted, inspected, or repacked.

Dependency closure is **not implemented**. Nonempty runtime, recommended,
optional, macOS-provided dependency lists, requirements, conflicts, and
target-specific variations are rejected rather than discarded. Build/test
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
  relative output names, and the explicit dependency rejection policy.
  No timestamps, local paths, raw input JSON, or upstream URLs are recorded.

Identical inputs/options give deterministic output. Existing identical files
are reused; conflicting output bytes fail without overwriting existing files.
All validation and conflict checks precede output creation. Files are created
exclusively, but the three output writes are not a transactional publication;
an interrupted write may require a clean output directory. This does not publish
a source, verify Homebrew signatures, implement upgrades, or enable bottle
installation. SHA-256 verifies consistency with the operator's pinned JSON,
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
`brew.msix` before cache activation, but successful signed-feed acceptance and
bottle installation are not yet demonstrated.

`tests/homebrew/` contains a synthetic dependency-free pinned formula and a
base64-encoded empty tar.gz bottle used by both implementations' offline tests;
it is not an upstream package or a dependency-closure demonstration.
