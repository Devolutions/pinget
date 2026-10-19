use std::cmp::Ordering;
use std::fs::{self, File};
use std::io::{Cursor, Read, Write};
use std::path::{Component, Path, PathBuf};

use anyhow::{Context, Result, bail, ensure};
use serde::Deserialize;
use zip::ZipArchive;

use crate::agent_skill_source::SkillSource;
use crate::{InstallRequest, InstallResult, InstallerMode, Repository, compare_version, sha256_hex};

const MAX_UNPACKED: u64 = 25 * 1024 * 1024;
const MAX_FILES: usize = 1000;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase", deny_unknown_fields)]
struct SkillArtifact {
    artifact_url: Option<String>,
    artifact_sha256: Option<String>,
    #[serde(default)]
    skill_path: String,
    plugin_path: Option<String>,
    source: Option<SkillSource>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct SkillManifest {
    manifest_type: String,
    pinget_manifest_version: u32,
    pub(crate) package_identifier: String,
    pub(crate) package_version: String,
    pub(crate) package_name: String,
    publisher: Option<String>,
    agent_skill: SkillArtifact,
    #[serde(skip)]
    pub(crate) catalog_source: Option<String>,
    #[serde(skip)]
    pub(crate) catalog_manifest_sha256: Option<String>,
}

pub(crate) fn parse(bytes: &[u8]) -> Result<Option<SkillManifest>> {
    let mut documents = serde_yaml::Deserializer::from_slice(bytes);
    let Some(first) = documents.next() else { return Ok(None) };
    let value = serde_yaml::Value::deserialize(first).context("invalid YAML manifest")?;
    if value.get("ManifestType").and_then(serde_yaml::Value::as_str) != Some("pinget.agentSkill") {
        ensure!(
            value.get("AgentSkill").is_none() && value.get("PingetManifestVersion").is_none(),
            "AgentSkill fields require ManifestType: pinget.agentSkill"
        );
        return Ok(None);
    }
    ensure!(
        documents.next().is_none(),
        "Pinget AgentSkill manifests must be a single YAML document"
    );
    if let Some(path) = value.get("AgentSkill").and_then(|skill| skill.get("SkillPath")) {
        ensure!(
            path.as_str().is_some_and(|path| !path.is_empty()),
            "AgentSkill.SkillPath must be a nonempty ZIP-relative directory when provided"
        );
    }
    let manifest: SkillManifest = serde_yaml::from_value(value).context("invalid Pinget AgentSkill manifest")?;
    ensure!(
        manifest.manifest_type == "pinget.agentSkill",
        "unsupported manifest type"
    );
    ensure!(
        manifest.pinget_manifest_version == 1,
        "unsupported PingetManifestVersion"
    );
    ensure!(
        !manifest.package_identifier.trim().is_empty(),
        "PackageIdentifier is required"
    );
    ensure!(
        !manifest.package_version.trim().is_empty(),
        "PackageVersion is required"
    );
    ensure!(
        valid_name(&manifest.package_name),
        "PackageName must be a valid Agent Skills name"
    );
    match &manifest.agent_skill.source {
        Some(source) => {
            ensure!(
                manifest.agent_skill.artifact_url.is_none() && manifest.agent_skill.artifact_sha256.is_none(),
                "AgentSkill.Source cannot be combined with legacy ArtifactUrl or ArtifactSha256"
            );
            source.validate()?;
            ensure!(
                !source.is_notion_plugin() || manifest.agent_skill.plugin_path.is_some(),
                "Notion plugin sources require AgentSkill.PluginPath"
            );
            ensure!(
                !matches!(source, SkillSource::NotionSkill { .. }) || manifest.agent_skill.plugin_path.is_none(),
                "Notion skill directories do not contain an Agent Plugin"
            );
        }
        None => {
            let url = manifest
                .agent_skill
                .artifact_url
                .as_deref()
                .context("AgentSkill.ArtifactUrl is required")?;
            let sha256 = manifest
                .agent_skill
                .artifact_sha256
                .as_deref()
                .context("AgentSkill.ArtifactSha256 is required")?;
            SkillSource::Archive {
                url: url.to_owned(),
                sha256: sha256.to_owned(),
                format: "zip".to_owned(),
            }
            .validate()?;
        }
    }
    safe_path(&manifest.agent_skill.skill_path)?;
    ensure!(
        !manifest.agent_skill.skill_path.ends_with('/'),
        "AgentSkill.SkillPath must be a ZIP-relative directory without a trailing slash"
    );
    if let Some(plugin_path) = manifest.agent_skill.plugin_path.as_deref() {
        ensure!(
            !plugin_path.is_empty(),
            "AgentSkill.PluginPath must be a directory or '.' for the root"
        );
        if plugin_path != "." {
            safe_path(plugin_path)?;
            ensure!(!plugin_path.ends_with('/'), "AgentSkill.PluginPath must not end in '/'");
        }
        ensure!(
            manifest.agent_skill.skill_path == format!("skills/{}", manifest.package_name),
            "Agent Plugin skills must use SkillPath: skills/<PackageName>"
        );
    }
    Ok(Some(manifest))
}

pub(crate) fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 64
        && !name.starts_with('-')
        && !name.ends_with('-')
        && !name.contains("--")
        && name
            .bytes()
            .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
}

