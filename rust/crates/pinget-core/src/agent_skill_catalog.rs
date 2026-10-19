use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, ensure};
use serde::Deserialize;
use serde_json::Value;

use crate::agent_skill::{self, SkillManifest};
use crate::agent_skill_source::{download, https_url};
use crate::{
    PackageQuery, Repository, SearchMatch, SearchResponse, SourceKind, SourceRecord, SourceStore, SourceUpdateResult,
    compare_version, max_results, sha256_hex,
};

const MAX_INDEX: u64 = 1024 * 1024;
const MAX_MANIFEST: u64 = 1024 * 1024;
const MAX_PACKAGES: usize = 1000;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Catalog {
    schema_version: u32,
    packages: Vec<CatalogEntry>,
}

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CatalogEntry {
    package_identifier: String,
    package_version: String,
    package_name: String,
    manifest_url: String,
    manifest_sha256: String,
}

fn catalog_store_path(app_root: &Path) -> PathBuf {
    app_root.join("agent-skill-sources.json")
}

const SOURCE_METADATA_KEYS: &[(&str, &str)] = &[
    ("trust_level", "trustLevel"),
    ("last_update", "lastUpdate"),
    ("source_version", "sourceVersion"),
    ("etag", "eTag"),
    ("last_modified", "lastModified"),
];

fn catalog_source_keys(store: &mut Value, to_camel_case: bool) -> Result<()> {
    let sources = store
        .get_mut("sources")
        .and_then(Value::as_array_mut)
        .context("invalid AgentSkill source store")?;
    for source in sources {
        let fields = source.as_object_mut().context("invalid AgentSkill source entry")?;
        for &(snake, camel) in SOURCE_METADATA_KEYS {
            let (from, to) = if to_camel_case { (snake, camel) } else { (camel, snake) };
            if let Some(value) = fields.remove(from) {
                ensure!(!fields.contains_key(to), "duplicate AgentSkill source metadata");
                fields.insert(to.to_owned(), value);
            }
        }
    }
    Ok(())
}

fn catalog_cache_path(app_root: &Path, source: &SourceRecord) -> PathBuf {
    app_root
        .join("agent-skill-catalogs")
        .join(format!("{}.json", sha256_hex(source.name.as_bytes())))
}

fn write_atomically(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path.parent().context("invalid AgentSkill catalog path")?;
    fs::create_dir_all(parent)?;
    let temp = parent.join(format!(
        ".pinget-catalog-{}-{}.tmp",
        std::process::id(),
        chrono::Utc::now().timestamp_nanos_opt().context("invalid clock time")?
    ));
    fs::write(&temp, bytes)?;
    if let Err(error) = fs::rename(&temp, path) {
        let _ = fs::remove_file(&temp);
        return Err(error).context("failed to replace AgentSkill catalog");
    }
    Ok(())
}

pub(super) fn load_sources(app_root: &Path) -> Result<Vec<SourceRecord>> {
    let path = catalog_store_path(app_root);
    if !path.exists() {
        return Ok(Vec::new());
    }
    let mut value: Value = serde_json::from_slice(&fs::read(path)?).context("invalid AgentSkill source store")?;
    catalog_source_keys(&mut value, false)?;
    let store: SourceStore = serde_json::from_value(value).context("invalid AgentSkill source store")?;
    let mut names = HashSet::new();
    for source in &store.sources {
        ensure!(
            source.kind == SourceKind::AgentSkills,
            "non-AgentSkill entry in AgentSkill source store"
        );
        catalog_url(&source.arg)?;
        ensure!(
            names.insert(source.name.to_ascii_lowercase()),
            "duplicate AgentSkill source name"
        );
    }
    Ok(store.sources)
}

pub(super) fn save_sources(app_root: &Path, sources: &[SourceRecord]) -> Result<()> {
    let mut value = serde_json::to_value(SourceStore {
        sources: sources.to_vec(),
    })?;
    catalog_source_keys(&mut value, true)?;
    let bytes = serde_json::to_vec_pretty(&value)?;
    write_atomically(&catalog_store_path(app_root), &bytes)
}

pub(super) fn catalog_url(value: &str) -> Result<reqwest::Url> {
    let url = https_url(value)?;
    ensure!(url.query().is_none(), "AgentSkill catalog URL cannot contain a query");
    Ok(url)
}

