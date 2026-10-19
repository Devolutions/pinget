use std::collections::HashSet;
use std::io::{Cursor, Read, Write};
use std::time::Duration;

use anyhow::{Context, Result, bail, ensure};
use flate2::read::GzDecoder;
use reqwest::Url;
use reqwest::blocking::Client;
use reqwest::redirect::Policy;
use serde::Deserialize;
use serde_json::Value;
use zip::write::SimpleFileOptions;

use crate::agent_skill::{safe_path, valid_name};

const MAX_DOWNLOAD: u64 = 10 * 1024 * 1024;
const MAX_UNPACKED: u64 = 25 * 1024 * 1024;
const MAX_FILES: usize = 1000;
const DISCOVERY_SCHEMA: &str = "https://schemas.agentskills.io/discovery/0.2.0/schema.json";
const NOTION_VERSION: &str = "2026-03-11";

#[derive(Clone, Debug, Deserialize)]
#[serde(
    tag = "Type",
    rename_all = "kebab-case",
    rename_all_fields = "PascalCase",
    deny_unknown_fields
)]
pub(crate) enum SkillSource {
    Archive {
        url: String,
        sha256: String,
        format: String,
    },
    Git {
        url: String,
        commit: String,
    },
    NotionSkill {
        id: String,
        version_id: String,
    },
    NotionPlugin {
        id: String,
        version_id: String,
    },
    WellKnown {
        index_url: String,
        sha256: String,
    },
}

pub(crate) struct ResolvedSource {
    pub zip: Vec<u8>,
    pub kind: &'static str,
    pub url: String,
    pub revision: Option<String>,
    pub sha256: String,
}