pub(crate) fn safe_path(value: &str) -> Result<PathBuf> {
    let path = Path::new(value);
    ensure!(
        path.components()
            .all(|component| matches!(component, Component::Normal(_))),
        "unsafe skill archive path: {value}"
    );
    ensure!(
        !value.contains('\\') && !value.contains("//") && !value.chars().any(char::is_control),
        "unsafe skill archive path: {value}"
    );
    for part in value.split('/').filter(|part| !part.is_empty()) {
        let stem = part.split('.').next().unwrap_or_default();
        let device = matches!(stem.to_ascii_uppercase().as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || (stem.len() == 4
                && (stem.as_bytes()[..3].eq_ignore_ascii_case(b"COM")
                    || stem.as_bytes()[..3].eq_ignore_ascii_case(b"LPT"))
                && matches!(stem.as_bytes()[3], b'1'..=b'9'));
        ensure!(
            !part.ends_with([' ', '.']) && !part.contains(['<', '>', ':', '"', '|', '?', '*']) && !device,
            "unsafe skill archive path: {value}"
        );
    }
    Ok(path.to_path_buf())
}

fn agent_root(agent: &str, global: bool) -> Result<PathBuf> {
    let root = if global {
        dirs::home_dir().context("could not determine the user home directory")?
    } else {
        std::env::current_dir().context("could not determine the project directory")?
    };
    let suffix = match (agent, global) {
        ("github-copilot", false) | ("codex", false) => ".agents/skills",
        ("github-copilot", true) => ".copilot/skills",
        ("codex", true) => ".codex/skills",
        ("claude-code", false) | ("claude-code", true) => ".claude/skills",
        _ => bail!("unsupported skill agent: {agent}"),
    };
    Ok(root.join(suffix))
}

fn validate_skill_md(path: &Path, name: &str) -> Result<()> {
    ensure!(
        fs::metadata(path.join("SKILL.md"))
            .context("skill archive must contain SKILL.md")?
            .len()
            <= 1024 * 1024,
        "SKILL.md exceeds the 1 MiB limit"
    );
    let content = fs::read_to_string(path.join("SKILL.md")).context("skill archive must contain SKILL.md")?;
    let mut lines = content.trim_start_matches('\u{feff}').lines();
    ensure!(lines.next() == Some("---"), "SKILL.md must start with YAML frontmatter");
    let frontmatter: String = lines
        .by_ref()
        .take_while(|line| *line != "---")
        .collect::<Vec<_>>()
        .join("\n");
    ensure!(
        lines.any(|line| !line.trim().is_empty()),
        "SKILL.md must contain instructions after its frontmatter"
    );
    let metadata: serde_yaml::Value = serde_yaml::from_str(&frontmatter).context("invalid SKILL.md frontmatter")?;
    ensure!(
        metadata.get("name").and_then(serde_yaml::Value::as_str) == Some(name),
        "SKILL.md name must match PackageName"
    );
    ensure!(
        metadata
            .get("description")
            .and_then(serde_yaml::Value::as_str)
            .is_some_and(|value| !value.trim().is_empty() && value.len() <= 1024),
        "SKILL.md description must contain 1-1024 characters"
    );
    Ok(())
}

fn unpack_skill(bytes: &[u8], skill_path: &Path, dest: &Path) -> Result<()> {
    let mut archive = ZipArchive::new(Cursor::new(bytes)).context("invalid skill ZIP archive")?;
    ensure!(archive.len() <= MAX_FILES, "skill archive has too many files");
    let mut unpacked = 0u64;
    let mut found = false;
    let mut seen = std::collections::HashSet::new();
    for index in 0..archive.len() {
        let entry = archive.by_index(index)?;
        let name = entry.name();
        ensure!(!name.is_empty(), "skill archive contains an empty path");
        let path = safe_path(name)?;
        ensure!(
            seen.insert(path.to_string_lossy().to_lowercase()),
            "skill archive contains duplicate paths"
        );
        ensure!(
            entry.unix_mode().is_none_or(|mode| {
                let kind = mode & 0o170000;
                kind == 0 || kind == if entry.is_dir() { 0o040000 } else { 0o100000 }
            }),
            "skill archive contains a link or unsupported entry"
        );
        let Ok(relative) = path.strip_prefix(skill_path) else {
            continue;
        };
        if relative.as_os_str().is_empty() {
            continue;
        }
        let available = MAX_UNPACKED - unpacked;
        let expected_size = entry.size();
        ensure!(expected_size <= available, "skill archive exceeds extracted size limit");
        let target = dest.join(relative);
        if entry.is_dir() {
            fs::create_dir_all(&target)?;
        } else {
            found = true;
            fs::create_dir_all(target.parent().context("invalid skill path")?)?;
            let mut file = File::create(&target)?;
            let copied = std::io::copy(&mut entry.take(available + 1), &mut file)?;
            ensure!(
                copied == expected_size,
                "skill archive file size does not match ZIP metadata"
            );
            unpacked += copied;
            file.flush()?;
        }
    }
    ensure!(found, "AgentSkill.SkillPath does not contain a skill");
    Ok(())
}

fn plugin_manifest_path(plugin_path: Option<&str>) -> Option<String> {
    plugin_path.map(|path| {
        if path == "." {
            "plugin.json".to_owned()
        } else {
            format!("{path}/plugin.json")
        }
    })
}

fn selected_path(skill_path: &str, plugin_path: Option<&str>) -> String {
    match plugin_path {
        Some(path) if path != "." => format!("{path}/{skill_path}"),
        _ => skill_path.to_owned(),
    }
}

fn notion_wrapped_paths(
    bytes: &[u8],
    selection: &str,
    plugin_manifest: Option<&str>,
) -> Result<Option<(String, Option<String>)>> {
    let mut archive = ZipArchive::new(Cursor::new(bytes)).context("invalid Notion skill archive")?;
    let selected_file = if selection.is_empty() {
        "SKILL.md".to_owned()
    } else {
        format!("{selection}/SKILL.md")
    };
    if archive.by_name(&selected_file).is_ok() {
        return Ok(None);
    }
    let mut root = None::<String>;
    for name in archive.file_names() {
        let Some((first, _)) = name.split_once('/') else {
            return Ok(None);
        };
        if root.as_deref().is_some_and(|existing| existing != first) {
            return Ok(None);
        }
        root = Some(first.to_owned());
    }
    let Some(root) = root else {
        return Ok(None);
    };
    let wrapped_selection = if selection.is_empty() {
        root.clone()
    } else {
        format!("{root}/{selection}")
    };
    if archive.by_name(&format!("{wrapped_selection}/SKILL.md")).is_err() {
        return Ok(None);
    }
    let wrapped_plugin = plugin_manifest.map(|path| format!("{root}/{path}"));
    if wrapped_plugin
        .as_ref()
        .is_some_and(|path| archive.by_name(path).is_err())
    {
        return Ok(None);
    }
    Ok(Some((wrapped_selection, wrapped_plugin)))
}

fn provenance_url(input: &str) -> Result<String> {
    let mut url = reqwest::Url::parse(input).context("invalid AgentSkill provenance URL")?;
    url.set_query(None);
    url.set_fragment(None);
    Ok(url.to_string())
}

fn validate_plugin(bytes: &[u8], path: &str) -> Result<Vec<String>> {
    let mut archive = ZipArchive::new(Cursor::new(bytes)).context("invalid Agent Plugin ZIP")?;
    let entry = archive
        .by_name(path)
        .context("Agent Plugin requires plugin.json at PluginPath")?;
    ensure!(entry.size() <= 64 * 1024, "Agent Plugin plugin.json exceeds 64 KiB");
    let mut contents = Vec::new();
    entry.take(64 * 1024 + 1).read_to_end(&mut contents)?;
    ensure!(contents.len() <= 64 * 1024, "Agent Plugin plugin.json exceeds 64 KiB");
    let value: serde_json::Value = serde_json::from_slice(&contents).context("invalid Agent Plugin plugin.json")?;
    let manifest = value
        .as_object()
        .context("Agent Plugin plugin.json must be a JSON object")?;
    ensure!(
        manifest.get("$schema").and_then(serde_json::Value::as_str)
            == Some("https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"),
        "unsupported Agent Plugin schema"
    );
    let name = manifest
        .get("name")
        .and_then(serde_json::Value::as_str)
        .context("Agent Plugin name is required")?;
    ensure!(
        name.len() <= 64
            && name
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-' || byte == b'.')
            && name
                .bytes()
                .next()
                .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
            && name
                .bytes()
                .last()
                .is_some_and(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit())
            && !name.contains("--")
            && !name.contains(".."),
        "Agent Plugin name is invalid"
    );
    let unknown: Vec<_> = manifest
        .keys()
        .filter(|key| {
            !matches!(
                key.as_str(),
                "$schema"
                    | "name"
                    | "version"
                    | "description"
                    | "author"
                    | "homepage"
                    | "repository"
                    | "license"
                    | "keywords"
                    | "extensions"
            )
        })
        .collect();
    Ok(if unknown.is_empty() {
        Vec::new()
    } else {
        vec!["Agent Plugin contains unknown manifest fields; they were ignored".to_owned()]
    })
}

fn reject_linked_parents(path: &Path) -> Result<()> {
    for parent in path.ancestors() {
        match fs::symlink_metadata(parent) {
            Ok(meta) => ensure!(
                !meta.file_type().is_symlink(),
                "skill destination contains a symbolic link"
            ),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error).with_context(|| format!("failed to inspect {}", parent.display())),
        }
    }
    Ok(())
}