fn manifest_url(base: &reqwest::Url, value: &str) -> Result<reqwest::Url> {
    ensure!(
        !value.contains(['?', '#', '\\'])
            && (!value.contains(':') || value.starts_with("https://"))
            && !value.to_ascii_lowercase().contains("%2e")
            && !value.to_ascii_lowercase().contains("%2f")
            && !value.to_ascii_lowercase().contains("%5c")
            && !value.split('/').any(|part| part == "..")
            && !value.starts_with("//"),
        "AgentSkill catalog manifest URL contains a query, fragment, or unsafe path"
    );
    let url = base.join(value).context("invalid AgentSkill catalog manifest URL")?;
    https_url(url.as_str())?;
    Ok(url)
}

fn parse_index(bytes: &[u8], index_url: &str) -> Result<Catalog> {
    ensure!(bytes.len() as u64 <= MAX_INDEX, "AgentSkill catalog exceeds 1 MiB");
    let index: Catalog = serde_json::from_slice(bytes).context("invalid AgentSkill catalog JSON")?;
    ensure!(
        index.schema_version == 1,
        "unsupported AgentSkill catalog schemaVersion"
    );
    ensure!(
        index.packages.len() <= MAX_PACKAGES,
        "AgentSkill catalog exceeds 1000 packages"
    );
    let base = catalog_url(index_url)?;
    let mut identities = HashSet::new();
    for entry in &index.packages {
        ensure!(
            !entry.package_identifier.trim().is_empty()
                && !entry.package_version.trim().is_empty()
                && agent_skill::valid_name(&entry.package_name),
            "AgentSkill catalog contains an invalid package identity"
        );
        ensure!(
            identities.insert((
                entry.package_identifier.to_ascii_lowercase(),
                entry.package_version.to_ascii_lowercase(),
            )),
            "AgentSkill catalog contains duplicate package identity and version"
        );
        ensure!(
            entry.manifest_sha256.len() == 64 && entry.manifest_sha256.bytes().all(|byte| byte.is_ascii_hexdigit()),
            "AgentSkill catalog requires a 64-character manifest SHA-256"
        );
        manifest_url(&base, &entry.manifest_url)?;
    }
    Ok(index)
}

fn ensure_immutable_versions(previous: &Catalog, updated: &Catalog, index_url: &str) -> Result<()> {
    let base = catalog_url(index_url)?;
    for entry in &updated.packages {
        if let Some(old) = previous.packages.iter().find(|old| {
            old.package_identifier.eq_ignore_ascii_case(&entry.package_identifier)
                && old.package_version.eq_ignore_ascii_case(&entry.package_version)
        }) {
            ensure!(
                old.package_name == entry.package_name
                    && manifest_url(&base, &old.manifest_url)? == manifest_url(&base, &entry.manifest_url)?
                    && old.manifest_sha256.eq_ignore_ascii_case(&entry.manifest_sha256),
                "AgentSkill catalog changed an existing package version; publish a new version instead"
            );
        }
    }
    Ok(())
}

fn select<'a>(index: &'a Catalog, query: &PackageQuery) -> Result<Vec<&'a CatalogEntry>> {
    ensure!(
        query.moniker.is_none()
            && query.tag.is_none()
            && query.command.is_none()
            && query.channel.is_none()
            && query.locale.is_none()
            && query.installer_type.is_none()
            && query.installer_architecture.is_none()
            && query.platform.is_none()
            && query.os_version.is_none(),
        "installer and application-specific filters are not supported for AgentSkill catalogs"
    );
    let mut entries = index
        .packages
        .iter()
        .filter(|entry| {
            query.id.as_deref().is_none_or(|id| {
                if query.exact {
                    entry.package_identifier.eq_ignore_ascii_case(id)
                } else {
                    entry
                        .package_identifier
                        .to_ascii_lowercase()
                        .contains(&id.to_ascii_lowercase())
                }
            }) && query.name.as_deref().is_none_or(|name| {
                if query.exact {
                    entry.package_name.eq_ignore_ascii_case(name)
                } else {
                    entry
                        .package_name
                        .to_ascii_lowercase()
                        .contains(&name.to_ascii_lowercase())
                }
            }) && query.query.as_deref().is_none_or(|text| {
                let text = text.to_ascii_lowercase();
                entry.package_identifier.to_ascii_lowercase().contains(&text)
                    || entry.package_name.to_ascii_lowercase().contains(&text)
            }) && query
                .version
                .as_deref()
                .is_none_or(|version| entry.package_version.eq_ignore_ascii_case(version))
        })
        .collect::<Vec<_>>();
    entries.sort_by(|a, b| {
        a.package_identifier
            .to_ascii_lowercase()
            .cmp(&b.package_identifier.to_ascii_lowercase())
            .then_with(|| compare_version(&b.package_version, &a.package_version))
    });
    if query.version.is_none() {
        let mut names = HashSet::new();
        entries.retain(|entry| names.insert(entry.package_identifier.to_ascii_lowercase()));
    }
    Ok(entries)
}