fn digest_valid(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

pub(crate) fn https_url(value: &str) -> Result<Url> {
    let url = Url::parse(value).context("AgentSkill source URL must be an absolute HTTPS URL")?;
    ensure!(
        url.scheme() == "https" && url.username().is_empty() && url.password().is_none() && url.fragment().is_none(),
        "AgentSkill source URL must use HTTPS without credentials or a fragment"
    );
    Ok(url)
}

fn is_uuid(value: &str) -> bool {
    value.len() == 36
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
}

impl SkillSource {
    pub(crate) fn validate(&self) -> Result<()> {
        match self {
            Self::Archive { url, sha256, format } => {
                https_url(url)?;
                ensure!(
                    digest_valid(sha256),
                    "AgentSkill.Source.Sha256 must be 64 hexadecimal digits"
                );
                ensure!(
                    matches!(format.as_str(), "zip" | "tar.gz" | "skill-md"),
                    "AgentSkill.Source.Format must be zip, tar.gz, or skill-md"
                );
            }
            Self::Git { url, commit } => {
                https_url(url)?;
                ensure!(
                    commit.len() == 40 && commit.bytes().all(|byte| byte.is_ascii_hexdigit()),
                    "AgentSkill.Source.Commit must be a full 40-character Git SHA-1"
                );
            }
            Self::NotionSkill { id, version_id } | Self::NotionPlugin { id, version_id } => {
                ensure!(is_uuid(id), "AgentSkill.Source.Id must be a Notion UUID");
                ensure!(
                    version_id.len() == 64 && version_id.bytes().all(|byte| byte.is_ascii_graphic()),
                    "AgentSkill.Source.VersionId must be a 64-character Notion version ID"
                );
            }
            Self::WellKnown { index_url, sha256 } => {
                https_url(index_url)?;
                ensure!(
                    digest_valid(sha256),
                    "AgentSkill.Source.Sha256 must be 64 hexadecimal digits"
                );
            }
        }
        Ok(())
    }

    pub(crate) fn is_notion_plugin(&self) -> bool {
        matches!(self, Self::NotionPlugin { .. })
    }

    pub(crate) fn resolve(
        &self,
        user_agent: &str,
        skill_name: &str,
        path: &str,
        plugin_manifest: Option<&str>,
    ) -> Result<ResolvedSource> {
        self.validate()?;
        match self {
            Self::Archive { url, sha256, format } => {
                let bytes = download(&https_url(url)?, user_agent, MAX_DOWNLOAD)?;
                check_digest(&bytes, sha256)?;
                Ok(ResolvedSource {
                    zip: convert(&bytes, format)?,
                    kind: "archive",
                    url: url.clone(),
                    revision: None,
                    sha256: crate::sha256_hex(&bytes),
                })
            }
            Self::Git { url, commit } => {
                let zip = crate::agent_skill_git::fetch_git_zip(url, commit, path, plugin_manifest)?;
                Ok(ResolvedSource {
                    sha256: crate::sha256_hex(&zip),
                    zip,
                    kind: "git",
                    url: url.clone(),
                    revision: Some(commit.clone()),
                })
            }
            Self::NotionSkill { id, version_id } | Self::NotionPlugin { id, version_id } => {
                let plugin = self.is_notion_plugin();
                let endpoint = format!(
                    "https://api.notion.com/v1/ai/{}/{}",
                    if plugin { "plugins" } else { "skills" },
                    id
                );
                let token = std::env::var("NOTION_API_KEY")
                    .context("NOTION_API_KEY is required for a Notion AgentSkill source")?;
                ensure!(!token.is_empty(), "NOTION_API_KEY must not be empty");
                let client = Client::builder()
                    .user_agent(user_agent)
                    .timeout(Duration::from_secs(30))
                    .redirect(Policy::none())
                    .build()
                    .context("failed to create Notion API client")?;
                let response = client
                    .get(&endpoint)
                    .bearer_auth(token)
                    .header("Notion-Version", NOTION_VERSION)
                    .send()
                    .context("failed to request Notion skill directory")?
                    .error_for_status()
                    .context("Notion skill directory request failed")?;
                ensure!(
                    !response.status().is_redirection(),
                    "Notion API redirected the authenticated request"
                );
                let mut json = Vec::new();
                response.take(1024 * 1024 + 1).read_to_end(&mut json)?;
                ensure!(json.len() <= 1024 * 1024, "Notion directory response exceeds 1 MiB");
                let data: Value = serde_json::from_slice(&json).context("invalid Notion skill directory response")?;
                let signed = notion_download_url(&data, id, version_id)?;
                let bytes = download(&signed, user_agent, MAX_DOWNLOAD)
                    .context("failed to fetch the Notion skill directory")?;
                Ok(ResolvedSource {
                    zip: tar_gz_to_zip(&bytes, true)?,
                    kind: if plugin { "notion-plugin" } else { "notion-skill" },
                    url: endpoint,
                    revision: Some(version_id.clone()),
                    sha256: crate::sha256_hex(&bytes),
                })
            }
            Self::WellKnown { index_url, sha256 } => {
                let index = https_url(index_url)?;
                let bytes = download(&index, user_agent, 1024 * 1024)?;
                let data: Value = serde_json::from_slice(&bytes).context("invalid Agent Skills discovery index")?;
                let (artifact, kind) = discovery_asset(&data, &index, skill_name, sha256)?;
                let downloaded = download(&artifact, user_agent, MAX_DOWNLOAD)?;
                check_digest(&downloaded, sha256)?;
                let format = if kind == "skill-md" {
                    "skill-md"
                } else if downloaded.starts_with(b"PK") {
                    "zip"
                } else if downloaded.starts_with(&[0x1f, 0x8b]) {
                    "tar.gz"
                } else {
                    bail!("unsupported discovery skill archive format")
                };
                Ok(ResolvedSource {
                    zip: convert(&downloaded, format)?,
                    kind: "well-known",
                    url: index_url.clone(),
                    revision: Some(sha256.clone()),
                    sha256: crate::sha256_hex(&downloaded),
                })
            }
        }
    }
}

fn notion_download_url(data: &Value, id: &str, version_id: &str) -> Result<Url> {
    ensure!(
        data.get("id").and_then(Value::as_str) == Some(id)
            && data.get("version_id").and_then(Value::as_str) == Some(version_id),
        "Notion skill directory identity or version changed"
    );
    let signed = data
        .get("url")
        .and_then(Value::as_str)
        .context("Notion directory URL is missing")?;
    https_url(signed)
}

fn discovery_asset(data: &Value, index: &Url, name: &str, sha256: &str) -> Result<(Url, String)> {
    ensure!(
        data.get("$schema").and_then(Value::as_str) == Some(DISCOVERY_SCHEMA),
        "unsupported Agent Skills discovery schema"
    );
    let entries = data
        .get("skills")
        .and_then(Value::as_array)
        .context("discovery index has no skills")?;
    let mut matches = entries
        .iter()
        .filter(|entry| entry.get("name").and_then(Value::as_str) == Some(name));
    let entry = matches
        .next()
        .context("skill not found in Agent Skills discovery index")?;
    ensure!(
        matches.next().is_none(),
        "discovery index contains duplicate skill names"
    );
    ensure!(valid_name(name), "discovery entry has an invalid skill name");
    let digest = entry
        .get("digest")
        .and_then(Value::as_str)
        .context("discovery skill digest is missing")?;
    ensure!(
        digest
            .strip_prefix("sha256:")
            .is_some_and(|hash| hash.eq_ignore_ascii_case(sha256)),
        "discovery skill digest does not match AgentSkill.Source.Sha256"
    );
    let kind = entry
        .get("type")
        .and_then(Value::as_str)
        .context("discovery skill type is missing")?;
    ensure!(
        matches!(kind, "archive" | "skill-md"),
        "unsupported discovery skill artifact type"
    );
    let relative_url = entry
        .get("url")
        .and_then(Value::as_str)
        .context("discovery skill URL is missing")?;
    let artifact = index.join(relative_url).context("invalid discovery skill URL")?;
    https_url(artifact.as_str())?;
    Ok((artifact, kind.to_owned()))
}

fn client(user_agent: &str) -> Result<Client> {
    Client::builder()
        .user_agent(user_agent)
        .timeout(Duration::from_secs(120))
        .redirect(Policy::custom(|attempt| {
            if attempt.previous().len() >= 5
                || attempt.url().scheme() != "https"
                || !attempt.url().username().is_empty()
                || attempt.url().password().is_some()
            {
                attempt.stop()
            } else {
                attempt.follow()
            }
        }))
        .build()
        .context("failed to create AgentSkill source client")
}

pub(crate) fn download(url: &Url, user_agent: &str, limit: u64) -> Result<Vec<u8>> {
    let client = client(user_agent)?;
    let response = client
        .get(url.clone())
        .send()
        .map_err(reqwest::Error::without_url)
        .context("failed to download AgentSkill source")?
        .error_for_status()
        .map_err(reqwest::Error::without_url)?;
    ensure!(
        !response.status().is_redirection(),
        "AgentSkill source redirected to an untrusted URL"
    );
    let mut bytes = Vec::new();
    response.take(limit + 1).read_to_end(&mut bytes)?;
    ensure!(
        bytes.len() as u64 <= limit,
        "AgentSkill source exceeds download size limit"
    );
    Ok(bytes)
}

fn check_digest(bytes: &[u8], expected: &str) -> Result<()> {
    ensure!(
        crate::sha256_hex(bytes).eq_ignore_ascii_case(expected),
        "AgentSkill source SHA-256 does not match the manifest"
    );
    Ok(())
}

fn convert(bytes: &[u8], format: &str) -> Result<Vec<u8>> {
    match format {
        "zip" => Ok(bytes.to_vec()),
        "tar.gz" => tar_gz_to_zip(bytes, false),
        "skill-md" => {
            ensure!(bytes.len() <= 1024 * 1024, "SKILL.md exceeds the 1 MiB limit");
            let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
            zip.start_file("SKILL.md", SimpleFileOptions::default())?;
            zip.write_all(bytes)?;
            Ok(zip.finish()?.into_inner())
        }
        _ => bail!("unsupported AgentSkill source format"),
    }
}

fn tar_gz_to_zip(bytes: &[u8], normalize_notion_wrapper: bool) -> Result<Vec<u8>> {
    let mut tar = tar::Archive::new(GzDecoder::new(bytes).take(MAX_UNPACKED + (MAX_FILES as u64) * 4096 + 512));
    let mut files = Vec::new();
    let mut seen = HashSet::new();
    let mut size = 0u64;
    for entry in tar.entries().context("invalid AgentSkill tar.gz archive")? {
        let entry = entry.context("invalid AgentSkill tar.gz entry")?;
        ensure!(seen.len() < MAX_FILES, "AgentSkill tar.gz archive has too many entries");
        let path = String::from_utf8(entry.path_bytes().into_owned()).context("non-UTF-8 tar entry path")?;
        let path = path.strip_prefix("./").unwrap_or(&path).trim_end_matches('/');
        if path.is_empty() {
            continue;
        }
        safe_path(path)?;
        ensure!(
            seen.insert(path.to_ascii_lowercase()),
            "AgentSkill tar.gz archive has duplicate paths"
        );
        if entry.header().entry_type().is_dir() {
            continue;
        }
        ensure!(
            entry.header().entry_type().is_file(),
            "AgentSkill tar.gz archive contains a link or unsupported entry"
        );
        let expected = entry.size();
        ensure!(
            expected <= MAX_UNPACKED - size,
            "AgentSkill tar.gz archive exceeds extraction limit"
        );
        let mut contents = Vec::new();
        entry.take(expected + 1).read_to_end(&mut contents)?;
        ensure!(
            contents.len() as u64 == expected,
            "AgentSkill tar entry size does not match metadata"
        );
        size += expected;
        files.push((path.to_owned(), contents));
    }
    ensure!(!files.is_empty(), "AgentSkill tar.gz archive contains no files");
    let wrapper = files[0]
        .0
        .split('/')
        .next()
        .context("invalid AgentSkill archive path")?
        .to_owned();
    let has_wrapper = normalize_notion_wrapper
        && files.iter().all(|(path, _)| path.starts_with(&format!("{wrapper}/")))
        && files
            .iter()
            .any(|(path, _)| path == &format!("{wrapper}/SKILL.md") || path == &format!("{wrapper}/plugin.json"));
    let mut zip = zip::ZipWriter::new(Cursor::new(Vec::new()));
    for (path, contents) in files {
        let path = if has_wrapper {
            path.strip_prefix(&format!("{wrapper}/"))
                .context("invalid archive wrapper")?
        } else {
            &path
        };
        zip.start_file(path, SimpleFileOptions::default())?;
        zip.write_all(&contents)?;
    }
    Ok(zip.finish()?.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn source_variants_require_pins_and_safe_urls() {
        let git: SkillSource = serde_yaml::from_str(
            "Type: git\nUrl: https://github.com/example/skills.git\nCommit: 0123456789abcdef0123456789abcdef01234567\n",
        )
        .expect("parse pinned Git source");
        git.validate().expect("validate pinned Git source");
        for source in [
            format!(
                "Type: notion-skill\nId: 01234567-89ab-cdef-0123-456789abcdef\nVersionId: {}\n",
                "v".repeat(64)
            ),
            format!(
                "Type: notion-plugin\nId: 01234567-89ab-cdef-0123-456789abcdef\nVersionId: {}\n",
                "v".repeat(64)
            ),
            format!(
                "Type: well-known\nIndexUrl: https://example.org/.well-known/agent-skills/index.json\nSha256: {}\n",
                "a".repeat(64)
            ),
        ] {
            let parsed: SkillSource = serde_yaml::from_str(&source).expect("parse typed skill source");
            parsed.validate().expect("validate typed skill source");
        }
        for source in [
            "Type: git\nUrl: file:///private/repo\nCommit: 0123456789abcdef0123456789abcdef01234567\n",
            "Type: git\nUrl: https://example.org/repo.git\nCommit: main\n",
            "Type: archive\nUrl: https://example.org/a.zip\nSha256: bad\nFormat: zip\n",
            "Type: archive\nUrl: https://example.org/a.zip\nSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nFormat: exe\n",
        ] {
            let parsed: SkillSource = serde_yaml::from_str(source).expect("parse source shape");
            assert!(parsed.validate().is_err(), "{source}");
        }
        let unknown = serde_yaml::from_str::<SkillSource>(
            "Type: git\nUrl: https://example.org/repo.git\nCommit: 0123456789abcdef0123456789abcdef01234567\nToken: secret\n",
        );
        assert!(
            unknown.is_err(),
            "reject unexpected and potentially secret-bearing source fields"
        );
    }

    #[test]
    fn notion_directory_requires_exact_identity_and_discovery_requires_pinned_digest() {
        let id = "01234567-89ab-cdef-0123-456789abcdef";
        let version = "a".repeat(64);
        let response = serde_json::json!({
            "id": id,
            "version_id": version,
            "url": "https://example.org/temporary.tar.gz?signature=private"
        });
        assert_eq!(
            notion_download_url(&response, id, &version)
                .expect("read signed directory URL")
                .host_str(),
            Some("example.org")
        );
        assert!(notion_download_url(&response, id, &"b".repeat(64)).is_err());
        let digest = "a".repeat(64);
        let index = Url::parse("https://example.org/.well-known/agent-skills/index.json").expect("index URL");
        let data = serde_json::json!({
            "$schema": DISCOVERY_SCHEMA,
            "skills": [{"name": "demo", "type": "archive", "url": "demo.zip", "digest": format!("sha256:{digest}")}]
        });
        let (artifact, kind) = discovery_asset(&data, &index, "demo", &digest).expect("select pinned discovery entry");
        assert_eq!(
            artifact.as_str(),
            "https://example.org/.well-known/agent-skills/demo.zip"
        );
        assert_eq!(kind, "archive");
        assert!(discovery_asset(&data, &index, "demo", &"b".repeat(64)).is_err());
    }

    #[test]
    fn tar_archive_normalizes_notion_wrapper_and_rejects_links() {
        fn archive(link: bool) -> Vec<u8> {
            let output = Vec::new();
            let encoder = flate2::write::GzEncoder::new(output, flate2::Compression::default());
            let mut tar = tar::Builder::new(encoder);
            let content = b"---\nname: demo\ndescription: Demo\n---\nInstructions\n";
            let mut header = tar::Header::new_gnu();
            header.set_size(content.len() as u64);
            header.set_mode(0o644);
            header.set_cksum();
            tar.append_data(&mut header, "notion-export/SKILL.md", &content[..])
                .expect("append skill");
            if link {
                let mut symlink = tar::Header::new_gnu();
                symlink.set_entry_type(tar::EntryType::Symlink);
                symlink.set_size(0);
                symlink.set_link_name("../../escape").expect("set link target");
                symlink.set_cksum();
                tar.append_data(&mut symlink, "notion-export/link", std::io::empty())
                    .expect("append link");
            }
            tar.into_inner().expect("finish tar").finish().expect("finish gzip")
        }
        let zip = tar_gz_to_zip(&archive(false), true).expect("convert Notion tar.gz");
        let mut reader = zip::ZipArchive::new(Cursor::new(zip)).expect("read converted ZIP");
        assert!(reader.by_name("SKILL.md").is_ok(), "strip unique Notion wrapper");
        let zip = tar_gz_to_zip(&archive(false), false).expect("convert pinned archive tar.gz");
        let mut reader = zip::ZipArchive::new(Cursor::new(zip)).expect("read converted ZIP");
        assert!(
            reader.by_name("notion-export/SKILL.md").is_ok(),
            "keep explicit archive paths"
        );
        assert!(
            tar_gz_to_zip(&archive(true), true).is_err(),
            "reject symlink in Notion tarball"
        );
    }
}