fn ensure_absent(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(_) => bail!("skill destination or provenance already exists: {}", path.display()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error).with_context(|| format!("failed to inspect {}", path.display())),
    }
}

fn tree_digest(root: &Path) -> Result<String> {
    fn collect(root: &Path, dir: &Path, entries: &mut Vec<PathBuf>) -> Result<()> {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();
            let kind = entry.file_type()?;
            ensure!(!kind.is_symlink(), "managed skill contains a symbolic link");
            if kind.is_dir() {
                collect(root, &path, entries)?;
            } else {
                ensure!(kind.is_file(), "managed skill contains a non-file entry");
                ensure!(entries.len() < MAX_FILES, "managed skill contains too many files");
                entries.push(path.strip_prefix(root)?.to_path_buf());
            }
        }
        Ok(())
    }
    let mut entries = Vec::new();
    collect(root, root, &mut entries)?;
    let mut entries = entries
        .into_iter()
        .map(|path| {
            let name = path
                .to_str()
                .context("skill file path is not UTF-8")?
                .replace('\\', "/")
                .into_bytes();
            Ok((name, path))
        })
        .collect::<Result<Vec<_>>>()?;
    entries.sort_by(|left, right| left.0.cmp(&right.0));
    let mut contents = Vec::new();
    let mut file_bytes = 0u64;
    for (name, relative) in entries {
        let file = root.join(&relative);
        let size = fs::metadata(&file)?.len();
        ensure!(size <= MAX_UNPACKED - file_bytes, "managed skill exceeds 25 MiB");
        file_bytes += size;
        contents.extend_from_slice(&(name.len() as u64).to_le_bytes());
        contents.extend_from_slice(&name);
        contents.extend_from_slice(&size.to_le_bytes());
        contents.extend(fs::read(file)?);
    }
    Ok(sha256_hex(&contents))
}