fn manifest_from_entry(
    source: &SourceRecord,
    entry: &CatalogEntry,
    fetch: impl FnOnce(&reqwest::Url, u64) -> Result<Vec<u8>>,
) -> Result<SkillManifest> {
    let url = manifest_url(&catalog_url(&source.arg)?, &entry.manifest_url)?;
    let bytes = fetch(&url, MAX_MANIFEST)?;
    ensure!(bytes.len() as u64 <= MAX_MANIFEST, "AgentSkill manifest exceeds 1 MiB");
    ensure!(
        sha256_hex(&bytes).eq_ignore_ascii_case(&entry.manifest_sha256),
        "AgentSkill manifest SHA-256 does not match catalog"
    );
    let mut manifest =
        agent_skill::parse(&bytes)?.context("catalog entry does not contain a Pinget AgentSkill manifest")?;
    ensure!(
        manifest
            .package_identifier
            .eq_ignore_ascii_case(&entry.package_identifier)
            && manifest.package_version == entry.package_version
            && manifest.package_name == entry.package_name,
        "AgentSkill manifest identity does not match catalog entry"
    );
    manifest.catalog_source = Some(source.name.clone());
    manifest.catalog_manifest_sha256 = Some(entry.manifest_sha256.clone());
    Ok(manifest)
}

impl Repository {
    fn catalog_sources_for_query(&self, source: Option<&str>) -> Result<Vec<SourceRecord>> {
        let selected = self
            .skill_sources
            .iter()
            .filter(|record| {
                source.is_some_and(|name| record.name.eq_ignore_ascii_case(name))
                    || source.is_none() && !record.explicit
            })
            .cloned()
            .collect::<Vec<_>>();
        ensure!(
            !selected.is_empty(),
            "no Pinget AgentSkills source matched; add one with source add --type Pinget.AgentSkills"
        );
        Ok(selected)
    }

    fn load_agent_skill_catalog(&self, source: &SourceRecord) -> Result<Catalog> {
        let cache = catalog_cache_path(&self.app_root, source);
        let bytes = if cache.exists() {
            fs::read(&cache).with_context(|| format!("failed to read AgentSkill source '{}'", source.name))?
        } else {
            let data = download(&catalog_url(&source.arg)?, &self.user_agent, MAX_INDEX)
                .with_context(|| format!("failed to update AgentSkill source '{}'", source.name))?;
            parse_index(&data, &source.arg)?;
            write_atomically(&cache, &data)?;
            data
        };
        parse_index(&bytes, &source.arg)
    }

    pub(super) fn update_agent_skill_source(&mut self, name: &str) -> Result<SourceUpdateResult> {
        let index = self
            .skill_sources
            .iter()
            .position(|source| source.name.eq_ignore_ascii_case(name))
            .context("AgentSkill source not configured")?;
        let source = self.skill_sources[index].clone();
        let bytes = download(&catalog_url(&source.arg)?, &self.user_agent, MAX_INDEX)
            .with_context(|| format!("failed to update AgentSkill source '{}'", source.name))?;
        let catalog = parse_index(&bytes, &source.arg)?;
        let cache = catalog_cache_path(&self.app_root, &source);
        if cache.exists() {
            let previous = parse_index(&fs::read(&cache)?, &source.arg)?;
            ensure_immutable_versions(&previous, &catalog, &source.arg)?;
        }
        write_atomically(&cache, &bytes)?;
        self.skill_sources[index].last_update = Some(chrono::Utc::now());
        save_sources(&self.app_root, &self.skill_sources)?;
        Ok(SourceUpdateResult {
            name: source.name,
            kind: SourceKind::AgentSkills,
            detail: format!("Indexed {} AgentSkill versions", catalog.packages.len()),
        })
    }

    pub fn search_agent_skills(&self, query: &PackageQuery) -> Result<SearchResponse> {
        let mut matches = Vec::new();
        for source in self.catalog_sources_for_query(query.source.as_deref())? {
            for entry in select(&self.load_agent_skill_catalog(&source)?, query)? {
                matches.push(SearchMatch {
                    source_name: source.name.clone(),
                    source_kind: SourceKind::AgentSkills,
                    id: entry.package_identifier.clone(),
                    name: entry.package_name.clone(),
                    moniker: None,
                    version: Some(entry.package_version.clone()),
                    channel: None,
                    match_criteria: None,
                });
            }
        }
        let limit = max_results(query);
        let truncated = matches.len() > limit;
        matches.truncate(limit);
        Ok(SearchResponse {
            matches,
            warnings: Vec::new(),
            truncated,
        })
    }

