# Devolutions.Pinget.Tool

`Devolutions.Pinget.Tool` installs the C# Pinget CLI as a framework-dependent .NET tool.

## Install globally

```powershell
dotnet tool install -g Devolutions.Pinget.Tool
```

## Install in a local tool manifest

```powershell
dotnet new tool-manifest
dotnet tool install Devolutions.Pinget.Tool
dotnet tool run pinget -- --help
```

## Run

```powershell
pinget --help
pinget --info
```

This package uses the C# Pinget implementation and requires a compatible .NET runtime on the target machine. It is separate from the `Devolutions.Pinget.Cli.Rust` and `Devolutions.Pinget.Cli.DotNet` packages, which are build-time `PackageReference` packages that copy prebuilt executables into another project's output.

## Install an AgentSkill manifest

Pinget supports a single-document YAML extension for installing an agent skill,
without invoking a separate skills CLI. Existing manifests with `ArtifactUrl`
and `ArtifactSha256` still install HTTPS ZIP artifacts:

```yaml
ManifestType: pinget.agentSkill
PingetManifestVersion: 1
PackageIdentifier: Example.SampleSkill
PackageVersion: 1.0.0
PackageName: sample-skill
AgentSkill:
  ArtifactUrl: https://example.org/sample-skill.zip
  ArtifactSha256: 0000000000000000000000000000000000000000000000000000000000000000
  SkillPath: skills/source-folder
```

For new manifests, use **either** the legacy fields above **or** `Source`, never
both. A pinned Git skill (including a skills.sh-hosted HTTPS Git repository) is:

```yaml
ManifestType: pinget.agentSkill
PingetManifestVersion: 1
PackageIdentifier: Example.SampleSkill
PackageVersion: 1.0.0
PackageName: sample-skill
AgentSkill:
  Source:
    Type: git
    Url: https://github.com/example/skills.git
    Commit: 0123456789abcdef0123456789abcdef01234567
  SkillPath: skills/sample-skill
```

Use `git rev-parse HEAD` in the source repository to get an actual 40-hex-digit
commit before running the install command below. Git must be installed on
`PATH`. Omitting `SkillPath` is supported only when `SKILL.md` is at the pinned
repository root; in that case, the repository root and its companion files are
selected. Git is fetched over HTTPS, the exact commit is verified, and only
the selected skill subtree (and plugin manifest, when specified) is inspected
and copied; no source script or hook is run.

Other source mappings:

```yaml
# An HTTPS archive (Format: zip, tar.gz, or skill-md)
Source: { Type: archive, Url: "https://example.org/skill.tar.gz", Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", Format: tar.gz }

# Notion Agent Skills API (NOTION_API_KEY is read from the runtime environment)
Source: { Type: notion-skill, Id: "123e4567-e89b-12d3-a456-426614174000", VersionId: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }

# Agent Skills discovery index v0.2.0: the SHA-256 pins the selected skill
Source: { Type: well-known, IndexUrl: "https://example.org/.well-known/skills/index.json", Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
```

These snippets replace `AgentSkill.Source` in the complete Git manifest above;
replace the illustrative URLs, UUID, version IDs, hashes, and commit with real
values. The discovery index must use its v0.2.0 `$schema` and a `skills` array;
Pinget selects the entry whose `name` matches `PackageName`, resolves its `url`
against `IndexUrl` (including relative URLs), requires HTTPS, and verifies its
`sha256:` digest against the manifest pin before downloading.

For an Agent Plugins v1.0.0 layout, add `PluginPath: .` when `plugin.json`
is at the artifact root (or a relative directory if nested), and set
`SkillPath: skills/sample-skill` relative to that plugin root; a `PluginPath`
without an explicit `SkillPath` is rejected. Pinget validates
`plugin.json`'s `$schema` and requires a reverse-DNS-style `name` as an
additional Pinget constraint beyond the Agent Plugins schema. Unknown
top-level fields produce a warning and are ignored. Pinget extracts *only*
the selected skill.
`Source: { Type: notion-plugin, Id: "<UUID>", VersionId: "<64-character version>" }`
requires both `PluginPath` and `SkillPath`. Notion endpoints are
`/v1/ai/skills/{id}` and `/v1/ai/plugins/{id}`, with `Notion-Version: 2026-03-11`;
the signed HTTPS tar.gz URL is temporary and is never written to provenance.
Set `NOTION_API_KEY` in the environment before running Pinget; do not include
it in a manifest.

To compute the archive pin on Windows, run:

```powershell
(Get-FileHash .\skill.tar.gz -Algorithm SHA256).Hash
pinget install --manifest .\sample-skill.yaml --agent github-copilot
pinget install --manifest .\sample-skill.yaml --agent claude-code --scope user
```

Replace the URL and SHA-256 with the actual HTTPS artifact and its digest.
`PackageName` must be a valid 1–64-character lowercase skill slug and match
the `name` in `SKILL.md` frontmatter. `Publisher` is optional. `SkillPath`
is optional and defaults to the archive root for archives; when supplied, it
selects a directory within an archive but need not match the skill name (except
for plugins, where it must be `skills/<PackageName>`). The selected
directory must contain `SKILL.md` with a nonempty frontmatter `description`.