fn managed_version(
    dest: &Path,
    state_path: &Path,
    manifest: &SkillManifest,
    agent: &str,
    global: bool,
) -> Result<Option<String>> {
    fn exists(path: &Path) -> Result<bool> {
        match fs::symlink_metadata(path) {
            Ok(_) => Ok(true),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
            Err(error) => Err(error).with_context(|| format!("failed to inspect {}", path.display())),
        }
    }
    let dest_exists = exists(dest)?;
    let state_exists = exists(state_path)?;
    if !dest_exists && !state_exists {
        return Ok(None);
    }
    ensure!(
        dest_exists && state_exists,
        "unmanaged or incomplete AgentSkill installation cannot be replaced"
    );
    ensure!(
        fs::metadata(dest)?.is_dir(),
        "managed AgentSkill destination is not a directory"
    );
    let state: serde_json::Value =
        serde_json::from_slice(&fs::read(state_path)?).context("invalid installed AgentSkill provenance")?;
    ensure!(
        state.get("manifestType").and_then(serde_json::Value::as_str) == Some("pinget.agentSkill")
            && state
                .get("packageIdentifier")
                .and_then(serde_json::Value::as_str)
                .is_some_and(|id| id.eq_ignore_ascii_case(&manifest.package_identifier))
            && state.get("packageName").and_then(serde_json::Value::as_str) == Some(manifest.package_name.as_str())
            && state.get("agent").and_then(serde_json::Value::as_str) == Some(agent)
            && state.get("scope").and_then(serde_json::Value::as_str) == Some(if global { "user" } else { "project" })
            && state.get("installedPath").and_then(serde_json::Value::as_str) == Some(dest.to_string_lossy().as_ref()),
        "installed AgentSkill provenance does not match the selected package, agent, scope, or path"
    );
    ensure!(
        state
            .get("catalogSource")
            .and_then(serde_json::Value::as_str)
            .is_none_or(|source| manifest.catalog_source.as_deref() == Some(source)),
        "installed AgentSkill belongs to a different catalog source"
    );
    let previous = state
        .get("packageVersion")
        .and_then(serde_json::Value::as_str)
        .context("installed AgentSkill provenance has no packageVersion")?;
    let old_digest = state
        .get("installedTreeSha256")
        .and_then(serde_json::Value::as_str)
        .context("installed AgentSkill provenance has no tree digest; remove and reinstall before updating")?;
    ensure!(
        tree_digest(dest)?.eq_ignore_ascii_case(old_digest),
        "installed AgentSkill files were modified; refusing to overwrite local changes"
    );
    Ok(Some(previous.to_owned()))
}

fn publish_skill(
    staging: &Path,
    state_staging: &Path,
    dest: &Path,
    state_path: &Path,
    version: Option<&str>,
) -> Result<()> {
    if version.is_none() {
        ensure_absent(dest)?;
        ensure_absent(state_path)?;
        fs::rename(staging, dest)?;
        if let Err(error) = fs::rename(state_staging, state_path) {
            fs::remove_dir_all(dest).context("failed to roll back skill after provenance write failure")?;
            return Err(error).context("failed to write skill provenance");
        }
        return Ok(());
    }
    let backup = staging.with_file_name(format!(
        "{}.backup",
        staging
            .file_name()
            .context("invalid skill staging path")?
            .to_string_lossy()
    ));
    let state_backup = state_staging.with_file_name(format!(
        "{}.backup",
        state_staging
            .file_name()
            .context("invalid provenance staging path")?
            .to_string_lossy()
    ));
    fs::rename(dest, &backup).context("failed to back up installed skill")?;
    if let Err(error) = fs::rename(state_path, &state_backup) {
        fs::rename(&backup, dest).context("failed to recover skill after provenance backup failure")?;
        return Err(error).context("failed to back up installed provenance");
    }
    let replace = (|| {
        fs::rename(staging, dest)?;
        fs::rename(state_staging, state_path)?;
        Ok::<_, std::io::Error>(())
    })();
    if let Err(error) = replace {
        if dest.exists() {
            fs::remove_dir_all(dest).context("failed to remove incomplete updated skill")?;
        }
        fs::rename(&backup, dest).context("failed to recover previous skill from backup")?;
        fs::rename(&state_backup, state_path).context("failed to recover previous provenance from backup")?;
        return Err(error).context("failed to publish updated AgentSkill; previous version restored");
    }
    fs::remove_dir_all(backup).context("updated AgentSkill installed, but could not remove old skill backup")?;
    fs::remove_file(state_backup)
        .context("updated AgentSkill installed, but could not remove old provenance backup")?;
    Ok(())
}

