# Homebrew bottle compatibility

This document records the exact boundary of the current Pinget Homebrew
prototype. It is not a claim of general Homebrew compatibility. The current
code imports and publishes a narrow manifest/archive slice and implements a
dedicated `brew.msix` source update path; successful signed-source acceptance
and bottle installation have not been demonstrated.

## Current support matrix

| Area | Supported now | Explicitly not supported |
| --- | --- | --- |
| Formula input | One pinned local formula JSON object | API fetching, bulk import, casks, Ruby evaluation |
| Bottle target | `arm64_sonoma` / macOS / arm64; minimum product version 14.0 | Other tags, Linux, x64, platform-independent targets |
| Formula metadata | Identity, stable version, revision, version scheme, aliases, old names, keg-only, description, homepage, SPDX license | Runtime dependency closure, `uses_from_macos`, requirements, conflicts, target variations |
| Bottle classes | `any` and `any_skip_relocation` are preserved as distinct metadata values | Fixed-cellar bottles |
| Archive handling | Verify the selected archive SHA-256 and mirror its bytes unchanged under a content-addressed name | Extraction, inspection of `INSTALL_RECEIPT.json`, relocation, execution |
| Static publishing | Homebrew-only immutable snapshots, mirrored bottle files, `brew.msix`, external signing and publisher verification | Native Windows/macOS signing and publishing acceptance |
| Source update | Dedicated source type fetches `brew.msix` only, disables redirects, verifies signature/trust/identity, validates index schema before cache activation | Successful real signed-feed acceptance; native bottle lifecycle |
| Client operations | Manifest parsing and catalog metadata support | Native download/install/list/upgrade/uninstall, receipts, dependency ordering, rollback |

The tag mappings in the manifest contract are validation/mapping knowledge,
not evidence that the publisher accepts every tag or that a native client can
install for it. In particular, the current offline importer is deliberately
restricted to one dependency-free macOS arm64 target. It does not retrieve
historical bottles or demonstrate a real package.

## Import and publication checks

The importer requires the operator to provide the exact formula JSON and
archive. It verifies the archive digest against the selected bottle metadata,
rejects unsupported dependency/requirement fields, and writes deterministic
manifests plus an audit report. It does not authenticate the provenance of the
pinned formula JSON; the hash establishes consistency between those inputs,
not upstream authenticity.

The publisher requires each Homebrew installer URL and hash to identify one
flat, lowercase SHA-256-named `.tar.gz` file in the sibling `bottles/` input
directory. It rechecks the digest, includes the unchanged bytes in the
immutable snapshot and published destination, and selects `brew.msix` for the
Homebrew-only profile. Mixed Homebrew/Windows snapshots are rejected. The
source update path requires `psign-tool` and explicit publisher/trust
configuration. Its failure-path tests do not establish successful
cryptographic acceptance.

No client has unpacked a bottle in this prototype, so archive path traversal,
link escape, special-file, relocation, loader, permissions, and installed-file
ownership behavior have not been implemented or acceptance-tested. Do not
install these manifests as if `homebrew-bottle` were a supported installer.

## Required acceptance before claiming support

The remaining compatibility work includes API-backed import, exact bottle
dependency resolution and receipt inspection, a real formula dependency chain,
and a signed static source that the Pinget client successfully verifies before
activating its cache. The client must then install, run, list, upgrade (including
rebuild-only changes), and uninstall a real package in an isolated prefix.
Those lifecycle operations need bounded archive extraction, target and ABI
preflight, relocation for the curated set, atomic receipts/links, and recovery
tests. Native Windows and macOS publisher/signature interoperability gates are
also outstanding; Linux checks alone do not prove them.