Agent selection is required: `github-copilot`, `claude-code`, or `codex`. By
default, Pinget installs under the current project's `.agents/skills` for
Copilot/Codex or `.claude/skills` for Claude Code. `--scope user` installs under
`~/.copilot/skills`, `~/.claude/skills`, or `~/.codex/skills`, respectively.
Pinget records the manifest identity, source type and pin (legacy artifact URL/
digest for legacy manifests), selected agent, and
destination under `<project-or-home>/.pinget/agent-skills/<agent>/<skill-name>.json`.
The `artifactSha256` field is calculated over downloaded bytes (including
Notion's signed tar.gz) or, for Git, the selected files' paths and bytes in a
stable order; the Git value is not a repository commit hash.
Provenance strips URL queries so signed URLs and tokens are not retained.
Unmanaged skill directories and provenance without a recorded installed-tree
SHA-256 are never replaced. For managed skills, the installed file-tree digest
must still match its provenance: an unchanged version is a no-op, a lower
version is rejected, and a newer version is staged and swapped with rollback
if replacing the skill or provenance fails. Preserve your edits elsewhere
before upgrading; Pinget will not overwrite a locally changed skill.
The digest is compatible with the Rust CLI: sort regular files lexicographically
by the UTF-8 bytes of their relative `/`-separated paths, then hash each path
and file content, preceded by their respective unsigned 64-bit little-endian
byte lengths.
Empty directories do not affect it; hexadecimal letter case is ignored when
checking existing provenance.
Catalog installs also record the catalog source name and verified manifest
SHA-256; a managed catalog skill cannot silently switch catalog sources.

## Install and update from a Pinget AgentSkills catalog

A catalog served over HTTPS can pin AgentSkill manifests by ID and version:

```json
{
  "schemaVersion": 1,
  "packages": [
    {
      "packageIdentifier": "Example.SampleSkill",
      "packageName": "sample-skill",
      "packageVersion": "1.0.0",
      "manifestUrl": "sample-skill.yaml",
      "manifestSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ]
}
```

`packageName` is a required lowercase skill slug and must match the pinned
manifest's `PackageName`. The `manifestUrl` may be an absolute HTTPS URL or a
safe path relative to the catalog index URL (for example, `sample-skill.yaml`
resolves next to `catalog.json`). Queries, fragments, credentials and traversal
are rejected. The hash is the SHA-256 of the raw YAML manifest bytes, not the
artifact. Use the manifest's own artifact pin for the underlying skill. For
example:

```powershell
pinget source add --name skills --type Pinget.AgentSkills --arg https://catalog.example/catalog.json
pinget source update skills
pinget search --agent github-copilot --query sample-skill
pinget search --agent github-copilot --id Example.SampleSkill --source skills
pinget install --agent github-copilot --id Example.SampleSkill
pinget upgrade --agent github-copilot --id Example.SampleSkill
```

Replace the example addresses and digest with a real HTTPS catalog, manifest
and SHA-256. Install selects the newest catalog version unless `--version`
selects one explicitly; upgrade requires an existing managed installation.
The first search/install fetches and caches the validated catalog if no
snapshot exists. Subsequent search/install/upgrade commands use that snapshot;
only `pinget source update skills` refreshes the catalog pin. An invalid
refresh leaves the previous snapshot intact. `source reset --name skills`
clears its snapshot and causes the next read to fetch again. The catalog
snapshot is stored at `<appRoot>/agent-skill-catalogs/<SHA-256 of the exact
UTF-8 source name>.json`, alongside the separate
`<appRoot>/agent-skill-sources.json` store; these files can be shared with the
Rust CLI for catalogs containing absolute HTTPS or safe relative manifest URLs.
The catalog must have unique ID/version entries (up to 1 MiB / 1,000 entries).
Re-pinning an existing ID/version on update is rejected: publish a new version
instead.
Unknown catalog fields are rejected. Pinget verifies its bounds and checks
the manifest digest and exact ID, name and version before installing.
`--source skills` is optional when only one matching non-explicit catalog has
the requested ID (or ID and `--version`); if several catalogs match, specify
`--source` to disambiguate. Explicit catalogs require `--source`.
`search --agent` matches catalog package identifiers and names, returning
the newest version per ID and source without downloading manifests or
artifacts. It supports `--query`, `--id`, `--name`, `--exact`, `--count`, and
`--source`. Ordinary WinGet application `search` and `show`
remain separate and never search AgentSkills catalogs.
Catalog snapshots contain only validated metadata; skill artifacts and YAML
manifests are not cached. Explicit update is required to see newer versions.
`--id` lookup is case-insensitive, including across multiple catalogs; the
pinned manifest's package identifier may differ only in letter case. Catalog
package versions and skill names must match the pinned manifest exactly.
Catalogs are Pinget-only sources stored separately from WinGet's sources, even
in system WinGet source mode. `winget.exe` does not understand or manage them.
Catalog and manifest URLs cannot contain query strings (which might carry
tokens); cached indexes never store opaque unknown fields.
The catalog is limited to 1 MiB, manifests to 1 MiB, and HTTPS redirects
cannot downgrade to HTTP. Skill uninstall and catalog manifest preview are not supported.
A Git URL alone is not skills.sh catalog integration.

WinGet application manifests retain their existing install behavior. The Agent Plugins specification defines
the plugin layout, not distribution: Pinget selects a skill from a supplied
artifact, but does not install a plugin, its commands, agents, or MCP features.
Archives are limited to 10 MiB downloaded, with at most 25 MiB unpacked
(25 MiB per entry), 1,000 entries, and a 1 MiB `SKILL.md`.
Archive pins (except Notion's verified opaque version ID) require a matching
SHA-256; paths, links, and case collisions are rejected;
`--ignore-security-hash` cannot bypass these checks. Installer-specific switches
such as `--force`, `--no-upgrade`, and `--dependencies-only` are rejected.