pub(crate) fn install(
    repository: &Repository,
    manifest: SkillManifest,
    request: &InstallRequest,
    agent: &str,
    global: bool,
    upgrade_only: bool,
) -> Result<InstallResult> {
    let query = &request.query;
    ensure!(
        request.mode != InstallerMode::Interactive
            && request.log_path.is_none()
            && request.custom.is_none()
            && request.override_args.is_none()
            && request.install_location.is_none()
            && !request.skip_dependencies
            && !request.dependencies_only
            && !request.accept_package_agreements
            && !request.force
            && request.rename.is_none()
            && !request.uninstall_previous
            && !request.ignore_security_hash
            && request.dependency_source.is_none()
            && query.query.is_none()
            && query.name.is_none()
            && query.moniker.is_none()
            && query.tag.is_none()
            && query.command.is_none()
            && query.count.is_none()
            && query.channel.is_none()
            && query.locale.is_none()
            && query.installer_type.is_none()
            && query.installer_architecture.is_none()
            && query.platform.is_none()
            && query.os_version.is_none(),
        "installer-specific options are not supported for AgentSkill manifests"
    );
    ensure!(
        query
            .id
            .as_deref()
            .is_none_or(|id| id.eq_ignore_ascii_case(&manifest.package_identifier))
            && query
                .version
                .as_deref()
                .is_none_or(|version| version == manifest.package_version)
            && (request.manifest_path.is_none() || query.source.is_none()),
        "AgentSkill query does not match the manifest"
    );
    let root = agent_root(agent, global)?;
    let dest = root.join(&manifest.package_name);
    let state_root = root
        .parent()
        .context("invalid skill root")?
        .parent()
        .context("invalid agent directory")?
        .join(".pinget")
        .join("agent-skills")
        .join(agent);
    let state_path = state_root.join(format!("{}.json", manifest.package_name));
    reject_linked_parents(&dest)?;
    reject_linked_parents(&state_path)?;
    let previous = managed_version(&dest, &state_path, &manifest, agent, global)?;
    ensure!(
        !upgrade_only || previous.is_some(),
        "No managed AgentSkill is installed to upgrade"
    );
    if let Some(old) = previous.as_deref() {
        match compare_version(&manifest.package_version, old) {
            Ordering::Less => bail!("AgentSkill downgrade is not supported"),
            Ordering::Equal if manifest.package_version != old => bail!("AgentSkill version order is ambiguous"),
            Ordering::Equal => {
                return Ok(InstallResult {
                    package_id: manifest.package_identifier,
                    version: manifest.package_version,
                    installer_path: dest,
                    installer_type: "agentSkill".to_owned(),
                    exit_code: 0,
                    success: true,
                    no_op: true,
                    warnings: Vec::new(),
                });
            }
            Ordering::Greater => ensure!(
                !request.no_upgrade,
                "--no-upgrade prevents updating the installed AgentSkill"
            ),
        }
    }
    let source = match &manifest.agent_skill.source {
        Some(source) => source.clone(),
        None => SkillSource::Archive {
            url: manifest
                .agent_skill
                .artifact_url
                .clone()
                .context("AgentSkill.ArtifactUrl is required")?,
            sha256: manifest
                .agent_skill
                .artifact_sha256
                .clone()
                .context("AgentSkill.ArtifactSha256 is required")?,
            format: "zip".to_owned(),
        },
    };
    let selection = selected_path(
        &manifest.agent_skill.skill_path,
        manifest.agent_skill.plugin_path.as_deref(),
    );
    let plugin_manifest = plugin_manifest_path(manifest.agent_skill.plugin_path.as_deref());
    let resolved = source.resolve(
        &repository.user_agent,
        &manifest.package_name,
        &selection,
        plugin_manifest.as_deref(),
    )?;
    let wrapped = if matches!(
        source,
        SkillSource::NotionSkill { .. } | SkillSource::NotionPlugin { .. }
    ) {
        notion_wrapped_paths(&resolved.zip, &selection, plugin_manifest.as_deref())?
    } else {
        None
    };
    let (selection, plugin_manifest) = wrapped
        .as_ref()
        .map(|(skill, plugin)| (skill.as_str(), plugin.as_deref()))
        .unwrap_or((selection.as_str(), plugin_manifest.as_deref()));
    let warnings = if let Some(path) = plugin_manifest {
        validate_plugin(&resolved.zip, path)?
    } else {
        Vec::new()
    };
    fs::create_dir_all(&root)?;
    fs::create_dir_all(&state_root)?;
    let nonce = chrono::Utc::now().timestamp_nanos_opt().context("invalid clock time")?;
    let staging = root.join(format!(
        ".{}.pinget-{}-{nonce}",
        manifest.package_name,
        std::process::id()
    ));
    let state_staging = state_root.join(format!(
        ".{}.pinget-{}-{nonce}.json",
        manifest.package_name,
        std::process::id()
    ));
    fs::create_dir(&staging)?;
    let result = (|| {
        unpack_skill(&resolved.zip, Path::new(selection), &staging)?;
        validate_skill_md(&staging, &manifest.package_name)?;
        let artifact_url = manifest
            .agent_skill
            .artifact_url
            .as_deref()
            .map(provenance_url)
            .transpose()?;
        let installed_tree = tree_digest(&staging)?;
        let state = serde_json::json!({
            "manifestType": manifest.manifest_type,
            "packageIdentifier": manifest.package_identifier,
            "packageVersion": manifest.package_version,
            "packageName": manifest.package_name,
            "publisher": manifest.publisher,
            "agent": agent,
            "scope": if global { "user" } else { "project" },
            "artifactUrl": artifact_url,
            "artifactSha256": resolved.sha256.to_ascii_uppercase(),
            "installedTreeSha256": installed_tree,
            "sourceType": resolved.kind,
            "sourceUrl": provenance_url(&resolved.url)?,
            "sourceRevision": resolved.revision,
            "catalogSource": manifest.catalog_source,
            "catalogManifestSha256": manifest.catalog_manifest_sha256,
            "skillPath": if manifest.agent_skill.skill_path.is_empty() { None } else { Some(&manifest.agent_skill.skill_path) },
            "pluginPath": manifest.agent_skill.plugin_path,
            "installedPath": dest,
            "installedAt": chrono::Utc::now(),
        });
        let mut state_file = File::create_new(&state_staging)?;
        serde_json::to_writer_pretty(&mut state_file, &state)?;
        state_file.flush()?;
        if previous.is_some() {
            ensure!(
                managed_version(&dest, &state_path, &manifest, agent, global)? == previous,
                "installed AgentSkill changed during update"
            );
        }
        publish_skill(&staging, &state_staging, &dest, &state_path, previous.as_deref())?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_dir_all(&staging);
        let _ = fs::remove_file(&state_staging);
    }
    result?;
    Ok(InstallResult {
        package_id: manifest.package_identifier,
        version: manifest.package_version,
        installer_path: dest,
        installer_type: "agentSkill".to_owned(),
        exit_code: 0,
        success: true,
        no_op: false,
        warnings,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{PackageQuery, RepositoryOptions};

    #[test]
    fn requires_typed_manifest_and_rejects_unsafe_path() {
        assert!(
            parse(b"ManifestType: singleton\nPackageIdentifier: App.Test")
                .expect("parse ordinary manifest")
                .is_none()
        );
        assert!(
            parse(b"ManifestType: version\n---\nManifestType: installer")
                .expect("parse multi-document WinGet manifest")
                .is_none()
        );
        let manifest = b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.Skill\nPackageVersion: 1.0.0\nPackageName: demo\nAgentSkill:\n  ArtifactUrl: https://example.test/skill.zip\n  ArtifactSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n  SkillPath: ../escape\n";
        let error = parse(manifest).expect_err("reject unsafe skill path");
        assert!(error.to_string().contains("unsafe skill archive path"), "{error:#}");
    }

    #[test]
    fn rejects_mixed_sources_and_plugins_without_explicit_skill_path() {
        let base = "ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.Skill\nPackageVersion: 1.0.0\nPackageName: demo\nAgentSkill:\n  Source:\n    Type: git\n    Url: https://github.com/example/skills.git\n    Commit: 0123456789abcdef0123456789abcdef01234567\n";
        let mixed = format!("{base}  ArtifactUrl: https://example.org/archive.zip\n");
        assert!(parse(mixed.as_bytes()).is_err(), "reject mixed legacy and typed fields");
        let incomplete = format!("{base}  PluginPath: .\n");
        assert!(
            parse(incomplete.as_bytes()).is_err(),
            "require explicit plugin skill path"
        );
        let unknown = format!("{base}  AccessToken: secret\n");
        assert!(parse(unknown.as_bytes()).is_err(), "reject unsupported manifest fields");
    }

    #[test]
    fn plugin_source_selects_only_skill_and_validates_plugin_manifest() {
        let manifest = b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.Skill\nPackageVersion: 1.0.0\nPackageName: demo\nAgentSkill:\n  Source:\n    Type: git\n    Url: https://github.com/example/skills.git\n    Commit: 0123456789abcdef0123456789abcdef01234567\n  PluginPath: .\n  SkillPath: skills/demo\n";
        let parsed = parse(manifest)
            .expect("parse plugin source")
            .expect("typed skill manifest");
        assert_eq!(parsed.agent_skill.plugin_path.as_deref(), Some("."));
        let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
        zip.start_file("plugin.json", zip::write::SimpleFileOptions::default())
            .expect("create plugin manifest");
        zip.write_all(
            br#"{"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"example.plugin"}"#,
        )
        .expect("write plugin manifest");
        zip.start_file("skills/demo/SKILL.md", zip::write::SimpleFileOptions::default())
            .expect("create skill");
        zip.write_all(b"---\nname: demo\ndescription: Demo\n---\nInstructions\n")
            .expect("write skill");
        let archive = zip.finish().expect("finish plugin").into_inner();
        assert!(
            validate_plugin(&archive, "plugin.json")
                .expect("validate plugin")
                .is_empty()
        );
        let dest = std::env::temp_dir().join(format!("pinget-plugin-test-{}", std::process::id()));
        fs::create_dir_all(&dest).expect("create destination");
        unpack_skill(&archive, Path::new("skills/demo"), &dest).expect("select plugin skill");
        validate_skill_md(&dest, "demo").expect("validate plugin skill");
        assert!(
            !dest.join("plugin.json").exists(),
            "plugin metadata must not be installed as a skill"
        );
        fs::remove_dir_all(dest).expect("remove test destination");
    }

    #[test]
    fn notion_wrappers_preserve_selected_plugin_paths_and_provenance_omits_queries() {
        let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
        zip.start_file("export/plugin.json", zip::write::SimpleFileOptions::default())
            .expect("create plugin manifest");
        zip.write_all(b"{}").expect("write plugin manifest");
        zip.start_file("export/skills/demo/SKILL.md", zip::write::SimpleFileOptions::default())
            .expect("create skill");
        zip.write_all(b"skill").expect("write skill");
        let bytes = zip.finish().expect("finish archive").into_inner();
        assert_eq!(
            notion_wrapped_paths(&bytes, "skills/demo", Some("plugin.json")).expect("resolve Notion wrapper"),
            Some(("export/skills/demo".to_owned(), Some("export/plugin.json".to_owned())))
        );
        assert_eq!(
            provenance_url("https://example.org/skill.zip?signature=private").expect("sanitize provenance"),
            "https://example.org/skill.zip"
        );
    }

    #[test]
    fn rejects_archive_escape_and_invalid_frontmatter() {
        assert!(safe_path("../escape").is_err());
        assert!(safe_path("C:\\escape").is_err());
        assert!(safe_path("sample-skill/CON").is_err());
        assert!(safe_path("sample-skill/trailing.").is_err());
        assert!(!valid_name("Bad-Skill"));
        assert!(valid_name("valid-skill"));
        let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
        zip.start_file("../escape", zip::write::SimpleFileOptions::default())
            .expect("create archive entry");
        zip.write_all(b"bad").expect("write archive entry");
        let bytes = zip.finish().expect("finish archive").into_inner();
        let dest = std::env::temp_dir().join(format!("pinget-skill-test-escape-{}", std::process::id()));
        assert!(unpack_skill(&bytes, Path::new(""), &dest).is_err());
        assert!(!dest.exists());
    }

    #[test]
    fn unpacks_only_selected_skill_and_validates_frontmatter() {
        use zip::write::SimpleFileOptions;
        let buffer = Cursor::new(Vec::new());
        let mut zip = zip::ZipWriter::new(buffer);
        let options = SimpleFileOptions::default();
        zip.start_file("skills/demo/SKILL.md", options)
            .expect("create skill entry");
        zip.write_all(b"---\nname: demo\ndescription: A test skill\n---\nInstructions\n")
            .expect("write skill");
        zip.start_file("skills/demo/references/notes.txt", options)
            .expect("create reference entry");
        zip.write_all(b"reference").expect("write reference");
        zip.start_file("skills/other/SKILL.md", options)
            .expect("create other entry");
        zip.write_all(b"not selected").expect("write other entry");
        let bytes = zip.finish().expect("finish archive").into_inner();
        let dest = std::env::temp_dir().join(format!("pinget-skill-test-{}", std::process::id()));
        fs::create_dir_all(&dest).expect("create test destination");
        unpack_skill(&bytes, Path::new("skills/demo"), &dest).expect("extract selected skill");
        validate_skill_md(&dest, "demo").expect("validate extracted skill");
        assert_eq!(
            fs::read(dest.join("references/notes.txt")).expect("read reference"),
            b"reference"
        );
        assert!(!dest.join("other").exists());
        fs::write(
            dest.join("SKILL.md"),
            b"---\nname: demo\ndescription: A test skill\n---\n",
        )
        .expect("write empty skill");
        assert!(validate_skill_md(&dest, "demo").is_err());
        fs::remove_dir_all(dest).expect("remove test destination");
    }

    #[test]
    fn install_from_manifest_directory_requires_agent_and_rejects_installer_switches() {
        let root = std::env::temp_dir().join(format!(
            "pinget-skill-routing-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().expect("clock time")
        ));
        fs::create_dir_all(&root).expect("create test directory");
        let manifest = b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.Skill\nPackageVersion: 1.0.0\nPackageName: demo\nAgentSkill:\n  ArtifactUrl: https://example.test/skill.zip\n  ArtifactSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n";
        fs::write(root.join("skill.yaml"), manifest).expect("write skill manifest");
        let mut repo =
            Repository::open_with_options(RepositoryOptions::new(root.join("repository"))).expect("open repository");
        let mut request = InstallRequest::new(PackageQuery::default());
        request.manifest_path = Some(root.clone());
        let error = repo.install_request(&request).expect_err("require explicit agent");
        assert!(error.to_string().contains("--agent"), "{error:#}");
        request.agent = Some("github-copilot".to_owned());
        request.ignore_security_hash = true;
        let error = repo
            .install_request(&request)
            .expect_err("reject installer options before download");
        assert!(error.to_string().contains("installer-specific options"), "{error:#}");
        fs::remove_dir_all(root).expect("clean test directory");
    }

    #[test]
    fn upgrade_does_not_install_an_unmanaged_skill() {
        let manifest = parse(b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.MissingUpgrade\nPackageVersion: 1.0.0\nPackageName: pinget-missing-upgrade\nAgentSkill:\n  ArtifactUrl: https://example.test/skill.zip\n  ArtifactSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")
            .expect("parse manifest")
            .expect("typed manifest");
        let repo = Repository::open_with_options(RepositoryOptions::new(
            std::env::temp_dir().join(format!("pinget-missing-upgrade-{}", std::process::id())),
        ))
        .expect("open repository");
        let request = InstallRequest::new(PackageQuery::default());
        let error = install(&repo, manifest, &request, "github-copilot", false, true)
            .expect_err("upgrade requires an existing managed skill");
        assert!(error.to_string().contains("No managed AgentSkill"), "{error:#}");
    }

    #[test]
    fn managed_update_replaces_version_and_recovers_if_provenance_publish_fails() {
        let root = std::env::temp_dir().join(format!(
            "pinget-skill-update-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().expect("clock time")
        ));
        let dest = root.join(".agents/skills/demo");
        let state_path = root.join(".pinget/agent-skills/github-copilot/demo.json");
        fs::create_dir_all(&dest).expect("create installed skill");
        fs::create_dir_all(state_path.parent().expect("state root")).expect("create provenance root");
        fs::write(dest.join("SKILL.md"), b"original").expect("write original skill");
        let old_tree = tree_digest(&dest).expect("hash installed skill");
        let mut state = serde_json::json!({
            "manifestType": "pinget.agentSkill",
            "packageIdentifier": "Test.Skill",
            "packageName": "demo",
            "packageVersion": "1.0.0",
            "agent": "github-copilot",
            "scope": "project",
            "installedPath": dest,
            "installedTreeSha256": old_tree
        });
        fs::write(&state_path, serde_json::to_vec(&state).expect("encode provenance")).expect("write provenance");
        let mut manifest = parse(b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Test.Skill\nPackageVersion: 1.1.0\nPackageName: demo\nAgentSkill:\n  ArtifactUrl: https://example.test/skill.zip\n  ArtifactSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")
            .expect("parse update manifest")
            .expect("typed manifest");
        assert_eq!(
            managed_version(&dest, &state_path, &manifest, "github-copilot", false).expect("managed installation"),
            Some("1.0.0".to_owned())
        );
        state["catalogSource"] = serde_json::json!("skills");
        fs::write(
            &state_path,
            serde_json::to_vec(&state).expect("encode catalog provenance"),
        )
        .expect("write catalog provenance");
        manifest.catalog_source = Some("other".to_owned());
        assert!(
            managed_version(&dest, &state_path, &manifest, "github-copilot", false).is_err(),
            "an update from another catalog cannot replace a managed skill"
        );
        manifest.catalog_source = Some("skills".to_owned());
        assert!(managed_version(&dest, &state_path, &manifest, "github-copilot", false).is_ok());
        fs::write(dest.join("SKILL.md"), b"locally changed").expect("modify installed skill");
        assert!(
            managed_version(&dest, &state_path, &manifest, "github-copilot", false).is_err(),
            "updates must not replace locally modified files"
        );
        fs::write(dest.join("SKILL.md"), b"original").expect("restore installed skill");
        let staging = root.join(".agents/skills/.new-stage");
        let state_staging = root.join(".pinget/agent-skills/github-copilot/.new-state");
        fs::create_dir(&staging).expect("create staged skill");
        fs::write(staging.join("SKILL.md"), b"updated").expect("write staged skill");
        let error = publish_skill(&staging, &state_staging, &dest, &state_path, Some("1.0.0"))
            .expect_err("missing staged provenance must roll back");
        assert!(error.to_string().contains("previous version restored"), "{error:#}");
        assert_eq!(
            fs::read(dest.join("SKILL.md")).expect("read restored skill"),
            b"original"
        );
        assert!(state_path.exists(), "restore original provenance");
        fs::create_dir(&staging).expect("create staged skill again");
        fs::write(staging.join("SKILL.md"), b"updated").expect("write replacement skill");
        fs::write(&state_staging, b"new provenance").expect("write replacement provenance");
        publish_skill(&staging, &state_staging, &dest, &state_path, Some("1.0.0")).expect("publish managed update");
        assert_eq!(fs::read(dest.join("SKILL.md")).expect("read updated skill"), b"updated");
        assert_eq!(fs::read(&state_path).expect("read updated state"), b"new provenance");
        assert!(!root.join(".agents/skills/.new-stage.backup").exists());
        assert!(
            !root
                .join(".pinget/agent-skills/github-copilot/.new-state.backup")
                .exists()
        );
        fs::remove_dir_all(root).expect("remove update fixture");
    }
}