    pub(super) fn resolve_agent_skill_from_catalog(&self, query: &PackageQuery) -> Result<SkillManifest> {
        let id = query.id.as_deref().context("catalog installation requires --id")?;
        let sources = self.catalog_sources_for_query(query.source.as_deref())?;
        let mut found = Vec::new();
        for source in sources {
            let index = self.load_agent_skill_catalog(&source)?;
            let entries = select(
                &index,
                &PackageQuery {
                    exact: true,
                    ..query.clone()
                },
            )?;
            for entry in entries {
                if entry.package_identifier.eq_ignore_ascii_case(id) {
                    found.push((source.clone(), entry.clone()));
                }
            }
        }
        ensure!(
            !found.is_empty(),
            "AgentSkill package or version not found in configured sources"
        );
        ensure!(
            found.len() == 1,
            "AgentSkill package matches multiple sources; specify --source"
        );
        let (source, entry) = &found[0];
        manifest_from_entry(source, entry, |url, limit| download(url, &self.user_agent, limit))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::RepositoryOptions;

    #[test]
    fn catalog_selects_pinned_version_and_checks_manifest_identity() {
        let body = b"ManifestType: pinget.agentSkill\nPingetManifestVersion: 1\nPackageIdentifier: Example.Demo\nPackageVersion: 1.2.0\nPackageName: demo\nAgentSkill:\n  Source:\n    Type: git\n    Url: https://github.com/example/skills.git\n    Commit: 0123456789abcdef0123456789abcdef01234567\n  SkillPath: skills/demo\n";
        let digest = sha256_hex(body);
        let index_bytes = format!(
            "{{\"schemaVersion\":1,\"packages\":[{{\"packageIdentifier\":\"Example.Demo\",\"packageVersion\":\"1.2.0\",\"packageName\":\"demo\",\"manifestUrl\":\"demo.yaml\",\"manifestSha256\":\"{digest}\"}}]}}"
        );
        let index = parse_index(index_bytes.as_bytes(), "https://example.org/index.json").expect("catalog");
        let query = PackageQuery {
            id: Some("Example.Demo".to_owned()),
            exact: true,
            ..PackageQuery::default()
        };
        let entry = select(&index, &query).expect("select").pop().expect("entry");
        let source = SourceRecord {
            name: "skills".to_owned(),
            kind: SourceKind::AgentSkills,
            arg: "https://example.org/index.json".to_owned(),
            identifier: "skills".to_owned(),
            trust_level: "None".to_owned(),
            explicit: false,
            priority: 0,
            last_update: None,
            source_version: None,
            etag: None,
            last_modified: None,
        };
        let manifest = manifest_from_entry(&source, entry, |url, limit| {
            assert_eq!(url.as_str(), "https://example.org/demo.yaml");
            assert_eq!(limit, MAX_MANIFEST);
            Ok(body.to_vec())
        })
        .expect("verify pinned manifest");
        assert_eq!(manifest.catalog_source.as_deref(), Some("skills"));
        assert_eq!(manifest.catalog_manifest_sha256.as_deref(), Some(digest.as_str()));
        assert!(manifest_from_entry(&source, entry, |_, _| Ok(b"changed".to_vec())).is_err());
        let root = std::env::temp_dir().join(format!("pinget-catalog-test-{}", std::process::id()));
        let mut repo = Repository::open_with_options(RepositoryOptions::new(&root)).expect("open test repository");
        write_atomically(&catalog_cache_path(&root, &source), index_bytes.as_bytes()).expect("cache catalog");
        assert!(
            repo.search_agent_skills(&query).is_err(),
            "unconfigured catalogs are not searched"
        );
        assert!(
            repo.add_source(
                "tokenized",
                "https://example.org/index.json?token=secret",
                SourceKind::AgentSkills
            )
            .is_err(),
            "a catalog source with a query must not be stored"
        );
        repo.add_source("skills", &source.arg, SourceKind::AgentSkills)
            .expect("configure skill catalog");
        let matches = repo.search_agent_skills(&query).expect("search cached catalog");
        assert_eq!(matches.matches.len(), 1);
        assert_eq!(matches.matches[0].name, "demo");
        write_atomically(&catalog_cache_path(&root, &source), index_bytes.as_bytes())
            .expect("replace cached catalog on update");
        drop(repo);
        let reopened = Repository::open_with_options(RepositoryOptions::new(&root)).expect("reopen repository");
        assert_eq!(
            reopened
                .search_agent_skills(&query)
                .expect("search persisted source")
                .matches
                .len(),
            1
        );
        drop(reopened);
        fs::remove_dir_all(&root).expect("remove fixture");
    }

    #[test]
    fn catalog_rejects_duplicates_and_insecure_manifests() {
        let entry = r#"{"packageIdentifier":"Example.Demo","packageVersion":"1.0","packageName":"demo","manifestUrl":"http://example.org/x.yaml","manifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"#;
        let body = format!(r#"{{"schemaVersion":1,"packages":[{entry}]}}"#);
        assert!(parse_index(body.as_bytes(), "https://example.org/index.json").is_err());
        let safe = body.replace("http://example.org/x.yaml", "x.yaml");
        let safe_entry = entry.replace("http://example.org/x.yaml", "x.yaml");
        let duplicate = safe.replace(&safe_entry, &format!("{safe_entry},{safe_entry}"));
        assert!(parse_index(duplicate.as_bytes(), "https://example.org/index.json").is_err());
        assert!(parse_index(safe.as_bytes(), "https://example.org/index.json?token=secret").is_err());
        for unsafe_url in [
            "../x.yaml",
            "%2e%2e/x.yaml",
            "x.yaml?token=secret",
            "//other.example/x.yaml",
            "https:x.yaml",
        ] {
            assert!(
                parse_index(
                    safe.replace("x.yaml", unsafe_url).as_bytes(),
                    "https://example.org/index.json"
                )
                .is_err(),
                "{unsafe_url}"
            );
        }
        let before = parse_index(safe.as_bytes(), "https://example.org/index.json").expect("original catalog");
        let changed = safe.replace("x.yaml", "renamed.yaml");
        let after = parse_index(changed.as_bytes(), "https://example.org/index.json").expect("updated catalog");
        assert!(ensure_immutable_versions(&before, &after, "https://example.org/index.json").is_err());
        let newer = safe.replace(
            &safe_entry,
            &format!("{safe_entry},{}", safe_entry.replace("\"1.0\"", "\"1.1\"")),
        );
        let next = parse_index(newer.as_bytes(), "https://example.org/index.json").expect("new catalog version");
        ensure_immutable_versions(&before, &next, "https://example.org/index.json")
            .expect("adding a version is allowed");
        let canonical = safe.replace("x.yaml", "https://example.org/x.yaml");
        let after_csharp_cache =
            parse_index(canonical.as_bytes(), "https://example.org/index.json").expect("normalized C# cache");
        ensure_immutable_versions(&before, &after_csharp_cache, "https://example.org/index.json")
            .expect("normalizing a relative URL to its absolute URL preserves the pin");
        let query = PackageQuery {
            id: Some("Example.Demo".to_owned()),
            ..PackageQuery::default()
        };
        assert_eq!(select(&next, &query).expect("latest version")[0].package_version, "1.1");
        let pinned = PackageQuery {
            version: Some("1.0".to_owned()),
            ..query
        };
        assert_eq!(
            select(&next, &pinned).expect("pinned version")[0].package_version,
            "1.0"
        );
    }

    #[test]
    fn source_store_preserves_csharp_metadata_on_round_trip() {
        let root = std::env::temp_dir().join(format!(
            "pinget-skill-store-{}-{}",
            std::process::id(),
            chrono::Utc::now().timestamp_nanos_opt().expect("clock time")
        ));
        fs::create_dir_all(&root).expect("create fixture");
        let source = r#"{"sources":[{"name":"skills","kind":"AgentSkills","arg":"https://example.org/index.json","identifier":"skills","trustLevel":"Trusted","explicit":true,"priority":7,"lastUpdate":"2026-01-01T00:00:00Z","sourceVersion":"1","eTag":"tag","lastModified":"Wed, 01 Jan 2026 00:00:00 GMT"}]}"#;
        fs::write(catalog_store_path(&root), source).expect("write C# source store");
        let sources = load_sources(&root).expect("load C# source store");
        assert_eq!(sources[0].trust_level, "Trusted");
        assert_eq!(sources[0].etag.as_deref(), Some("tag"));
        assert_eq!(sources[0].last_update.expect("update date").timestamp(), 1767225600);
        save_sources(&root, &sources).expect("save source store");
        let stored: Value = serde_json::from_slice(&fs::read(catalog_store_path(&root)).expect("read store"))
            .expect("parse source store");
        let saved = &stored["sources"][0];
        assert_eq!(saved["sourceVersion"], "1");
        assert_eq!(saved["eTag"], "tag");
        assert_eq!(saved["lastModified"], "Wed, 01 Jan 2026 00:00:00 GMT");
        assert!(saved.get("source_version").is_none());
        fs::remove_dir_all(root).expect("remove fixture");
    }
}
