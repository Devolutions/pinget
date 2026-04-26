use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet, HashSet};
use std::fmt::{Display, Formatter};
use std::fs;
use std::io::{Cursor, Read};
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, anyhow, bail};
use chrono::{DateTime, Duration, Utc};
use reqwest::blocking::{Client, Response};
use rusqlite::types::{Value as SqlValue, ValueRef};
use rusqlite::{Connection, OpenFlags, Row as SqlRow, params_from_iter};
use serde::{Deserialize, Serialize};
use serde_json::{Map as JsonMap, Value as JsonValue};
use serde_yaml::{Mapping as YamlMapping, Value as YamlValue};
use sha2::{Digest, Sha256};
#[cfg(windows)]
use std::os::windows::ffi::OsStringExt;
#[cfg(windows)]
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, LocalFree};
#[cfg(windows)]
use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
#[cfg(windows)]
use windows_sys::Win32::Security::{GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser};
#[cfg(windows)]
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
#[cfg(windows)]
use winreg::{RegKey, enums::*};
use zip::ZipArchive;

const DEFAULT_MARKET: &str = "US";
const DEFAULT_MAX_RESULTS: usize = 50;
const LIST_LOOKUP_MAX_RESULTS: usize = 500;
const PREINDEXED_CANDIDATES: &[&str] = &["source2.msix", "source.msix"];
const DEFAULT_USER_AGENT: &str = "pinget-rs/0.1";
#[cfg(windows)]
const PACKAGED_FAMILY_NAME: &str = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
#[cfg(windows)]
const PACKAGED_NAME: &str = "Microsoft.DesktopAppInstaller";
const INSTALLED_STATE_UNSUPPORTED_WARNING: &str =
    "Installed package discovery is not supported on this platform; returning no installed packages.";
const INSTALL_UNSUPPORTED_WARNING: &str =
    "Installing packages is not supported on this platform; no changes were made.";
const REPAIR_UNSUPPORTED_WARNING: &str = "Repairing packages is not supported on this platform; no changes were made.";
const REPAIR_REINSTALL_WARNING: &str =
    "Pinget repair currently re-runs the package install flow for the selected package.";
const UNINSTALL_UNSUPPORTED_WARNING: &str =
    "Uninstalling packages is not supported on this platform; no changes were made.";
#[cfg(windows)]
const WINGET_PACKAGE_NOT_FOUND_EXIT_CODE: i32 = -1978335212;
const SUPPORTED_ADMIN_SETTINGS: &[&str] = &[
    "LocalManifestFiles",
    "BypassCertificatePinningForMicrosoftStore",
    "InstallerHashOverride",
    "LocalArchiveMalwareScanOverride",
    "ProxyCommandLineOptions",
];
const REST_SUPPORTED_CONTRACTS: &[&str] = &[
    "1.12.0", "1.10.0", "1.9.0", "1.7.0", "1.6.0", "1.5.0", "1.4.0", "1.1.0", "1.0.0",
];

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
pub enum SourceKind {
    #[serde(rename = "preIndexed", alias = "PreIndexed")]
    PreIndexed,
    #[serde(rename = "rest", alias = "Rest")]
    Rest,
}

impl Display for SourceKind {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            SourceKind::PreIndexed => f.write_str("preindexed"),
            SourceKind::Rest => f.write_str("rest"),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceRecord {
    pub name: String,
    pub kind: SourceKind,
    pub arg: String,
    pub identifier: String,
    #[serde(default = "default_source_trust_level")]
    pub trust_level: String,
    #[serde(default)]
    pub explicit: bool,
    #[serde(default)]
    pub priority: i32,
    #[serde(default)]
    pub last_update: Option<DateTime<Utc>>,
    #[serde(default)]
    pub source_version: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct SourceStore {
    sources: Vec<SourceRecord>,
}

impl Default for SourceStore {
    fn default() -> Self {
        Self {
            sources: vec![
                SourceRecord {
                    name: "winget".to_owned(),
                    kind: SourceKind::PreIndexed,
                    arg: "https://cdn.winget.microsoft.com/cache".to_owned(),
                    identifier: "Microsoft.Winget.Source_8wekyb3d8bbwe".to_owned(),
                    trust_level: "Trusted".to_owned(),
                    explicit: false,
                    priority: 0,
                    last_update: None,
                    source_version: None,
                },
                SourceRecord {
                    name: "msstore".to_owned(),
                    kind: SourceKind::Rest,
                    arg: "https://storeedgefd.dsx.mp.microsoft.com/v9.0".to_owned(),
                    identifier: "StoreEdgeFD".to_owned(),
                    trust_level: "Trusted".to_owned(),
                    explicit: false,
                    priority: 0,
                    last_update: None,
                    source_version: None,
                },
            ],
        }
    }
}

/// Options for embedding the WinGet core library in another application.
#[derive(Debug, Clone)]
pub struct RepositoryOptions {
    pub app_root: PathBuf,
    pub user_agent: String,
}

impl RepositoryOptions {
    /// Creates library options that keep all persistent state under the supplied root.
    pub fn new(app_root: impl Into<PathBuf>) -> Self {
        Self {
            app_root: app_root.into(),
            user_agent: DEFAULT_USER_AGENT.to_owned(),
        }
    }

    /// Uses the default per-user app-data root that the CLI also uses.
    pub fn for_current_user() -> Result<Self> {
        Ok(Self::new(default_app_root()?))
    }

    /// Overrides the HTTP user-agent sent to source endpoints.
    #[must_use]
    pub fn with_user_agent(mut self, user_agent: impl Into<String>) -> Self {
        self.user_agent = user_agent.into();
        self
    }
}

#[derive(Debug, Clone, Default)]
pub struct PackageQuery {
    pub query: Option<String>,
    pub id: Option<String>,
    pub name: Option<String>,
    pub moniker: Option<String>,
    pub tag: Option<String>,
    pub command: Option<String>,
    pub source: Option<String>,
    pub count: Option<usize>,
    pub exact: bool,
    pub version: Option<String>,
    pub channel: Option<String>,
    pub locale: Option<String>,
    pub installer_type: Option<String>,
    pub installer_architecture: Option<String>,
    pub platform: Option<String>,
    pub os_version: Option<String>,
    pub install_scope: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct ListQuery {
    pub query: Option<String>,
    pub id: Option<String>,
    pub name: Option<String>,
    pub moniker: Option<String>,
    pub tag: Option<String>,
    pub command: Option<String>,
    pub product_code: Option<String>,
    pub version: Option<String>,
    pub source: Option<String>,
    pub count: Option<usize>,
    pub exact: bool,
    pub install_scope: Option<String>,
    pub upgrade_only: bool,
    pub include_unknown: bool,
    pub include_pinned: bool,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SearchMatch {
    pub source_name: String,
    pub source_kind: SourceKind,
    pub id: String,
    pub name: String,
    pub moniker: Option<String>,
    pub version: Option<String>,
    pub channel: Option<String>,
    pub match_criteria: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SearchResponse {
    pub matches: Vec<SearchMatch>,
    pub warnings: Vec<String>,
    pub truncated: bool,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct ListMatch {
    pub name: String,
    pub id: String,
    pub local_id: String,
    pub installed_version: String,
    pub available_version: Option<String>,
    pub source_name: Option<String>,
    pub publisher: Option<String>,
    pub scope: Option<String>,
    pub installer_category: Option<String>,
    pub install_location: Option<String>,
    pub package_family_names: Vec<String>,
    pub product_codes: Vec<String>,
    pub upgrade_codes: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct ListResponse {
    pub matches: Vec<ListMatch>,
    pub warnings: Vec<String>,
    pub truncated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct VersionKey {
    pub version: String,
    pub channel: String,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct Manifest {
    pub id: String,
    pub name: String,
    pub version: String,
    pub channel: String,
    pub publisher: Option<String>,
    pub description: Option<String>,
    pub moniker: Option<String>,
    pub package_url: Option<String>,
    pub publisher_url: Option<String>,
    pub publisher_support_url: Option<String>,
    pub license: Option<String>,
    pub license_url: Option<String>,
    pub privacy_url: Option<String>,
    pub author: Option<String>,
    pub copyright: Option<String>,
    pub copyright_url: Option<String>,
    pub release_notes: Option<String>,
    pub release_notes_url: Option<String>,
    pub tags: Vec<String>,
    pub agreements: Vec<PackageAgreement>,
    pub package_dependencies: Vec<String>,
    pub documentation: Vec<Documentation>,
    pub installers: Vec<Installer>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct Documentation {
    pub label: Option<String>,
    pub url: String,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct PackageAgreement {
    pub label: Option<String>,
    pub text: Option<String>,
    pub url: Option<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct Installer {
    pub architecture: Option<String>,
    pub installer_type: Option<String>,
    pub url: Option<String>,
    pub sha256: Option<String>,
    pub product_code: Option<String>,
    pub locale: Option<String>,
    pub scope: Option<String>,
    pub release_date: Option<String>,
    pub package_family_name: Option<String>,
    pub upgrade_code: Option<String>,
    pub platforms: Vec<String>,
    pub minimum_os_version: Option<String>,
    pub switches: InstallerSwitches,
    pub commands: Vec<String>,
    pub package_dependencies: Vec<String>,
}

#[derive(Debug, Clone, Default, serde::Serialize)]
pub struct InstallerSwitches {
    pub silent: Option<String>,
    pub silent_with_progress: Option<String>,
    pub interactive: Option<String>,
    pub custom: Option<String>,
    pub log: Option<String>,
    pub install_location: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallerMode {
    Interactive,
    SilentWithProgress,
    Silent,
}

#[derive(Debug, Clone)]
pub struct InstallRequest {
    pub query: PackageQuery,
    pub manifest_path: Option<PathBuf>,
    pub mode: InstallerMode,
    pub log_path: Option<PathBuf>,
    pub custom: Option<String>,
    pub override_args: Option<String>,
    pub install_location: Option<String>,
    pub skip_dependencies: bool,
    pub dependencies_only: bool,
    pub accept_package_agreements: bool,
    pub force: bool,
    pub rename: Option<String>,
    pub uninstall_previous: bool,
    pub ignore_security_hash: bool,
    pub dependency_source: Option<String>,
    pub no_upgrade: bool,
}

impl InstallRequest {
    pub fn new(query: PackageQuery) -> Self {
        Self {
            query,
            manifest_path: None,
            mode: InstallerMode::SilentWithProgress,
            log_path: None,
            custom: None,
            override_args: None,
            install_location: None,
            skip_dependencies: false,
            dependencies_only: false,
            accept_package_agreements: false,
            force: false,
            rename: None,
            uninstall_previous: false,
            ignore_security_hash: false,
            dependency_source: None,
            no_upgrade: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct UninstallRequest {
    pub query: PackageQuery,
    pub manifest_path: Option<PathBuf>,
    pub product_code: Option<String>,
    pub mode: InstallerMode,
    pub all_versions: bool,
    pub force: bool,
    pub purge: bool,
    pub preserve: bool,
    pub log_path: Option<PathBuf>,
}

impl UninstallRequest {
    pub fn new(query: PackageQuery) -> Self {
        Self {
            query,
            manifest_path: None,
            product_code: None,
            mode: InstallerMode::SilentWithProgress,
            all_versions: false,
            force: false,
            purge: false,
            preserve: false,
            log_path: None,
        }
    }
}

#[derive(Debug, Clone)]
pub struct RepairRequest {
    pub query: PackageQuery,
    pub manifest_path: Option<PathBuf>,
    pub product_code: Option<String>,
    pub mode: InstallerMode,
    pub log_path: Option<PathBuf>,
    pub accept_package_agreements: bool,
    pub force: bool,
    pub ignore_security_hash: bool,
}

impl RepairRequest {
    pub fn new(query: PackageQuery) -> Self {
        Self {
            query,
            manifest_path: None,
            product_code: None,
            mode: InstallerMode::SilentWithProgress,
            log_path: None,
            accept_package_agreements: false,
            force: false,
            ignore_security_hash: false,
        }
    }
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct ShowResult {
    pub package: SearchMatch,
    pub manifest: Manifest,
    pub selected_installer: Option<Installer>,
    pub cached_files: Vec<PathBuf>,
    pub warnings: Vec<String>,
    #[serde(skip_serializing)]
    manifest_documents: JsonValue,
}

impl ShowResult {
    pub fn structured_document(&self) -> JsonValue {
        collapse_structured_document(&self.manifest_documents)
    }
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct VersionsResult {
    pub package: SearchMatch,
    pub versions: Vec<VersionKey>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct CacheWarmResult {
    pub package: SearchMatch,
    pub cached_files: Vec<PathBuf>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct SourceUpdateResult {
    pub name: String,
    pub kind: SourceKind,
    pub detail: String,
}

#[derive(Debug, Clone, serde::Serialize)]
pub struct PinRecord {
    pub package_id: String,
    pub version: String,
    pub source_id: String,
    pub pin_type: PinType,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
pub enum PinType {
    Pinning,
    Blocking,
    Gating,
}

impl Display for PinType {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            PinType::Pinning => f.write_str("Pinning"),
            PinType::Blocking => f.write_str("Blocking"),
            PinType::Gating => f.write_str("Gating"),
        }
    }
}

/// Result of an install/uninstall/upgrade operation
#[derive(Debug, Clone, serde::Serialize)]
pub struct InstallResult {
    pub package_id: String,
    pub version: String,
    pub installer_path: PathBuf,
    pub installer_type: String,
    pub exit_code: i32,
    pub success: bool,
    pub no_op: bool,
    pub warnings: Vec<String>,
}

impl InstallerSwitches {
    fn with_fallback(&self, fallback: &Self) -> Self {
        Self {
            silent: self.silent.clone().or_else(|| fallback.silent.clone()),
            silent_with_progress: self
                .silent_with_progress
                .clone()
                .or_else(|| fallback.silent_with_progress.clone()),
            interactive: self.interactive.clone().or_else(|| fallback.interactive.clone()),
            custom: self.custom.clone().or_else(|| fallback.custom.clone()),
            log: self.log.clone().or_else(|| fallback.log.clone()),
            install_location: self
                .install_location
                .clone()
                .or_else(|| fallback.install_location.clone()),
        }
    }
}

fn installed_package_discovery_supported() -> bool {
    cfg!(windows)
}

fn package_actions_supported() -> bool {
    cfg!(windows)
}

fn unsupported_action_result(
    package_id: impl Into<String>,
    version: impl Into<String>,
    installer_type: impl Into<String>,
    warning: &str,
) -> InstallResult {
    InstallResult {
        package_id: package_id.into(),
        version: version.into(),
        installer_path: PathBuf::new(),
        installer_type: installer_type.into(),
        exit_code: 0,
        success: true,
        no_op: true,
        warnings: vec![warning.to_owned()],
    }
}

#[derive(Debug, Clone)]
struct InstalledPackage {
    name: String,
    local_id: String,
    installed_version: String,
    publisher: Option<String>,
    scope: Option<String>,
    installer_category: Option<String>,
    install_location: Option<String>,
    package_family_names: Vec<String>,
    product_codes: Vec<String>,
    upgrade_codes: Vec<String>,
    correlated: Option<SearchMatch>,
}

#[derive(Debug, Clone)]
struct LocatedMatch {
    display: SearchMatch,
    source_index: usize,
    locator: MatchLocator,
}

#[derive(Debug)]
struct SearchSourceMatches {
    matches: Vec<LocatedMatch>,
    truncated: bool,
}

#[derive(Debug, Clone)]
enum MatchLocator {
    PreIndexedV1 {
        package_rowid: i64,
    },
    PreIndexedV2 {
        package_rowid: i64,
        package_hash: String,
    },
    Rest {
        package_id: String,
        versions: Vec<VersionKey>,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SearchSemantics {
    Many,
    Single,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct RestInfoCache {
    expires_at: DateTime<Utc>,
    value: RestInformation,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
struct RestInformation {
    #[serde(rename = "SourceIdentifier")]
    source_identifier: String,
    #[serde(rename = "ServerSupportedVersions", default)]
    server_supported_versions: Vec<String>,
    #[serde(rename = "RequiredPackageMatchFields", default)]
    required_package_match_fields: Vec<String>,
    #[serde(rename = "UnsupportedPackageMatchFields", default)]
    unsupported_package_match_fields: Vec<String>,
    #[serde(rename = "RequiredQueryParameters", default)]
    required_query_parameters: Vec<String>,
    #[serde(rename = "UnsupportedQueryParameters", default)]
    unsupported_query_parameters: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
struct PackageVersionDataDocument {
    #[serde(rename = "vD", default)]
    versions: Vec<PackageVersionDataEntry>,
}

#[derive(Debug, Clone, Deserialize)]
struct PackageVersionDataEntry {
    #[serde(rename = "v")]
    version: String,
    #[serde(rename = "rP")]
    manifest_relative_path: String,
    #[serde(rename = "s256H")]
    manifest_hash: String,
}

pub struct Repository {
    app_root: PathBuf,
    client: Client,
    store: SourceStore,
}

impl Repository {
    /// Opens the repository with the default per-user CLI storage layout.
    pub fn open() -> Result<Self> {
        Self::open_with_options(RepositoryOptions::for_current_user()?)
    }

    /// Opens the repository with explicit hosting options for library consumers.
    pub fn open_with_options(options: RepositoryOptions) -> Result<Self> {
        ensure_app_dirs(&options.app_root)?;
        let store = load_store(&options.app_root)?;
        let client = Client::builder()
            .user_agent(&options.user_agent)
            .build()
            .context("failed to build HTTP client")?;
        Ok(Self {
            app_root: options.app_root,
            client,
            store,
        })
    }

    pub fn app_root(&self) -> &Path {
        &self.app_root
    }

    pub fn list_sources(&self) -> Vec<SourceRecord> {
        self.store.sources.clone()
    }

    pub fn add_source(&mut self, name: &str, arg: &str, kind: SourceKind) -> Result<()> {
        self.add_source_with_metadata(name, arg, kind, None, false, 0)
    }

    pub fn add_source_with_metadata(
        &mut self,
        name: &str,
        arg: &str,
        kind: SourceKind,
        trust_level: Option<&str>,
        explicit: bool,
        priority: i32,
    ) -> Result<()> {
        if self.store.sources.iter().any(|s| s.name.eq_ignore_ascii_case(name)) {
            bail!("A source with name '{name}' already exists.");
        }
        if self.store.sources.iter().any(|s| s.arg == arg) {
            bail!("A source with argument '{arg}' already exists.");
        }
        let identifier = name.to_owned();
        self.store.sources.push(SourceRecord {
            name: name.to_owned(),
            kind,
            arg: arg.to_owned(),
            identifier,
            trust_level: normalize_source_trust_level(trust_level)?,
            explicit,
            priority,
            last_update: None,
            source_version: None,
        });
        self.save_store()?;
        Ok(())
    }

    pub fn edit_source(&mut self, name: &str, explicit: Option<bool>, trust_level: Option<&str>) -> Result<()> {
        let source = self
            .store
            .sources
            .iter_mut()
            .find(|source| source.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| anyhow!("Source '{name}' not found."))?;

        if let Some(explicit) = explicit {
            source.explicit = explicit;
        }

        if trust_level.is_some() {
            source.trust_level = normalize_source_trust_level(trust_level)?;
        }

        self.save_store()?;
        Ok(())
    }

    pub fn remove_source(&mut self, name: &str) -> Result<()> {
        let idx = self
            .store
            .sources
            .iter()
            .position(|s| s.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| anyhow!("Source '{name}' not found."))?;
        let source = self.store.sources.remove(idx);
        // Clean up cached state for this source
        let state_dir = self.source_state_dir(&source);
        if state_dir.exists() {
            let _ = fs::remove_dir_all(&state_dir);
        }
        self.save_store()?;
        Ok(())
    }

    pub fn reset_source(&mut self, name: &str) -> Result<()> {
        let idx = self
            .store
            .sources
            .iter()
            .position(|source| source.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| anyhow!("Source '{name}' not found."))?;

        let source = self.store.sources[idx].clone();
        let state_dir = self.source_state_dir(&source);
        if state_dir.exists() {
            let _ = fs::remove_dir_all(&state_dir);
        }

        if let Some(default_source) = SourceStore::default()
            .sources
            .into_iter()
            .find(|source| source.name.eq_ignore_ascii_case(name))
        {
            self.store.sources[idx] = default_source;
        } else {
            self.store.sources[idx].last_update = None;
            self.store.sources[idx].source_version = None;
        }

        self.save_store()?;
        Ok(())
    }

    pub fn reset_sources(&mut self) -> Result<()> {
        // Remove cached state for all current sources
        for source in &self.store.sources {
            let state_dir = self.source_state_dir(source);
            if state_dir.exists() {
                let _ = fs::remove_dir_all(&state_dir);
            }
        }
        self.store = SourceStore::default();
        self.save_store()?;
        Ok(())
    }

    pub fn supported_admin_settings() -> &'static [&'static str] {
        SUPPORTED_ADMIN_SETTINGS
    }

    pub fn get_user_settings(&self) -> Result<JsonValue> {
        Ok(JsonValue::Object(load_json_object(&user_settings_path(
            &self.app_root,
        ))?))
    }

    pub fn set_user_settings(&self, user_settings: &JsonValue, merge: bool) -> Result<JsonValue> {
        let update = match user_settings {
            JsonValue::Object(object) => object.clone(),
            _ => bail!("user settings must be a JSON object"),
        };

        let effective = if merge {
            merge_json_objects(&load_json_object(&user_settings_path(&self.app_root))?, &update)
        } else {
            update
        };
        save_json_object(user_settings_path(&self.app_root), &effective)?;
        Ok(JsonValue::Object(effective))
    }

    pub fn test_user_settings(&self, expected: &JsonValue, ignore_not_set: bool) -> Result<bool> {
        let current = self.get_user_settings()?;
        Ok(if ignore_not_set {
            json_contains(&current, expected)
        } else {
            current == *expected
        })
    }

    pub fn get_admin_settings(&self) -> Result<JsonValue> {
        let mut settings = load_json_object(&admin_settings_path(&self.app_root))?;
        for name in SUPPORTED_ADMIN_SETTINGS {
            settings.entry((*name).to_owned()).or_insert(JsonValue::Bool(false));
        }

        Ok(JsonValue::Object(settings))
    }

    pub fn set_admin_setting(&self, name: &str, enabled: bool) -> Result<()> {
        let normalized = normalize_admin_setting_name(name)?;
        let mut settings = match self.get_admin_settings()? {
            JsonValue::Object(settings) => settings,
            _ => JsonMap::new(),
        };
        settings.insert(normalized.to_owned(), JsonValue::Bool(enabled));
        save_json_object(admin_settings_path(&self.app_root), &settings)
    }

    pub fn reset_admin_setting(&self, name: Option<&str>, reset_all: bool) -> Result<()> {
        if !reset_all && name.is_none() {
            bail!("resetting admin settings requires a setting name or reset-all");
        }

        let mut settings = match self.get_admin_settings()? {
            JsonValue::Object(settings) => settings,
            _ => JsonMap::new(),
        };

        if reset_all {
            for setting_name in SUPPORTED_ADMIN_SETTINGS {
                settings.insert((*setting_name).to_owned(), JsonValue::Bool(false));
            }
        } else {
            settings.insert(
                normalize_admin_setting_name(name.expect("checked above"))?.to_owned(),
                JsonValue::Bool(false),
            );
        }

        save_json_object(admin_settings_path(&self.app_root), &settings)
    }

    pub fn ensure_settings_files(&self) -> Result<()> {
        let user_settings = user_settings_path(&self.app_root);
        if !user_settings.exists() {
            save_json_object(user_settings, &JsonMap::new())?;
        }

        let admin_settings = admin_settings_path(&self.app_root);
        if !admin_settings.exists() {
            let mut values = JsonMap::new();
            for name in SUPPORTED_ADMIN_SETTINGS {
                values.insert((*name).to_owned(), JsonValue::Bool(false));
            }
            save_json_object(admin_settings, &values)?;
        }

        Ok(())
    }

    pub fn update_sources(&mut self, source_name: Option<&str>) -> Result<Vec<SourceUpdateResult>> {
        let indexes = self.resolve_source_indexes(source_name)?;
        let mut results = Vec::new();

        for index in indexes {
            let detail = match self.store.sources[index].kind {
                SourceKind::PreIndexed => self.update_preindexed(index)?,
                SourceKind::Rest => self.update_rest(index)?,
            };

            results.push(SourceUpdateResult {
                name: self.store.sources[index].name.clone(),
                kind: self.store.sources[index].kind,
                detail,
            });
        }

        self.save_store()?;
        Ok(results)
    }

    pub fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse> {
        let (matches, warnings, truncated) = self.search_located(query, SearchSemantics::Many)?;
        Ok(SearchResponse {
            matches: matches.into_iter().map(|item| item.display).collect(),
            warnings,
            truncated,
        })
    }

    pub fn search_manifests(&mut self, query: &PackageQuery) -> Result<Vec<JsonValue>> {
        let (matches, _, _) = self.search_located(query, SearchSemantics::Many)?;
        let mut structured_documents = Vec::with_capacity(matches.len());
        for located in matches {
            let (_, manifest_documents, _) = self.manifest_for_match(&located, query)?;
            structured_documents.push(manifest_documents);
        }

        Ok(collapse_structured_documents(&structured_documents))
    }

    pub fn list(&mut self, query: &ListQuery) -> Result<ListResponse> {
        if (query.include_unknown || query.include_pinned) && !query.upgrade_only {
            bail!("--include-unknown and --include-pinned require --upgrade-available");
        }
        if query.source.is_some()
            && query.query.is_none()
            && query.id.is_none()
            && query.name.is_none()
            && query.moniker.is_none()
            && query.tag.is_none()
            && query.command.is_none()
        {
            bail!("list --source currently requires a query or explicit filter");
        }

        let has_filter = list_query_needs_available_lookup(query);
        let needs_available = has_filter || query.upgrade_only;

        let mut warnings = Vec::new();
        if !installed_package_discovery_supported() {
            warnings.push(INSTALLED_STATE_UNSUPPORTED_WARNING.to_owned());
        }
        let mut installed = collect_installed_packages(query.install_scope.as_deref())?;

        if needs_available && has_filter {
            // Filtered lookup: search sources with the user's query
            let available_query = package_query_from_list_query(query);
            let (matches, source_warnings, _) = self.search_located(&available_query, SearchSemantics::Many)?;
            warnings.extend(source_warnings);
            let candidates: Vec<SearchMatch> = matches.into_iter().map(|c| c.display).collect();
            for package in &mut installed {
                package.correlated =
                    correlate_installed_package(package, &candidates, allow_loose_list_correlation(query));
            }
        } else if needs_available {
            // Unfiltered upgrade: look up each installed package by its correlation names
            warnings.extend(self.correlate_all_installed(&mut installed)?);
        }

        let mut matches = installed
            .into_iter()
            .filter(|package| {
                list_package_matches(package, query)
                    && (!query.upgrade_only || installed_package_matches_upgrade_filter(package, query))
            })
            .collect::<Vec<_>>();

        matches.sort_by(|left, right| {
            list_sort_weight(left)
                .cmp(&list_sort_weight(right))
                .then_with(|| left.name.to_ascii_lowercase().cmp(&right.name.to_ascii_lowercase()))
                .then_with(|| left.local_id.cmp(&right.local_id))
        });

        let pins = if query.upgrade_only {
            self.list_pins(query.source.as_deref())?
        } else {
            Vec::new()
        };
        let mut list_matches = matches
            .into_iter()
            .map(list_match_from_installed)
            .filter(|item| !query.upgrade_only || query.include_pinned || !is_upgrade_blocked_by_pin(item, &pins))
            .collect::<Vec<_>>();
        let truncated = if let Some(limit) = query.count {
            let was_truncated = list_matches.len() > limit;
            list_matches.truncate(limit);
            was_truncated
        } else {
            false
        };

        Ok(ListResponse {
            matches: list_matches,
            warnings,
            truncated,
        })
    }

    /// For unfiltered upgrade/list, search the entire available index and correlate
    /// against all installed packages.
    fn correlate_all_installed(&mut self, installed: &mut [InstalledPackage]) -> Result<Vec<String>> {
        let all_query = PackageQuery {
            query: None,
            id: None,
            name: None,
            moniker: None,
            tag: None,
            command: None,
            source: None,
            count: Some(100_000), // fetch the entire index
            exact: false,
            version: None,
            channel: None,
            locale: None,
            installer_type: None,
            installer_architecture: None,
            platform: None,
            os_version: None,
            install_scope: None,
        };
        let (matches, warnings, _) = self.search_located(&all_query, SearchSemantics::Many)?;
        let candidates: Vec<SearchMatch> = matches.into_iter().map(|c| c.display).collect();

        for package in installed.iter_mut() {
            package.correlated = correlate_installed_package(package, &candidates, true);
        }

        Ok(warnings)
    }

    pub fn search_versions(&mut self, query: &PackageQuery) -> Result<VersionsResult> {
        let (located, warnings) = self.find_single_match_with_semantics(query, SearchSemantics::Many)?;
        let versions = self.versions_for_match(&located, query)?;
        Ok(VersionsResult {
            package: located.display,
            versions,
            warnings,
        })
    }

    pub fn show_versions(&mut self, query: &PackageQuery) -> Result<VersionsResult> {
        let (located, warnings) = self.find_single_match(query)?;
        let versions = self.versions_for_match(&located, query)?;
        Ok(VersionsResult {
            package: located.display,
            versions,
            warnings,
        })
    }

    pub fn show(&mut self, query: &PackageQuery) -> Result<ShowResult> {
        let (located, warnings) = self.find_single_match(query)?;
        let (manifest, manifest_documents, cached_files) = self.manifest_for_match(&located, query)?;
        let selected_installer = select_installer(&manifest.installers, query);

        Ok(ShowResult {
            package: located.display,
            manifest,
            selected_installer,
            cached_files,
            warnings,
            manifest_documents,
        })
    }

    pub fn warm_cache(&mut self, query: &PackageQuery) -> Result<CacheWarmResult> {
        let (located, warnings) = self.find_single_match(query)?;
        let (_, _, cached_files) = self.manifest_for_match(&located, query)?;

        Ok(CacheWarmResult {
            package: located.display,
            cached_files,
            warnings,
        })
    }

    // ── Pin management ──

    pub fn list_pins(&self, source_id: Option<&str>) -> Result<Vec<PinRecord>> {
        let db_path = pins_db_path(&self.app_root);
        if !db_path.exists() {
            return Ok(Vec::new());
        }
        let conn = Connection::open_with_flags(&db_path, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let pin_type_column = match resolve_pin_type_column(&conn)? {
            Some(column) => column,
            None => return Ok(Vec::new()),
        };
        let sql = if source_id.is_some() {
            format!("SELECT package_id, version, source_id, {pin_type_column} FROM pin WHERE source_id = ?1")
        } else {
            format!("SELECT package_id, version, source_id, {pin_type_column} FROM pin")
        };
        let mut stmt = conn.prepare(&sql)?;
        let rows = if let Some(source_id) = source_id {
            stmt.query_map([source_id], |row| {
                let pin_type_int: i64 = row.get(3)?;
                Ok(PinRecord {
                    package_id: row.get(0)?,
                    version: row.get(1)?,
                    source_id: row.get(2)?,
                    pin_type: decode_pin_type(pin_type_int),
                })
            })?
            .filter_map(|r| r.ok())
            .collect()
        } else {
            stmt.query_map([], |row| {
                let pin_type_int: i64 = row.get(3)?;
                Ok(PinRecord {
                    package_id: row.get(0)?,
                    version: row.get(1)?,
                    source_id: row.get(2)?,
                    pin_type: decode_pin_type(pin_type_int),
                })
            })?
            .filter_map(|r| r.ok())
            .collect()
        };
        Ok(rows)
    }

    pub fn add_pin(&self, package_id: &str, version: &str, source_id: &str, pin_type: PinType) -> Result<()> {
        let db_path = pins_db_path(&self.app_root);
        let conn = Connection::open(&db_path)?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS pin (
                package_id TEXT NOT NULL,
                version TEXT NOT NULL DEFAULT '*',
                source_id TEXT NOT NULL DEFAULT '',
                type INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (package_id, source_id)
            )",
        )?;
        let pin_type_column = resolve_pin_type_column(&conn)?.unwrap_or("type");
        let type_int: i64 = match pin_type {
            PinType::Pinning => 2,
            PinType::Blocking => 4,
            PinType::Gating => 3,
        };
        conn.execute(
            &format!(
                "INSERT OR REPLACE INTO pin (package_id, version, source_id, {pin_type_column}) VALUES (?1, ?2, ?3, ?4)"
            ),
            rusqlite::params![package_id, version, source_id, type_int],
        )?;
        Ok(())
    }

    pub fn remove_pin(&self, package_id: &str, source_id: Option<&str>) -> Result<bool> {
        let db_path = pins_db_path(&self.app_root);
        if !db_path.exists() {
            return Ok(false);
        }
        let conn = Connection::open(&db_path)?;
        let count = if let Some(source_id) = source_id {
            conn.execute(
                "DELETE FROM pin WHERE package_id = ?1 AND source_id = ?2",
                rusqlite::params![package_id, source_id],
            )?
        } else {
            conn.execute("DELETE FROM pin WHERE package_id = ?1", rusqlite::params![package_id])?
        };
        Ok(count > 0)
    }

    pub fn reset_pins(&self, source_id: Option<&str>) -> Result<()> {
        let db_path = pins_db_path(&self.app_root);
        if !db_path.exists() {
            return Ok(());
        }

        if let Some(source_id) = source_id {
            let conn = Connection::open(&db_path)?;
            conn.execute("DELETE FROM pin WHERE source_id = ?1", rusqlite::params![source_id])?;
        } else {
            fs::remove_file(&db_path)?;
        }
        Ok(())
    }

    fn save_store(&self) -> Result<()> {
        save_store(&self.app_root, &self.store)
    }

    fn source_state_dir(&self, source: &SourceRecord) -> PathBuf {
        source_state_dir(&self.app_root, source)
    }

    // ── Install / download ──

    pub fn download_installer(&mut self, query: &PackageQuery, download_dir: &Path) -> Result<(Manifest, PathBuf)> {
        self.download_installer_for_request(&InstallRequest::new(query.clone()), download_dir)
    }

    pub fn download_installer_for_request(
        &mut self,
        request: &InstallRequest,
        download_dir: &Path,
    ) -> Result<(Manifest, PathBuf)> {
        let manifest = self.resolve_manifest_for_install(request)?;
        let installer = select_installer(&manifest.installers, &request.query)
            .ok_or_else(|| anyhow!("No applicable installer found for the current system"))?;
        let url = installer
            .url
            .as_deref()
            .ok_or_else(|| anyhow!("Installer has no URL"))?;

        fs::create_dir_all(download_dir)?;
        let filename = request.rename.as_deref().unwrap_or_else(|| {
            url.rsplit('/')
                .next()
                .unwrap_or("installer")
                .split('?')
                .next()
                .unwrap_or("installer")
        });
        let dest = download_dir.join(filename);

        let response = self.client.get(url).send().context("failed to download installer")?;
        if !response.status().is_success() {
            bail!("Download failed: HTTP {}", response.status());
        }
        let bytes = response.bytes()?;
        fs::write(&dest, &bytes)?;

        if let Err(error) = verify_installer_hash(installer.sha256.as_deref(), &bytes, request.ignore_security_hash) {
            let _ = fs::remove_file(&dest);
            return Err(error);
        }

        Ok((manifest, dest))
    }

    pub fn install(&mut self, query: &PackageQuery, silent: bool) -> Result<InstallResult> {
        let mut request = InstallRequest::new(query.clone());
        request.mode = if silent {
            InstallerMode::Silent
        } else {
            InstallerMode::SilentWithProgress
        };
        self.install_request(&request)
    }

    pub fn install_with_mode(&mut self, query: &PackageQuery, mode: InstallerMode) -> Result<InstallResult> {
        let mut request = InstallRequest::new(query.clone());
        request.mode = mode;
        self.install_request(&request)
    }

    pub fn install_request(&mut self, request: &InstallRequest) -> Result<InstallResult> {
        let manifest = self.resolve_manifest_for_install(request)?;
        if !package_actions_supported() {
            let installer_type = select_installer(&manifest.installers, &request.query)
                .and_then(|installer| installer.installer_type)
                .unwrap_or_else(|| "install".to_owned())
                .to_lowercase();
            return Ok(unsupported_action_result(
                manifest.id.clone(),
                manifest.version.clone(),
                installer_type,
                INSTALL_UNSUPPORTED_WARNING,
            ));
        }

        let existing_match = self.find_installed_package_for_install(request, &manifest)?;
        if let Some(no_op_result) = Self::create_install_no_op_result(request, &manifest, existing_match.as_ref()) {
            return Ok(no_op_result);
        }

        Self::ensure_package_agreements_accepted(&manifest, request)?;
        self.install_dependencies(&manifest, request, &mut HashSet::new())?;

        if request.dependencies_only {
            return Ok(InstallResult {
                package_id: manifest.id.clone(),
                version: manifest.version.clone(),
                installer_path: PathBuf::new(),
                installer_type: "dependencies".to_owned(),
                exit_code: 0,
                success: true,
                no_op: false,
                warnings: Vec::new(),
            });
        }

        let installer = select_installer(&manifest.installers, &request.query)
            .ok_or_else(|| anyhow!("No applicable installer found"))?;

        if request.uninstall_previous {
            let mut uninstall_request = UninstallRequest::new(PackageQuery {
                id: request.query.id.clone().or_else(|| Some(manifest.id.clone())),
                query: request.query.query.clone().or_else(|| Some(manifest.id.clone())),
                source: request.query.source.clone(),
                exact: true,
                version: request.query.version.clone(),
                ..PackageQuery::default()
            });
            uninstall_request.product_code = installer.product_code.clone();
            uninstall_request.mode = InstallerMode::Silent;
            uninstall_request.all_versions = true;
            uninstall_request.force = true;
            let _ = self.uninstall_request(&uninstall_request);
        }

        let temp_dir = std::env::temp_dir().join("pinget-install");
        let (_, installer_path) = self.download_installer_for_request(request, &temp_dir)?;

        let installer_type = installer.installer_type.as_deref().unwrap_or("exe").to_lowercase();

        let exit_code = dispatch_installer(&installer_path, &installer_type, request, &manifest, &installer)?;

        Ok(InstallResult {
            package_id: manifest.id.clone(),
            version: manifest.version.clone(),
            installer_path,
            installer_type,
            exit_code,
            success: exit_code == 0,
            no_op: false,
            warnings: Vec::new(),
        })
    }

    fn find_installed_package_for_install(
        &mut self,
        request: &InstallRequest,
        manifest: &Manifest,
    ) -> Result<Option<ListMatch>> {
        let installed_matches = self.list(&ListQuery {
            query: request.query.query.clone(),
            id: request.query.id.clone().or_else(|| Some(manifest.id.clone())),
            name: request.query.name.clone(),
            moniker: request.query.moniker.clone(),
            tag: None,
            command: None,
            product_code: None,
            version: None,
            source: request.query.source.clone(),
            count: Some(100),
            exact: request.query.exact || !manifest.id.trim().is_empty(),
            install_scope: request.query.install_scope.clone(),
            upgrade_only: false,
            include_unknown: false,
            include_pinned: false,
        })?;

        Ok(installed_matches.matches.into_iter().find(|candidate| {
            candidate.id.eq_ignore_ascii_case(&manifest.id) || candidate.local_id.eq_ignore_ascii_case(&manifest.id)
        }))
    }

    fn create_install_no_op_result(
        request: &InstallRequest,
        manifest: &Manifest,
        existing_match: Option<&ListMatch>,
    ) -> Option<InstallResult> {
        let existing_match = existing_match?;

        if request.no_upgrade {
            return Some(InstallResult {
                package_id: manifest.id.clone(),
                version: existing_match.installed_version.clone(),
                installer_path: PathBuf::new(),
                installer_type: "install".to_owned(),
                exit_code: 0,
                success: true,
                no_op: true,
                warnings: vec!["Package is already installed; skipping because --no-upgrade was specified.".to_owned()],
            });
        }

        if !request.force
            && !existing_match.installed_version.trim().is_empty()
            && compare_version(&existing_match.installed_version, &manifest.version) != Ordering::Less
        {
            return Some(InstallResult {
                package_id: manifest.id.clone(),
                version: existing_match.installed_version.clone(),
                installer_path: PathBuf::new(),
                installer_type: "install".to_owned(),
                exit_code: 0,
                success: true,
                no_op: true,
                warnings: vec![
                    "Package is already installed and up to date; rerun with --force to reinstall.".to_owned(),
                ],
            });
        }

        None
    }

    pub fn repair(&mut self, request: &RepairRequest) -> Result<InstallResult> {
        if request.manifest_path.is_none() && !package_actions_supported() {
            let package_id = request
                .query
                .id
                .clone()
                .or_else(|| request.query.name.clone())
                .or_else(|| request.query.moniker.clone())
                .or_else(|| request.query.query.clone())
                .or_else(|| request.product_code.clone())
                .unwrap_or_else(|| "repair".to_owned());
            return Ok(unsupported_action_result(
                package_id,
                request.query.version.clone().unwrap_or_default(),
                "repair".to_owned(),
                REPAIR_UNSUPPORTED_WARNING,
            ));
        }

        let mut warnings = vec![REPAIR_REINSTALL_WARNING.to_owned()];
        let installed_match = if request.manifest_path.is_some() {
            None
        } else {
            let installed = self.list(&Self::create_repair_list_query(request))?;
            warnings.extend(installed.warnings);
            if installed.matches.is_empty() {
                bail!("No installed package matched the supplied repair query.");
            }
            if installed.matches.len() > 1 {
                bail!("Multiple installed packages matched the supplied repair query.");
            }
            installed.matches.into_iter().next()
        };

        let mut result =
            self.install_request(&Self::create_repair_install_request(request, installed_match.as_ref()))?;
        warnings.extend(result.warnings);
        result.warnings = warnings;
        Ok(result)
    }

    pub fn uninstall(&mut self, query: &PackageQuery, silent: bool) -> Result<InstallResult> {
        let mut request = UninstallRequest::new(query.clone());
        request.mode = if silent {
            InstallerMode::Silent
        } else {
            InstallerMode::SilentWithProgress
        };
        self.uninstall_request(&request)
    }

    pub fn uninstall_request(&mut self, request: &UninstallRequest) -> Result<InstallResult> {
        if !package_actions_supported() {
            let (package_id, version) = if let Some(path) = &request.manifest_path {
                let manifest = Self::load_manifest_from_path(path)?;
                (manifest.id, request.query.version.clone().unwrap_or(manifest.version))
            } else {
                (
                    request
                        .query
                        .id
                        .clone()
                        .or_else(|| request.query.query.clone())
                        .or_else(|| request.query.name.clone())
                        .or_else(|| request.query.moniker.clone())
                        .or_else(|| request.product_code.clone())
                        .unwrap_or_else(|| "unknown".to_owned()),
                    request.query.version.clone().unwrap_or_default(),
                )
            };

            return Ok(unsupported_action_result(
                package_id,
                version,
                "uninstall",
                UNINSTALL_UNSUPPORTED_WARNING,
            ));
        }

        let matches = self.resolve_uninstall_matches(request)?;
        let mut exit_code = 0;

        for installed in &matches {
            exit_code = uninstall_package(installed, request)?;
            if exit_code != 0 && !request.all_versions {
                break;
            }
        }

        let installed = matches
            .first()
            .ok_or_else(|| anyhow!("No installed package found matching the query"))?;

        Ok(InstallResult {
            package_id: installed.id.clone(),
            version: installed.installed_version.clone(),
            installer_path: PathBuf::new(),
            installer_type: "uninstall".to_owned(),
            exit_code,
            success: exit_code == 0,
            no_op: false,
            warnings: Vec::new(),
        })
    }

    fn resolve_manifest_for_install(&mut self, request: &InstallRequest) -> Result<Manifest> {
        if let Some(path) = &request.manifest_path {
            return Self::load_manifest_from_path(path);
        }

        let (located, _warnings) = self.find_single_match(&request.query)?;
        let (manifest, _, _cached_files) = self.manifest_for_match(&located, &request.query)?;
        Ok(manifest)
    }

    fn ensure_package_agreements_accepted(manifest: &Manifest, request: &InstallRequest) -> Result<()> {
        if !request.accept_package_agreements && !manifest.agreements.is_empty() {
            bail!("Package agreements are present; rerun with --accept-package-agreements to continue.");
        }
        Ok(())
    }

    fn install_dependencies(
        &mut self,
        manifest: &Manifest,
        request: &InstallRequest,
        visited: &mut HashSet<String>,
    ) -> Result<()> {
        if request.skip_dependencies {
            return Ok(());
        }

        let dependency_ids = manifest
            .package_dependencies
            .iter()
            .chain(
                manifest
                    .installers
                    .iter()
                    .flat_map(|installer| installer.package_dependencies.iter()),
            )
            .filter(|value| !value.trim().is_empty())
            .cloned()
            .collect::<BTreeSet<_>>();

        for dependency_id in dependency_ids {
            if !visited.insert(dependency_id.clone()) {
                continue;
            }

            let mut dependency_request = InstallRequest::new(PackageQuery {
                id: Some(dependency_id),
                source: request
                    .dependency_source
                    .clone()
                    .or_else(|| request.query.source.clone()),
                exact: true,
                ..PackageQuery::default()
            });
            dependency_request.mode = request.mode;
            dependency_request.accept_package_agreements = request.accept_package_agreements;
            dependency_request.force = request.force;
            dependency_request.ignore_security_hash = request.ignore_security_hash;
            dependency_request.dependency_source = request.dependency_source.clone();
            self.install_request(&dependency_request)?;
        }

        Ok(())
    }

    fn create_repair_list_query(request: &RepairRequest) -> ListQuery {
        ListQuery {
            query: request.query.query.clone(),
            id: request.query.id.clone(),
            name: request.query.name.clone(),
            moniker: request.query.moniker.clone(),
            product_code: request.product_code.clone(),
            version: request.query.version.clone(),
            source: request.query.source.clone(),
            count: Some(100),
            exact: request.query.exact,
            install_scope: request.query.install_scope.clone(),
            ..ListQuery::default()
        }
    }

    fn create_repair_install_request(request: &RepairRequest, installed_match: Option<&ListMatch>) -> InstallRequest {
        let mut query = request.query.clone();
        query.query = if installed_match.is_none() {
            request.query.query.clone()
        } else {
            None
        };
        query.id = installed_match
            .map(|item| item.id.clone())
            .or_else(|| request.query.id.clone());
        query.name = if installed_match.is_none() {
            request.query.name.clone()
        } else {
            None
        };
        query.moniker = if installed_match.is_none() {
            request.query.moniker.clone()
        } else {
            None
        };
        query.source = request
            .query
            .source
            .clone()
            .or_else(|| installed_match.and_then(|item| item.source_name.clone()));
        query.exact = true;
        query.version = request
            .query
            .version
            .clone()
            .or_else(|| installed_match.map(|item| item.installed_version.clone()));

        InstallRequest {
            query,
            manifest_path: request.manifest_path.clone(),
            mode: request.mode,
            log_path: request.log_path.clone(),
            custom: None,
            override_args: None,
            install_location: None,
            skip_dependencies: false,
            dependencies_only: false,
            accept_package_agreements: request.accept_package_agreements,
            force: true,
            rename: None,
            uninstall_previous: false,
            ignore_security_hash: request.ignore_security_hash,
            dependency_source: None,
            no_upgrade: false,
        }
    }

    fn resolve_uninstall_matches(&mut self, request: &UninstallRequest) -> Result<Vec<ListMatch>> {
        let mut effective_query = request.query.clone();
        let mut effective_product_code = request.product_code.clone();

        if let Some(path) = &request.manifest_path {
            let manifest = Self::load_manifest_from_path(path)?;
            effective_query = PackageQuery {
                query: Some(manifest.id.clone()),
                id: Some(manifest.id.clone()),
                name: Some(manifest.name.clone()),
                exact: true,
                version: effective_query.version.clone().or(Some(manifest.version)),
                ..PackageQuery::default()
            };
            effective_product_code = effective_product_code.or_else(|| {
                manifest
                    .installers
                    .iter()
                    .find_map(|installer| installer.product_code.clone())
            });
        }

        let list_query = ListQuery {
            query: effective_query.query.clone(),
            id: effective_query.id.clone(),
            name: effective_query.name.clone(),
            moniker: effective_query.moniker.clone(),
            tag: None,
            command: None,
            product_code: effective_product_code,
            version: effective_query.version.clone(),
            source: effective_query.source.clone(),
            count: if request.all_versions { None } else { Some(100) },
            exact: effective_query.exact,
            install_scope: effective_query.install_scope,
            upgrade_only: false,
            include_unknown: false,
            include_pinned: false,
        };

        let list_result = self.list(&list_query)?;
        if list_result.matches.is_empty() {
            bail!("No installed package found matching the query");
        }
        if !request.all_versions && list_result.matches.len() > 1 && !request.force {
            bail!("Multiple installed packages matched the query; refine the query or use --all-versions.");
        }

        Ok(if request.all_versions {
            list_result.matches
        } else {
            vec![list_result.matches[0].clone()]
        })
    }

    fn load_manifest_from_path(manifest_path: &Path) -> Result<Manifest> {
        let resolved = resolve_manifest_path(manifest_path)?;
        let bytes =
            fs::read(&resolved).with_context(|| format!("failed to read manifest from {}", resolved.display()))?;
        parse_yaml_manifest(&bytes)
    }

    fn find_single_match(&mut self, query: &PackageQuery) -> Result<(LocatedMatch, Vec<String>)> {
        self.find_single_match_with_semantics(query, SearchSemantics::Single)
    }

    fn find_single_match_with_semantics(
        &mut self,
        query: &PackageQuery,
        semantics: SearchSemantics,
    ) -> Result<(LocatedMatch, Vec<String>)> {
        let (matches, warnings, _) = self.search_located(query, semantics)?;

        if matches.is_empty() {
            bail!("no package matched the supplied query");
        }

        if matches.len() > 1 {
            let choices = matches
                .iter()
                .take(10)
                .map(|item| {
                    format!(
                        "{} [{}] ({})",
                        item.display.name, item.display.id, item.display.source_name
                    )
                })
                .collect::<Vec<_>>()
                .join(", ");
            bail!("multiple packages matched: {choices}");
        }

        Ok((matches.into_iter().next().expect("one match"), warnings))
    }

    fn search_located(
        &mut self,
        query: &PackageQuery,
        semantics: SearchSemantics,
    ) -> Result<(Vec<LocatedMatch>, Vec<String>, bool)> {
        let indexes = self.resolve_source_indexes(query.source.as_deref())?;
        let mut matches = Vec::new();
        let mut warnings = Vec::new();
        let mut truncated = false;

        for index in indexes {
            match self.search_source(index, query, semantics) {
                Ok(mut source_matches) => {
                    truncated |= source_matches.truncated;
                    matches.append(&mut source_matches.matches);
                }
                Err(_error) => warnings.push(format!(
                    "Failed when searching source; results will not be included: {}",
                    self.store.sources[index].name
                )),
            }
        }

        if semantics == SearchSemantics::Many {
            matches.sort_by(|left, right| {
                search_match_sort_score(&right.display, query).cmp(&search_match_sort_score(&left.display, query))
            });
            let limit = max_results(query);
            if matches.len() > limit {
                truncated = true;
                matches.truncate(limit);
            }
        }

        Ok((matches, warnings, truncated))
    }

    fn search_source(
        &mut self,
        source_index: usize,
        query: &PackageQuery,
        semantics: SearchSemantics,
    ) -> Result<SearchSourceMatches> {
        match self.store.sources[source_index].kind {
            SourceKind::PreIndexed => self.search_preindexed(source_index, query, semantics),
            SourceKind::Rest => self.search_rest(source_index, query, semantics),
        }
    }

    fn versions_for_match(&mut self, located: &LocatedMatch, _query: &PackageQuery) -> Result<Vec<VersionKey>> {
        match &located.locator {
            MatchLocator::PreIndexedV1 { package_rowid } => {
                let source = self.source_clone(located.source_index);
                let connection = self.open_preindexed_connection(located.source_index)?;
                let versions = query_v1_versions(&connection, *package_rowid)?;
                let mut keys = versions
                    .into_iter()
                    .map(|row| VersionKey {
                        version: row.version,
                        channel: row.channel,
                    })
                    .collect::<Vec<_>>();
                sort_versions_desc(&mut keys);
                if keys.is_empty() {
                    bail!("no versions found for {} in {}", located.display.id, source.name);
                }
                Ok(keys)
            }
            MatchLocator::PreIndexedV2 {
                package_rowid,
                package_hash,
            } => {
                let source = self.source_clone(located.source_index);
                let (entries, _) = self.load_v2_version_data(&source, *package_rowid, package_hash.as_str())?;
                let mut keys = entries
                    .into_iter()
                    .map(|entry| VersionKey {
                        version: entry.version,
                        channel: String::new(),
                    })
                    .collect::<Vec<_>>();
                sort_versions_desc(&mut keys);
                Ok(keys)
            }
            MatchLocator::Rest { versions, .. } => {
                let mut keys = versions.clone();
                sort_versions_desc(&mut keys);
                Ok(keys)
            }
        }
    }

    fn manifest_for_match(
        &mut self,
        located: &LocatedMatch,
        query: &PackageQuery,
    ) -> Result<(Manifest, JsonValue, Vec<PathBuf>)> {
        match &located.locator {
            MatchLocator::PreIndexedV1 { package_rowid } => {
                let source = self.source_clone(located.source_index);
                let connection = self.open_preindexed_connection(located.source_index)?;
                let versions = query_v1_versions(&connection, *package_rowid)?;
                let selected = select_v1_version(&versions, query.version.as_deref(), query.channel.as_deref())?;
                let relative_path = resolve_v1_relative_path(&connection, selected.pathpart_id)?;
                let bytes =
                    self.get_cached_source_file("V1_M", &source, &relative_path, selected.manifest_hash.as_deref())?;
                let (mut manifest, manifest_documents) = parse_yaml_manifest_bundle(&bytes.bytes)?;
                manifest.version = selected.version.clone();
                manifest.channel = selected.channel;
                Ok((manifest, manifest_documents, vec![bytes.path]))
            }
            MatchLocator::PreIndexedV2 {
                package_rowid,
                package_hash,
            } => {
                let source = self.source_clone(located.source_index);
                let (entries, version_data_file) =
                    self.load_v2_version_data(&source, *package_rowid, package_hash.as_str())?;
                let selected = select_v2_version(&entries, query.version.as_deref())?;
                let manifest_bytes = self.get_cached_source_file(
                    "V2_M",
                    &source,
                    &selected.manifest_relative_path,
                    Some(selected.manifest_hash.as_str()),
                )?;
                let (mut manifest, manifest_documents) = parse_yaml_manifest_bundle(&manifest_bytes.bytes)?;
                manifest.version = selected.version;
                Ok((
                    manifest,
                    manifest_documents,
                    vec![version_data_file, manifest_bytes.path],
                ))
            }
            MatchLocator::Rest { package_id, versions } => {
                let source = self.source_clone(located.source_index);
                let selected = select_rest_version(versions, query.version.as_deref(), query.channel.as_deref())?;
                let (bytes, cache_path) =
                    self.get_or_fetch_rest_manifest(&source, package_id, &selected.version, &selected.channel)?;
                let (manifest, manifest_documents) =
                    parse_rest_manifest(&bytes, package_id, &selected.version, &selected.channel)?;
                Ok((manifest, manifest_documents, vec![cache_path]))
            }
        }
    }

    fn search_preindexed(
        &mut self,
        source_index: usize,
        query: &PackageQuery,
        semantics: SearchSemantics,
    ) -> Result<SearchSourceMatches> {
        let connection = self.open_preindexed_connection(source_index)?;
        let source = self.source_clone(source_index);

        match query_v2_matches(&connection, query, semantics) {
            Ok((rows, truncated)) => Ok(SearchSourceMatches {
                truncated,
                matches: rows
                    .into_iter()
                    .map(|row| LocatedMatch {
                        display: SearchMatch {
                            source_name: source.name.clone(),
                            source_kind: source.kind,
                            id: row.id.clone(),
                            name: row.name.clone(),
                            moniker: row.moniker.clone(),
                            version: Some(row.version.clone()),
                            channel: None,
                            match_criteria: row.match_criteria.clone(),
                        },
                        source_index,
                        locator: MatchLocator::PreIndexedV2 {
                            package_rowid: row.package_rowid,
                            package_hash: row.package_hash,
                        },
                    })
                    .collect(),
            }),
            Err(error) if can_fallback_to_v1(&error) => {
                let (rows, truncated) = query_v1_matches(&connection, query, semantics)?;
                let grouped = group_v1_rows(rows);

                Ok(SearchSourceMatches {
                    truncated,
                    matches: grouped
                        .into_iter()
                        .map(|row| LocatedMatch {
                            display: SearchMatch {
                                source_name: source.name.clone(),
                                source_kind: source.kind,
                                id: row.id.clone(),
                                name: row.name.clone(),
                                moniker: row.moniker.clone(),
                                version: Some(row.version.clone()),
                                channel: Some(row.channel.clone()),
                                match_criteria: row.match_criteria.clone(),
                            },
                            source_index,
                            locator: MatchLocator::PreIndexedV1 {
                                package_rowid: row.package_rowid,
                            },
                        })
                        .collect(),
                })
            }
            Err(error) => Err(error),
        }
    }

    fn search_rest(
        &mut self,
        source_index: usize,
        query: &PackageQuery,
        semantics: SearchSemantics,
    ) -> Result<SearchSourceMatches> {
        let source = self.source_clone(source_index);
        let info = self.load_rest_information(source_index)?;
        let contract = choose_contract(&info.server_supported_versions)
            .ok_or_else(|| anyhow!("no compatible REST contract for {}", source.name))?;

        let url = format!("{}/manifestSearch", source.arg.trim_end_matches('/'));
        let body = build_rest_search_body(query, &info, semantics)?;
        let response = self
            .client
            .post(url)
            .header("Version", contract)
            .json(&body)
            .send()
            .context("REST search request failed")?
            .error_for_status()
            .context("REST search request returned an error")?;
        let json = response
            .json::<JsonValue>()
            .context("failed to parse REST search response")?;
        let data = json
            .get("Data")
            .and_then(JsonValue::as_array)
            .cloned()
            .unwrap_or_default();
        let max_results = source_fetch_results(query, semantics);

        let mut results = Vec::new();
        for item in data {
            let package_id = json_string(&item, "PackageIdentifier")
                .ok_or_else(|| anyhow!("REST search result missing PackageIdentifier"))?;
            let package_name =
                json_string(&item, "PackageName").ok_or_else(|| anyhow!("REST search result missing PackageName"))?;
            let mut versions = parse_rest_versions(&item)?;
            sort_versions_desc(&mut versions);
            let latest = versions
                .first()
                .cloned()
                .ok_or_else(|| anyhow!("REST search result had no versions"))?;

            results.push(LocatedMatch {
                display: SearchMatch {
                    source_name: source.name.clone(),
                    source_kind: source.kind,
                    id: package_id.clone(),
                    name: package_name,
                    moniker: json_string(&item, "Moniker"),
                    version: Some(latest.version.clone()),
                    channel: if latest.channel.is_empty() {
                        None
                    } else {
                        Some(latest.channel.clone())
                    },
                    match_criteria: rest_match_criteria(&item, query, semantics),
                },
                source_index,
                locator: MatchLocator::Rest { package_id, versions },
            });
        }

        Ok(SearchSourceMatches {
            truncated: results.len() >= max_results,
            matches: results,
        })
    }

    fn open_preindexed_connection(&mut self, source_index: usize) -> Result<Connection> {
        let source = self.source_clone(source_index);
        let index_path = preindexed_index_path(&self.app_root, &source);
        if !index_path.exists() {
            let _ = self.update_preindexed(source_index)?;
            self.save_store()?;
        }

        Self::open_sqlite_connection(index_path).context("failed to open preindexed index")
    }

    fn open_sqlite_connection(path: PathBuf) -> Result<Connection> {
        let path_text = path.to_string_lossy().into_owned();
        Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_URI)
            .with_context(|| format!("failed to open SQLite database at {path_text}"))
    }

    fn update_preindexed(&mut self, source_index: usize) -> Result<String> {
        let source = &mut self.store.sources[source_index];
        let state_dir = source_state_dir(&self.app_root, source);
        fs::create_dir_all(&state_dir).context("failed to create source state directory")?;

        let mut last_error = None;
        for candidate in PREINDEXED_CANDIDATES {
            let url = format!("{}/{}", source.arg.trim_end_matches('/'), candidate);
            match self.client.get(&url).send() {
                Ok(response) if response.status().is_success() => {
                    let header_version = response
                        .headers()
                        .get("x-ms-meta-sourceversion")
                        .and_then(|value| value.to_str().ok())
                        .map(str::to_owned);
                    let bytes = response.bytes().context("failed to read preindexed package bytes")?;
                    let payload = bytes.to_vec();
                    let index_bytes = extract_zip_member(&payload, "Public/index.db")
                        .context("preindexed package did not contain Public/index.db")?;

                    fs::write(preindexed_package_path(&self.app_root, source), &payload)
                        .context("failed to persist source package")?;
                    fs::write(preindexed_index_path(&self.app_root, source), index_bytes)
                        .context("failed to persist source index")?;
                    source.last_update = Some(Utc::now());
                    source.source_version = header_version;
                    return Ok(format!("downloaded {}", candidate));
                }
                Ok(response) => {
                    last_error = Some(anyhow!("candidate {} returned HTTP {}", candidate, response.status()));
                }
                Err(error) => {
                    last_error = Some(anyhow!("candidate {} failed: {error}", candidate));
                }
            }
        }

        Err(last_error.unwrap_or_else(|| anyhow!("no preindexed source candidate succeeded")))
    }

    fn update_rest(&mut self, source_index: usize) -> Result<String> {
        let info = self.fetch_rest_information(source_index, true)?;
        let source = &mut self.store.sources[source_index];
        if !info.source_identifier.is_empty() {
            source.identifier = info.source_identifier.clone();
        }
        source.last_update = Some(Utc::now());
        source.source_version = choose_contract(&info.server_supported_versions).map(str::to_owned);
        Ok("refreshed information cache".to_owned())
    }

    fn load_rest_information(&mut self, source_index: usize) -> Result<RestInformation> {
        let source = self.source_clone(source_index);
        let cache_path = rest_information_cache_path(&self.app_root, &source);

        if cache_path.exists() {
            let cache = serde_json::from_slice::<RestInfoCache>(
                &fs::read(&cache_path).context("failed to read REST information cache")?,
            )
            .context("failed to parse REST information cache")?;
            if cache.expires_at > Utc::now() {
                return Ok(cache.value);
            }
        }

        self.fetch_rest_information(source_index, true)
    }

    fn fetch_rest_information(&mut self, source_index: usize, persist: bool) -> Result<RestInformation> {
        let source = self.source_clone(source_index);
        let url = format!("{}/information", source.arg.trim_end_matches('/'));
        let response = self
            .client
            .get(url)
            .send()
            .context("REST information request failed")?
            .error_for_status()
            .context("REST information request returned an error")?;

        let max_age = cache_control_max_age(&response);
        let json = response
            .json::<JsonValue>()
            .context("failed to parse REST information response")?;
        let data = json
            .get("Data")
            .cloned()
            .ok_or_else(|| anyhow!("REST information response missing Data"))?;
        let info = serde_json::from_value::<RestInformation>(data)
            .context("failed to deserialize REST information payload")?;

        if persist {
            let cache = RestInfoCache {
                expires_at: Utc::now() + Duration::seconds(i64::try_from(max_age).unwrap_or(i64::MAX)),
                value: info.clone(),
            };
            write_json(rest_information_cache_path(&self.app_root, &source), &cache)?;
        }

        Ok(info)
    }

    fn load_v2_version_data(
        &self,
        source: &SourceRecord,
        package_rowid: i64,
        package_hash: &str,
    ) -> Result<(Vec<PackageVersionDataEntry>, PathBuf)> {
        let connection = Self::open_sqlite_connection(preindexed_index_path(&self.app_root, source))
            .context("failed to reopen preindexed index for V2 version data")?;
        let package_hash = package_hash.to_ascii_lowercase();
        let package_id = query_optional_value(
            &connection,
            "SELECT id FROM packages WHERE rowid = ?1",
            vec![SqlValue::Integer(package_rowid)],
            |row| row_string(row, 0),
        )?
        .ok_or_else(|| anyhow!("failed to resolve package id for V2 version data"))?;
        let relative_path = package_version_data_relative_path(&package_id, &package_hash);
        let file = self.get_cached_source_file("V2_PVD", source, &relative_path, Some(&package_hash))?;
        let yaml = decompress_mszyml(&file.bytes)?;
        let document =
            serde_yaml::from_str::<PackageVersionDataDocument>(&yaml).context("failed to parse versionData.mszyml")?;
        Ok((document.versions, file.path))
    }

    fn get_cached_source_file(
        &self,
        bucket: &str,
        source: &SourceRecord,
        relative_path: &str,
        expected_hash: Option<&str>,
    ) -> Result<CachedBytes> {
        let normalized_relative = relative_path.replace('\\', "/");
        let cache_path =
            temp_cache_path(&self.app_root, bucket, &source.identifier).join(normalized_relative.replace('/', "\\"));

        if cache_path.exists() {
            let cached = fs::read(&cache_path).context("failed to read cached source file")?;
            if hash_matches(expected_hash, &cached) {
                return Ok(CachedBytes {
                    path: cache_path,
                    bytes: cached,
                });
            }
        }

        let url = format!(
            "{}/{}",
            source.arg.trim_end_matches('/'),
            normalized_relative.trim_start_matches('/')
        );
        let response = self
            .client
            .get(url)
            .send()
            .context("failed to fetch source file")?
            .error_for_status()
            .context("source file request returned an error")?;
        let bytes = response.bytes().context("failed to read source file body")?.to_vec();

        if let Some(hash) = expected_hash {
            verify_hash(hash, &bytes)?;
        }

        if let Some(parent) = cache_path.parent() {
            fs::create_dir_all(parent).context("failed to create cache directory")?;
        }
        fs::write(&cache_path, &bytes).context("failed to write cache file")?;

        Ok(CachedBytes {
            path: cache_path,
            bytes,
        })
    }

    fn get_or_fetch_rest_manifest(
        &mut self,
        source: &SourceRecord,
        package_id: &str,
        version: &str,
        channel: &str,
    ) -> Result<(Vec<u8>, PathBuf)> {
        let cache_path = rest_manifest_cache_path_with_root(&self.app_root, source, package_id, version, channel);
        if cache_path.exists() {
            return Ok((
                fs::read(&cache_path).context("failed to read cached REST manifest")?,
                cache_path,
            ));
        }

        let info = self.load_rest_information_by_name(&source.name)?;
        let contract = choose_contract(&info.server_supported_versions)
            .ok_or_else(|| anyhow!("no compatible REST contract for {}", source.name))?;
        let url = format!("{}/packageManifests/{}", source.arg.trim_end_matches('/'), package_id);

        let mut params = vec![("Version", version.to_owned())];
        if !channel.is_empty() {
            params.push(("Channel", channel.to_owned()));
        }
        if info
            .required_query_parameters
            .iter()
            .any(|value| value.eq_ignore_ascii_case("Market"))
        {
            params.push(("Market", default_market()));
        }

        let response = self
            .client
            .get(url)
            .header("Version", contract)
            .query(&params)
            .send()
            .context("REST manifest request failed")?
            .error_for_status()
            .context("REST manifest request returned an error")?;
        let bytes = response
            .bytes()
            .context("failed to read REST manifest response")?
            .to_vec();

        if let Some(parent) = cache_path.parent() {
            fs::create_dir_all(parent).context("failed to create REST manifest cache directory")?;
        }
        fs::write(&cache_path, &bytes).context("failed to write REST manifest cache")?;

        Ok((bytes, cache_path))
    }

    fn load_rest_information_by_name(&mut self, name: &str) -> Result<RestInformation> {
        let index = self
            .store
            .sources
            .iter()
            .position(|source| source.name.eq_ignore_ascii_case(name))
            .ok_or_else(|| anyhow!("source '{name}' was not configured"))?;
        self.load_rest_information(index)
    }

    fn resolve_source_indexes(&self, source_name: Option<&str>) -> Result<Vec<usize>> {
        if let Some(name) = source_name {
            let index = self
                .store
                .sources
                .iter()
                .position(|source| source.name.eq_ignore_ascii_case(name))
                .ok_or_else(|| anyhow!("source '{name}' was not configured"))?;
            return Ok(vec![index]);
        }

        let mut indexes: Vec<_> = self
            .store
            .sources
            .iter()
            .enumerate()
            .filter(|(_, source)| !source.explicit)
            .collect();
        indexes.sort_by(|(left_index, left_source), (right_index, right_source)| {
            right_source
                .priority
                .cmp(&left_source.priority)
                .then_with(|| left_index.cmp(right_index))
        });
        Ok(indexes.into_iter().map(|(index, _)| index).collect())
    }

    fn source_clone(&self, index: usize) -> SourceRecord {
        self.store.sources[index].clone()
    }
}

#[derive(Debug)]
struct CachedBytes {
    path: PathBuf,
    bytes: Vec<u8>,
}

#[derive(Debug, Clone)]
struct V1SearchRow {
    manifest_rowid: i64,
    package_rowid: i64,
    version: String,
    channel: String,
    id: String,
    name: String,
    moniker: Option<String>,
    match_criteria: Option<String>,
}

#[derive(Debug, Clone)]
struct V1VersionRow {
    version: String,
    channel: String,
    pathpart_id: i64,
    manifest_hash: Option<String>,
}

#[derive(Debug, Clone)]
struct V2SearchRow {
    package_rowid: i64,
    package_hash: String,
    id: String,
    name: String,
    moniker: Option<String>,
    version: String,
    match_criteria: Option<String>,
}

fn list_query_needs_available_lookup(query: &ListQuery) -> bool {
    query.query.is_some()
        || query.id.is_some()
        || query.name.is_some()
        || query.moniker.is_some()
        || query.tag.is_some()
        || query.command.is_some()
        || query.source.is_some()
}

fn package_query_from_list_query(query: &ListQuery) -> PackageQuery {
    PackageQuery {
        query: query.query.clone(),
        id: query.id.clone(),
        name: query.name.clone(),
        moniker: query.moniker.clone(),
        tag: query.tag.clone(),
        command: query.command.clone(),
        source: query.source.clone(),
        count: Some(LIST_LOOKUP_MAX_RESULTS),
        exact: query.exact,
        version: None,
        channel: None,
        locale: None,
        installer_type: None,
        installer_architecture: None,
        platform: None,
        os_version: None,
        install_scope: query.install_scope.clone(),
    }
}

fn allow_loose_list_correlation(query: &ListQuery) -> bool {
    query.query.is_some() || query.id.is_some() || query.name.is_some()
}

fn installed_package_has_upgrade(package: &InstalledPackage) -> bool {
    package
        .correlated
        .as_ref()
        .and_then(|candidate| candidate.version.as_deref())
        .is_some_and(|version| compare_version(version, &package.installed_version) == Ordering::Greater)
}

fn installed_package_has_unknown_version(package: &InstalledPackage) -> bool {
    package.installed_version.eq_ignore_ascii_case("Unknown")
}

fn installed_package_matches_upgrade_filter(package: &InstalledPackage, query: &ListQuery) -> bool {
    installed_package_has_upgrade(package)
        || (query.include_unknown && installed_package_has_unknown_version(package) && package.correlated.is_some())
}

fn find_applicable_pin<'a>(item: &ListMatch, pins: &'a [PinRecord]) -> Option<&'a PinRecord> {
    let mut source_specific = None;
    let mut source_agnostic = None;

    for pin in pins {
        if !pin.package_id.eq_ignore_ascii_case(&item.id) && !pin.package_id.eq_ignore_ascii_case(&item.local_id) {
            continue;
        }

        if pin.source_id.is_empty() {
            if source_agnostic.is_none() {
                source_agnostic = Some(pin);
            }
        } else if item
            .source_name
            .as_deref()
            .is_some_and(|source| pin.source_id.eq_ignore_ascii_case(source))
        {
            source_specific = Some(pin);
            break;
        }
    }

    source_specific.or(source_agnostic)
}

fn is_upgrade_blocked_by_pin(item: &ListMatch, pins: &[PinRecord]) -> bool {
    let Some(available_version) = item.available_version.as_deref() else {
        return false;
    };
    let Some(pin) = find_applicable_pin(item, pins) else {
        return false;
    };

    match pin.pin_type {
        PinType::Blocking => true,
        PinType::Gating | PinType::Pinning => !version_matches_pin_pattern(available_version, &pin.version),
    }
}

fn version_matches_pin_pattern(version: &str, pattern: &str) -> bool {
    if pattern.trim().is_empty() || pattern == "*" {
        return true;
    }

    if !pattern.contains('*') {
        return version.eq_ignore_ascii_case(pattern);
    }

    if pattern.ends_with('*') && pattern.matches('*').count() == 1 {
        let prefix = &pattern[..pattern.len() - 1];
        return version.to_ascii_lowercase().starts_with(&prefix.to_ascii_lowercase());
    }

    version.eq_ignore_ascii_case(pattern)
}

fn list_match_from_installed(package: InstalledPackage) -> ListMatch {
    let available_version = package.correlated.as_ref().and_then(|candidate| {
        candidate.version.as_ref().and_then(|candidate_version| {
            if installed_package_has_unknown_version(&package)
                || compare_version(candidate_version, &package.installed_version) == Ordering::Greater
            {
                Some(candidate_version.clone())
            } else {
                None
            }
        })
    });
    let source_name = package
        .correlated
        .as_ref()
        .map(|candidate| candidate.source_name.clone())
        .filter(|value| !value.is_empty());
    let id = package
        .correlated
        .as_ref()
        .map(|candidate| candidate.id.clone())
        .unwrap_or_else(|| package.local_id.clone());

    ListMatch {
        name: package.name,
        id,
        local_id: package.local_id,
        installed_version: package.installed_version,
        available_version,
        source_name,
        publisher: package.publisher,
        scope: package.scope,
        installer_category: package.installer_category,
        install_location: package.install_location,
        package_family_names: package.package_family_names,
        product_codes: package.product_codes,
        upgrade_codes: package.upgrade_codes,
    }
}

fn list_sort_weight(package: &InstalledPackage) -> usize {
    if package.local_id.starts_with("ARP\\") {
        0
    } else if package.name.contains(".SparseApp") || package.local_id.contains(".SparseApp_") {
        1
    } else {
        2
    }
}

fn list_package_matches(package: &InstalledPackage, query: &ListQuery) -> bool {
    let correlated = package.correlated.as_ref();

    if let Some(value) = &query.id {
        let local_match = matches_text(&package.local_id, value, query.exact);
        let correlated_match = correlated
            .map(|candidate| matches_text(&candidate.id, value, query.exact))
            .unwrap_or(false);
        if !local_match && !correlated_match {
            return false;
        }
    }

    if let Some(value) = &query.name
        && !matches_text(&package.name, value, query.exact)
    {
        return false;
    }

    if let Some(value) = &query.product_code
        && !package
            .product_codes
            .iter()
            .any(|code| code.eq_ignore_ascii_case(value))
    {
        return false;
    }

    if let Some(value) = &query.version
        && !package.installed_version.eq_ignore_ascii_case(value)
    {
        return false;
    }

    if let Some(value) = &query.query {
        let local_match =
            matches_text(&package.name, value, query.exact) || matches_text(&package.local_id, value, query.exact);
        let correlated_match = correlated
            .map(|candidate| {
                matches_text(&candidate.id, value, query.exact) || matches_text(&candidate.name, value, query.exact)
            })
            .unwrap_or(false);
        if !local_match && !correlated_match {
            return false;
        }
    }

    if let Some(source) = &query.source
        && correlated
            .map(|candidate| !candidate.source_name.eq_ignore_ascii_case(source))
            .unwrap_or(true)
    {
        return false;
    }

    if (query.moniker.is_some() || query.tag.is_some() || query.command.is_some()) && correlated.is_none() {
        return false;
    }

    true
}

fn correlate_installed_package(
    package: &InstalledPackage,
    candidates: &[SearchMatch],
    allow_loose_name_match: bool,
) -> Option<SearchMatch> {
    if package.local_id.starts_with("MSIX\\") {
        return None;
    }

    let installed_name = normalize_correlation_name(&package.name);
    let candidate_names = correlation_name_candidates(&package.name);

    candidates
        .iter()
        .filter_map(|candidate| {
            let candidate_name = normalize_correlation_name(&candidate.name);
            let score = if candidate.id.eq_ignore_ascii_case(&package.local_id) {
                1000
            } else if candidate_names.iter().any(|name| {
                let normalized = normalize_correlation_name(name);
                normalized == candidate_name
            }) {
                900
            } else if allow_loose_name_match && candidate_name.len() >= 6 && installed_name.contains(&candidate_name) {
                700
            } else {
                0
            };

            (score > 0).then_some((score, candidate.clone()))
        })
        .max_by_key(|(score, _)| *score)
        .map(|(_, candidate)| candidate)
}

fn correlation_name_candidates(name: &str) -> Vec<String> {
    let mut candidates = Vec::new();
    candidates.push(name.trim().to_owned());

    let mut trimmed = name.trim().to_owned();
    if let Some(index) = trimmed.find(" (") {
        trimmed.truncate(index);
    }

    let mut words = Vec::new();
    for token in trimmed.split_whitespace() {
        let lower = token
            .trim_matches(|ch: char| ch == '(' || ch == ')')
            .to_ascii_lowercase();
        if !words.is_empty()
            && (lower == "x64" || lower == "x86" || lower == "arm64" || token.chars().any(|ch| ch.is_ascii_digit()))
        {
            break;
        }
        words.push(token);
    }

    if !words.is_empty() {
        candidates.push(words.join(" "));
    }

    candidates.sort();
    candidates.dedup();
    candidates
}

fn normalize_correlation_name(value: &str) -> String {
    value
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric())
        .flat_map(|ch| ch.to_lowercase())
        .collect()
}

#[cfg(windows)]
fn collect_installed_packages(scope: Option<&str>) -> Result<Vec<InstalledPackage>> {
    let mut packages = Vec::new();
    let mut seen = BTreeSet::new();

    let machine = !matches!(scope, Some(value) if value.eq_ignore_ascii_case("user"));
    let user = !matches!(scope, Some(value) if value.eq_ignore_ascii_case("machine"));

    if machine {
        collect_uninstall_view(
            &mut packages,
            &mut seen,
            RegKey::predef(HKEY_LOCAL_MACHINE),
            "Machine",
            "X64",
            KEY_READ | KEY_WOW64_64KEY,
        )?;
        collect_uninstall_view(
            &mut packages,
            &mut seen,
            RegKey::predef(HKEY_LOCAL_MACHINE),
            "Machine",
            "X86",
            KEY_READ | KEY_WOW64_32KEY,
        )?;
        collect_appmodel_packages(
            &mut packages,
            &mut seen,
            RegKey::predef(HKEY_LOCAL_MACHINE),
            "Machine",
            KEY_READ | KEY_WOW64_64KEY,
        )?;
    }

    if user {
        collect_uninstall_view(
            &mut packages,
            &mut seen,
            RegKey::predef(HKEY_CURRENT_USER),
            "User",
            "X64",
            KEY_READ,
        )?;
        collect_appmodel_packages(
            &mut packages,
            &mut seen,
            RegKey::predef(HKEY_CURRENT_USER),
            "User",
            KEY_READ,
        )?;
    }

    Ok(packages)
}

#[cfg(not(windows))]
fn collect_installed_packages(_scope: Option<&str>) -> Result<Vec<InstalledPackage>> {
    Ok(Vec::new())
}

#[cfg(windows)]
fn collect_uninstall_view(
    packages: &mut Vec<InstalledPackage>,
    seen: &mut BTreeSet<String>,
    root: RegKey,
    scope: &str,
    arch: &str,
    flags: u32,
) -> Result<()> {
    const UNINSTALL_PATH: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    let uninstall = match root.open_subkey_with_flags(UNINSTALL_PATH, flags) {
        Ok(key) => key,
        Err(_) => return Ok(()),
    };

    for key_name in uninstall.enum_keys().flatten() {
        let subkey = match uninstall.open_subkey_with_flags(&key_name, flags) {
            Ok(key) => key,
            Err(_) => continue,
        };
        if read_reg_dword(&subkey, "SystemComponent") == Some(1) || read_reg_string(&subkey, "ParentKeyName").is_some()
        {
            continue;
        }

        let Some(name) = read_reg_string(&subkey, "DisplayName").filter(|value| !value.is_empty()) else {
            continue;
        };

        let local_id = format!(r"ARP\{scope}\{arch}\{key_name}");
        let installed_version = read_reg_string(&subkey, "DisplayVersion").unwrap_or_else(|| "Unknown".to_owned());
        let publisher = read_reg_string(&subkey, "Publisher");
        let install_location = read_reg_string(&subkey, "InstallLocation");
        let package_family_names = read_reg_string(&subkey, "PackageFamilyName")
            .into_iter()
            .collect::<Vec<_>>();
        let mut product_codes = read_reg_string(&subkey, "ProductCode").into_iter().collect::<Vec<_>>();
        if product_codes.is_empty() && looks_like_product_code(&key_name) {
            product_codes.push(key_name.to_ascii_lowercase());
        }
        let upgrade_codes = read_reg_string(&subkey, "UpgradeCode").into_iter().collect::<Vec<_>>();
        let installer_category =
            if local_id.starts_with("ARP\\") && read_reg_dword(&subkey, "WindowsInstaller") == Some(1) {
                Some("msi".to_owned())
            } else if key_name.starts_with("MSIX\\") {
                Some("msix".to_owned())
            } else {
                Some("exe".to_owned())
            };

        let dedupe_key = format!(
            "{}|{}|{}|{}",
            local_id,
            name.to_ascii_lowercase(),
            installed_version.to_ascii_lowercase(),
            publisher.clone().unwrap_or_default().to_ascii_lowercase()
        );
        if !seen.insert(dedupe_key) {
            continue;
        }

        packages.push(InstalledPackage {
            name,
            local_id,
            installed_version,
            publisher,
            scope: Some(scope.to_owned()),
            installer_category,
            install_location,
            package_family_names,
            product_codes,
            upgrade_codes,
            correlated: None,
        });
    }

    Ok(())
}

#[cfg(windows)]
fn collect_appmodel_packages(
    packages: &mut Vec<InstalledPackage>,
    seen: &mut BTreeSet<String>,
    root: RegKey,
    scope: &str,
    flags: u32,
) -> Result<()> {
    const APPMODEL_PACKAGES_PATH: &str =
        r"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    let appmodel = match root.open_subkey_with_flags(APPMODEL_PACKAGES_PATH, flags) {
        Ok(key) => key,
        Err(_) => return Ok(()),
    };

    for key_name in appmodel.enum_keys().flatten() {
        let subkey = match appmodel.open_subkey_with_flags(&key_name, flags) {
            Ok(key) => key,
            Err(_) => continue,
        };

        let Some(name) = read_reg_string(&subkey, "DisplayName").filter(|value| !value.is_empty()) else {
            continue;
        };
        let install_location = read_reg_string(&subkey, "PackageRootFolder");
        if install_location.as_deref().is_some_and(is_windows_system_path) {
            continue;
        }

        let Some(metadata) = parse_msix_package_full_name(&key_name) else {
            continue;
        };

        let local_id = format!(r"MSIX\{key_name}");
        let dedupe_key = format!(
            "{}|{}|{}",
            local_id,
            name.to_ascii_lowercase(),
            metadata.version.to_ascii_lowercase()
        );
        if !seen.insert(dedupe_key) {
            continue;
        }

        packages.push(InstalledPackage {
            name,
            local_id,
            installed_version: metadata.version,
            publisher: None,
            scope: Some(scope.to_owned()),
            installer_category: Some("msix".to_owned()),
            install_location,
            package_family_names: vec![metadata.family_name],
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        });
    }

    Ok(())
}

#[cfg(windows)]
struct ParsedMsixPackageFullName {
    version: String,
    family_name: String,
}

#[cfg(windows)]
fn parse_msix_package_full_name(value: &str) -> Option<ParsedMsixPackageFullName> {
    let segments = value.split('_').collect::<Vec<_>>();
    if segments.len() < 5 {
        return None;
    }

    let name = segments[..segments.len() - 4].join("_");
    if name.is_empty() {
        return None;
    }

    let version = segments[segments.len() - 4].trim();
    let resource_id = segments[segments.len() - 2].trim();
    let publisher_id = segments[segments.len() - 1].trim();
    if version.is_empty() || publisher_id.is_empty() {
        return None;
    }

    let family_name = if resource_id.is_empty() {
        format!("{name}_{publisher_id}")
    } else {
        format!("{name}_{resource_id}_{publisher_id}")
    };

    Some(ParsedMsixPackageFullName {
        version: version.to_owned(),
        family_name,
    })
}

#[cfg(windows)]
fn is_windows_system_path(path: &str) -> bool {
    path.trim().to_ascii_lowercase().starts_with(r"c:\windows\")
}

#[cfg(windows)]
fn read_reg_string(key: &RegKey, value_name: &str) -> Option<String> {
    key.get_value::<String, _>(value_name)
        .ok()
        .map(|value| value.trim().to_owned())
        .filter(|value| !value.is_empty())
}

#[cfg(windows)]
fn read_reg_dword(key: &RegKey, value_name: &str) -> Option<u32> {
    key.get_value::<u32, _>(value_name).ok()
}

#[cfg(windows)]
fn looks_like_product_code(value: &str) -> bool {
    value.starts_with('{') && value.ends_with('}')
}

fn ensure_app_dirs(app_root: &Path) -> Result<()> {
    fs::create_dir_all(app_root).context("failed to create app root directory")?;
    if uses_packaged_layout(app_root) {
        fs::create_dir_all(packaged_file_cache_root(app_root)).context("failed to create packaged file cache root")?;
        let user_sources_path = packaged_user_sources_path(app_root)?;
        if let Some(parent) = user_sources_path.parent() {
            fs::create_dir_all(parent).context("failed to create packaged secure settings directory")?;
        }
    } else {
        fs::create_dir_all(app_root.join("sources")).context("failed to create app source directory")?;
    }
    Ok(())
}

fn default_app_root() -> Result<PathBuf> {
    let local_app_data = dirs::data_local_dir().ok_or_else(|| anyhow!("unable to determine LocalAppData path"))?;

    #[cfg(windows)]
    {
        Ok(local_app_data
            .join("Packages")
            .join(PACKAGED_FAMILY_NAME)
            .join("LocalState"))
    }

    #[cfg(not(windows))]
    {
        Ok(local_app_data.join("pinget"))
    }
}

fn store_path(app_root: &Path) -> PathBuf {
    app_root.join("sources.json")
}

fn user_settings_path(app_root: &Path) -> PathBuf {
    if uses_packaged_layout(app_root) {
        app_root.join("settings.json")
    } else {
        app_root.join("user-settings.json")
    }
}

fn admin_settings_path(app_root: &Path) -> PathBuf {
    app_root.join("admin-settings.json")
}

fn source_state_dir(app_root: &Path, source: &SourceRecord) -> PathBuf {
    if uses_packaged_layout(app_root) {
        return app_root
            .join(packaged_source_type(source.kind))
            .join(sanitize_path_segment(&source.identifier));
    }

    app_root.join("sources").join(sanitize_path_segment(&source.name))
}

fn preindexed_package_path(app_root: &Path, source: &SourceRecord) -> PathBuf {
    source_state_dir(app_root, source).join("source.msix")
}

fn preindexed_index_path(app_root: &Path, source: &SourceRecord) -> PathBuf {
    source_state_dir(app_root, source).join("index.db")
}

fn rest_information_cache_path(app_root: &Path, source: &SourceRecord) -> PathBuf {
    source_state_dir(app_root, source).join("rest-information.json")
}

fn rest_manifest_cache_path_with_root(
    app_root: &Path,
    source: &SourceRecord,
    package_id: &str,
    version: &str,
    channel: &str,
) -> PathBuf {
    let key = format!("{}|{}|{}|{}", source.identifier, package_id, version, channel);
    let digest = sha256_hex(key.as_bytes());
    cache_root(app_root)
        .join("REST_M")
        .join(sanitize_path_segment(&source.identifier))
        .join(format!("{digest}.json"))
}

fn temp_cache_path(app_root: &Path, bucket: &str, identifier: &str) -> PathBuf {
    cache_root(app_root)
        .join(bucket)
        .join(sanitize_path_segment(identifier))
}

fn load_store(app_root: &Path) -> Result<SourceStore> {
    if uses_packaged_layout(app_root)
        && let Some(store) = load_packaged_store(app_root)?
    {
        return Ok(store);
    }

    let path = store_path(app_root);
    if !path.exists() {
        let store = SourceStore::default();
        save_store(app_root, &store)?;
        return Ok(store);
    }

    let bytes = fs::read(path).context("failed to read source store")?;
    serde_json::from_slice(&bytes).context("failed to parse source store")
}

fn save_store(app_root: &Path, store: &SourceStore) -> Result<()> {
    if uses_packaged_layout(app_root) {
        return save_packaged_store(app_root, store);
    }

    write_json(store_path(app_root), store)
}

fn load_packaged_store(app_root: &Path) -> Result<Option<SourceStore>> {
    let user_sources_path = packaged_user_sources_path(app_root)?;
    let metadata_path = app_root.join("sources_metadata");
    let user_sources_yaml = fs::read_to_string(&user_sources_path).ok();
    let metadata_yaml = fs::read_to_string(&metadata_path).ok();
    Ok(parse_packaged_source_store(
        user_sources_yaml.as_deref(),
        metadata_yaml.as_deref(),
    ))
}

fn save_packaged_store(app_root: &Path, store: &SourceStore) -> Result<()> {
    let user_sources_path = packaged_user_sources_path(app_root)?;
    if let Some(parent) = user_sources_path.parent() {
        fs::create_dir_all(parent).context("failed to create packaged source stream directory")?;
    }

    fs::write(&user_sources_path, render_packaged_sources_yaml(store))
        .context("failed to write packaged user_sources stream")?;
    fs::write(app_root.join("sources_metadata"), render_packaged_metadata_yaml(store))
        .context("failed to write packaged source metadata")?;
    Ok(())
}

fn parse_packaged_source_store(user_sources_yaml: Option<&str>, metadata_yaml: Option<&str>) -> Option<SourceStore> {
    if user_sources_yaml.is_none() && metadata_yaml.is_none() {
        return None;
    }

    let mut sources = SourceStore::default().sources;
    for entry in parse_packaged_yaml_entries(user_sources_yaml) {
        let Some(name) = entry.get("Name").and_then(|value| value.clone()) else {
            continue;
        };

        if yaml_bool(entry.get("IsTombstone")) {
            sources.retain(|source| !source.name.eq_ignore_ascii_case(&name));
            continue;
        }

        let Some(mapped) = map_packaged_source_entry(&entry) else {
            continue;
        };

        if let Some(existing) = sources
            .iter_mut()
            .find(|source| source.name.eq_ignore_ascii_case(&name))
        {
            *existing = mapped;
        } else {
            sources.push(mapped);
        }
    }

    for entry in parse_packaged_yaml_entries(metadata_yaml) {
        let Some(name) = entry.get("Name").and_then(|value| value.clone()) else {
            continue;
        };

        let Some(source) = sources
            .iter_mut()
            .find(|source| source.name.eq_ignore_ascii_case(&name))
        else {
            continue;
        };

        if let Some(last_update) = entry
            .get("LastUpdate")
            .and_then(|value| value.as_deref())
            .and_then(parse_packaged_datetime)
        {
            source.last_update = Some(last_update);
        }

        if let Some(source_version) = entry.get("SourceVersion").and_then(|value| value.clone())
            && !source_version.is_empty()
        {
            source.source_version = Some(source_version);
        }
    }

    Some(SourceStore { sources })
}

fn parse_packaged_yaml_entries(yaml: Option<&str>) -> Vec<BTreeMap<String, Option<String>>> {
    let Some(yaml) = yaml else {
        return Vec::new();
    };

    let mut entries = Vec::new();
    let mut current: Option<BTreeMap<String, Option<String>>> = None;
    for raw_line in yaml.replace("\r\n", "\n").split('\n') {
        let line = raw_line.trim_end();
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') || trimmed.eq_ignore_ascii_case("Sources:") {
            continue;
        }

        if let Some(remainder) = trimmed.strip_prefix("- ") {
            if let Some(entry) = current.take() {
                entries.push(entry);
            }

            let mut entry = BTreeMap::new();
            add_packaged_yaml_key_value(&mut entry, remainder);
            current = Some(entry);
            continue;
        }

        if let Some(entry) = current.as_mut() {
            add_packaged_yaml_key_value(entry, trimmed);
        }
    }

    if let Some(entry) = current {
        entries.push(entry);
    }

    entries
}

fn add_packaged_yaml_key_value(entry: &mut BTreeMap<String, Option<String>>, line: &str) {
    let Some((key, value)) = line.split_once(':') else {
        return;
    };

    entry.insert(
        key.trim().to_owned(),
        Some(unquote_yaml_scalar(value.trim()).to_owned()),
    );
}

fn unquote_yaml_scalar(value: &str) -> &str {
    if value.len() >= 2 {
        let bytes = value.as_bytes();
        let first = bytes[0] as char;
        let last = bytes[value.len() - 1] as char;
        if (first == '"' && last == '"') || (first == '\'' && last == '\'') {
            return &value[1..value.len() - 1];
        }
    }

    value
}

fn map_packaged_source_entry(entry: &BTreeMap<String, Option<String>>) -> Option<SourceRecord> {
    let name = entry.get("Name").and_then(|value| value.clone())?;
    let source_type = entry.get("Type").and_then(|value| value.clone())?;
    let arg = entry.get("Arg").and_then(|value| value.clone())?;
    let kind = parse_packaged_source_kind(&source_type)?;

    Some(SourceRecord {
        name: name.clone(),
        kind,
        arg,
        identifier: entry
            .get("Identifier")
            .and_then(|value| value.clone())
            .or_else(|| entry.get("Data").and_then(|value| value.clone()))
            .unwrap_or(name),
        trust_level: parse_packaged_trust_level(entry.get("TrustLevel").and_then(|value| value.as_deref())),
        explicit: yaml_bool(entry.get("Explicit")),
        priority: entry
            .get("Priority")
            .and_then(|value| value.as_deref())
            .and_then(|value| value.parse::<i32>().ok())
            .unwrap_or_default(),
        last_update: None,
        source_version: None,
    })
}

fn parse_packaged_source_kind(value: &str) -> Option<SourceKind> {
    if value.eq_ignore_ascii_case("Microsoft.PreIndexed.Package") {
        Some(SourceKind::PreIndexed)
    } else if value.eq_ignore_ascii_case("Microsoft.Rest") {
        Some(SourceKind::Rest)
    } else {
        None
    }
}

fn parse_packaged_trust_level(value: Option<&str>) -> String {
    match value {
        Some(raw) if raw.eq_ignore_ascii_case("Trusted") => "Trusted".to_owned(),
        Some(raw) if raw.parse::<i64>().ok().unwrap_or_default() > 0 => "Trusted".to_owned(),
        _ => "None".to_owned(),
    }
}

fn yaml_bool(value: Option<&Option<String>>) -> bool {
    value
        .and_then(|raw| raw.as_deref())
        .and_then(|raw| raw.parse::<bool>().ok())
        .unwrap_or(false)
}

fn parse_packaged_datetime(value: &str) -> Option<DateTime<Utc>> {
    if let Ok(unix_seconds) = value.parse::<i64>() {
        return DateTime::<Utc>::from_timestamp(unix_seconds, 0);
    }

    DateTime::parse_from_rfc3339(value)
        .ok()
        .map(|value| value.with_timezone(&Utc))
}

fn render_packaged_sources_yaml(store: &SourceStore) -> String {
    let mut lines = vec!["Sources:".to_owned()];
    for source in &store.sources {
        lines.push(format!("  - Name: {}", source.name));
        lines.push(format!("    Type: {}", packaged_source_type(source.kind)));
        lines.push(format!("    Arg: {}", yaml_scalar(&source.arg)));
        lines.push(format!("    Data: {}", yaml_scalar(&source.identifier)));
        lines.push(format!(
            "    TrustLevel: {}",
            if source.trust_level.eq_ignore_ascii_case("Trusted") {
                1
            } else {
                0
            }
        ));
        lines.push(format!("    Explicit: {}", source.explicit));
        lines.push(format!("    Priority: {}", source.priority));
        lines.push("    IsTombstone: false".to_owned());
    }
    lines.push(String::new());
    lines.join("\n")
}

fn render_packaged_metadata_yaml(store: &SourceStore) -> String {
    let mut lines = vec!["Sources:".to_owned()];
    for source in &store.sources {
        if source.last_update.is_none() && source.source_version.is_none() {
            continue;
        }

        lines.push(format!("  - Name: {}", source.name));
        if let Some(last_update) = source.last_update {
            lines.push(format!("    LastUpdate: {}", last_update.timestamp()));
        }
        if let Some(source_version) = &source.source_version {
            lines.push(format!("    SourceVersion: {}", yaml_scalar(source_version)));
        }
    }
    lines.push(String::new());
    lines.join("\n")
}

fn yaml_scalar(value: &str) -> String {
    if value.chars().any(|ch| ch.is_whitespace() || ch == ':') {
        format!("\"{}\"", value.replace('"', "\\\""))
    } else {
        value.to_owned()
    }
}

fn uses_packaged_layout(app_root: &Path) -> bool {
    #[cfg(windows)]
    {
        try_get_packaged_local_state_root(app_root).is_some()
    }

    #[cfg(not(windows))]
    {
        let _ = app_root;
        false
    }
}

#[cfg(windows)]
fn try_get_packaged_local_state_root(app_root: &Path) -> Option<PathBuf> {
    let local_state = app_root.file_name()?.to_string_lossy();
    if !local_state.eq_ignore_ascii_case("LocalState") {
        return None;
    }

    let family = app_root.parent()?.file_name()?.to_string_lossy();
    if !family.eq_ignore_ascii_case(PACKAGED_FAMILY_NAME) {
        return None;
    }

    let packages = app_root.parent()?.parent()?.file_name()?.to_string_lossy();
    if !packages.eq_ignore_ascii_case("Packages") {
        return None;
    }

    Some(app_root.to_path_buf())
}

#[cfg(windows)]
fn packaged_user_sources_path(app_root: &Path) -> Result<PathBuf> {
    let _ = try_get_packaged_local_state_root(app_root).ok_or_else(|| anyhow!("not a packaged WinGet app root"))?;
    Ok(packaged_secure_settings_root()?.join("user_sources"))
}

#[cfg(not(windows))]
fn packaged_user_sources_path(_app_root: &Path) -> Result<PathBuf> {
    bail!("packaged WinGet source streams are only available on Windows")
}

#[cfg(windows)]
fn packaged_secure_settings_root() -> Result<PathBuf> {
    let program_data = std::env::var_os("PROGRAMDATA")
        .map(PathBuf::from)
        .ok_or_else(|| anyhow!("unable to determine ProgramData path"))?;
    Ok(program_data
        .join("Microsoft")
        .join("WinGet")
        .join(current_user_sid()?)
        .join("settings")
        .join("pkg")
        .join(PACKAGED_NAME))
}

#[cfg(windows)]
fn current_user_sid() -> Result<String> {
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            bail!("failed to open current process token")
        }

        let mut length = 0;
        GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut length);
        let mut buffer = vec![0u8; length as usize];
        if GetTokenInformation(token, TokenUser, buffer.as_mut_ptr() as *mut _, length, &mut length) == 0 {
            CloseHandle(token);
            bail!("failed to get current user token information")
        }

        let token_user = &*(buffer.as_ptr() as *const TOKEN_USER);
        let mut sid_ptr: *mut u16 = std::ptr::null_mut();
        if ConvertSidToStringSidW(token_user.User.Sid, &mut sid_ptr) == 0 {
            CloseHandle(token);
            bail!("failed to convert current user SID")
        }

        let mut len = 0usize;
        while *sid_ptr.add(len) != 0 {
            len += 1;
        }

        let sid = std::ffi::OsString::from_wide(std::slice::from_raw_parts(sid_ptr, len))
            .to_string_lossy()
            .into_owned();
        LocalFree(sid_ptr.cast());
        CloseHandle(token);
        Ok(sid)
    }
}

fn packaged_file_cache_root(app_root: &Path) -> PathBuf {
    app_root.join("Microsoft").join("Windows Package Manager")
}

fn cache_root(app_root: &Path) -> PathBuf {
    if uses_packaged_layout(app_root) {
        packaged_file_cache_root(app_root)
    } else {
        default_cache_root_fallback()
    }
}

fn default_cache_root_fallback() -> PathBuf {
    std::env::temp_dir().join("cache")
}

fn packaged_source_type(kind: SourceKind) -> &'static str {
    match kind {
        SourceKind::PreIndexed => "Microsoft.PreIndexed.Package",
        SourceKind::Rest => "Microsoft.Rest",
    }
}

fn sanitize_path_segment(value: &str) -> String {
    value
        .chars()
        .map(|ch| match ch {
            '\\' | '/' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => '_',
            _ => ch,
        })
        .collect()
}

fn load_json_object(path: &Path) -> Result<JsonMap<String, JsonValue>> {
    if !path.exists() {
        return Ok(JsonMap::new());
    }

    let bytes = fs::read(path).context("failed to read JSON file")?;
    let value: JsonValue = serde_json::from_slice(&bytes).context("failed to parse JSON file")?;
    Ok(match value {
        JsonValue::Object(object) => object,
        _ => JsonMap::new(),
    })
}

fn save_json_object(path: PathBuf, value: &JsonMap<String, JsonValue>) -> Result<()> {
    write_json(path, value)
}

fn write_json<T: Serialize>(path: PathBuf, value: &T) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).context("failed to create JSON parent directory")?;
    }

    let bytes = serde_json::to_vec_pretty(value).context("failed to serialize JSON")?;
    fs::write(path, bytes).context("failed to write JSON file")
}

fn merge_json_objects(
    current: &JsonMap<String, JsonValue>,
    update: &JsonMap<String, JsonValue>,
) -> JsonMap<String, JsonValue> {
    let mut merged = current.clone();
    for (key, value) in update {
        match (merged.get(key), value) {
            (Some(JsonValue::Object(current_object)), JsonValue::Object(update_object)) => {
                merged.insert(
                    key.clone(),
                    JsonValue::Object(merge_json_objects(current_object, update_object)),
                );
            }
            _ => {
                merged.insert(key.clone(), value.clone());
            }
        }
    }

    merged
}

fn json_contains(current: &JsonValue, expected: &JsonValue) -> bool {
    match expected {
        JsonValue::Object(expected_object) => match current {
            JsonValue::Object(current_object) => expected_object.iter().all(|(key, expected_child)| {
                current_object
                    .get(key)
                    .is_some_and(|current_child| json_contains(current_child, expected_child))
            }),
            _ => false,
        },
        JsonValue::Array(expected_array) => match current {
            JsonValue::Array(current_array) => {
                current_array.len() == expected_array.len()
                    && current_array
                        .iter()
                        .zip(expected_array.iter())
                        .all(|(current_child, expected_child)| json_contains(current_child, expected_child))
            }
            _ => false,
        },
        _ => current == expected,
    }
}

fn normalize_source_trust_level(trust_level: Option<&str>) -> Result<String> {
    match trust_level.unwrap_or("none").trim().to_ascii_lowercase().as_str() {
        "none" | "default" => Ok("None".to_owned()),
        "trusted" => Ok("Trusted".to_owned()),
        other => bail!("unsupported source trust level: {other}"),
    }
}

fn normalize_admin_setting_name(name: &str) -> Result<&'static str> {
    SUPPORTED_ADMIN_SETTINGS
        .iter()
        .copied()
        .find(|setting| setting.eq_ignore_ascii_case(name))
        .ok_or_else(|| anyhow!("unsupported admin setting: {name}"))
}

fn default_source_trust_level() -> String {
    "None".to_owned()
}

fn query_rows<T, F>(connection: &Connection, sql: &str, params: Vec<SqlValue>, mut map: F) -> Result<Vec<T>>
where
    F: FnMut(&SqlRow<'_>) -> Result<T>,
{
    let mut statement = connection
        .prepare(sql)
        .with_context(|| format!("failed to prepare SQL query: {sql}"))?;
    let mut rows = statement
        .query(params_from_iter(params))
        .with_context(|| format!("failed to execute SQL query: {sql}"))?;
    let mut result = Vec::new();

    while let Some(row) = rows.next().context("failed to read SQL row")? {
        result.push(map(row)?);
    }

    Ok(result)
}

fn query_optional_value<T, F>(connection: &Connection, sql: &str, params: Vec<SqlValue>, map: F) -> Result<Option<T>>
where
    F: FnOnce(&SqlRow<'_>) -> Result<T>,
{
    let mut statement = connection
        .prepare(sql)
        .with_context(|| format!("failed to prepare SQL query: {sql}"))?;
    let mut rows = statement
        .query(params_from_iter(params))
        .with_context(|| format!("failed to execute SQL query: {sql}"))?;

    match rows.next().context("failed to read SQL row")? {
        Some(row) => Ok(Some(map(row)?)),
        None => Ok(None),
    }
}

fn row_ref<'a>(row: &'a SqlRow<'_>, index: usize) -> Result<ValueRef<'a>> {
    row.get_ref(index)
        .with_context(|| format!("failed to read SQL column {index}"))
}

fn row_string(row: &SqlRow<'_>, index: usize) -> Result<String> {
    match row_ref(row, index)? {
        ValueRef::Text(value) => std::str::from_utf8(value)
            .context("SQL text column was not valid UTF-8")
            .map(str::to_owned),
        ValueRef::Integer(value) => Ok(value.to_string()),
        ValueRef::Real(value) => Ok(value.to_string()),
        ValueRef::Blob(value) => String::from_utf8(value.to_vec()).context("SQL blob column was not valid UTF-8"),
        ValueRef::Null => bail!("SQL column {index} was unexpectedly NULL"),
    }
}

fn row_opt_string(row: &SqlRow<'_>, index: usize) -> Result<Option<String>> {
    match row_ref(row, index)? {
        ValueRef::Null => Ok(None),
        ValueRef::Text(value) => Ok(Some(
            std::str::from_utf8(value)
                .context("SQL text column was not valid UTF-8")?
                .to_owned(),
        )),
        ValueRef::Integer(value) => Ok(Some(value.to_string())),
        ValueRef::Real(value) => Ok(Some(value.to_string())),
        ValueRef::Blob(value) => Ok(Some(
            String::from_utf8(value.to_vec()).context("SQL blob column was not valid UTF-8")?,
        )),
    }
}

fn row_i64(row: &SqlRow<'_>, index: usize) -> Result<i64> {
    match row_ref(row, index)? {
        ValueRef::Integer(value) => Ok(value),
        ValueRef::Text(value) => std::str::from_utf8(value)
            .context("SQL text column was not valid UTF-8")?
            .parse::<i64>()
            .with_context(|| format!("failed to parse integer from SQL text column {index}")),
        ValueRef::Real(value) => f64_to_i64_checked(value, index),
        ValueRef::Null => bail!("SQL column {index} was unexpectedly NULL"),
        ValueRef::Blob(_) => bail!("SQL column {index} was unexpectedly a blob"),
    }
}

fn row_hex_string(row: &SqlRow<'_>, index: usize) -> Result<String> {
    match row_ref(row, index)? {
        ValueRef::Blob(value) => Ok(bytes_to_hex(value)),
        ValueRef::Text(value) => Ok(std::str::from_utf8(value)
            .context("SQL text column was not valid UTF-8")?
            .to_ascii_lowercase()),
        ValueRef::Null => bail!("SQL column {index} was unexpectedly NULL"),
        ValueRef::Integer(value) => Ok(format!("{value:x}")),
        ValueRef::Real(value) => Ok(format!("{:x}", f64_to_i64_checked(value, index)?)),
    }
}

fn row_opt_hex_string(row: &SqlRow<'_>, index: usize) -> Result<Option<String>> {
    match row_ref(row, index)? {
        ValueRef::Null => Ok(None),
        ValueRef::Blob(value) => Ok(Some(bytes_to_hex(value))),
        ValueRef::Text(value) => Ok(Some(
            std::str::from_utf8(value)
                .context("SQL text column was not valid UTF-8")?
                .to_ascii_lowercase(),
        )),
        ValueRef::Integer(value) => Ok(Some(format!("{value:x}"))),
        ValueRef::Real(value) => Ok(Some(format!("{:x}", f64_to_i64_checked(value, index)?))),
    }
}

fn f64_to_i64_checked(value: f64, index: usize) -> Result<i64> {
    if !value.is_finite() {
        bail!("SQL column {index} contained a non-finite floating-point value");
    }

    if value.fract() != 0.0 {
        bail!("SQL column {index} contained a non-integer floating-point value");
    }

    if !(-9_223_372_036_854_775_808.0..=9_223_372_036_854_775_807.0).contains(&value) {
        bail!("SQL column {index} contained a floating-point value outside the i64 range");
    }

    format!("{value:.0}")
        .parse::<i64>()
        .with_context(|| format!("failed to parse integer from SQL floating-point column {index}"))
}

fn bytes_to_hex(bytes: &[u8]) -> String {
    let mut result = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        result.push_str(&format!("{byte:02x}"));
    }
    result
}

fn query_v2_matches(
    connection: &Connection,
    query: &PackageQuery,
    semantics: SearchSemantics,
) -> Result<(Vec<V2SearchRow>, bool)> {
    let (where_clause, params) = build_preindexed_where_clause(query, true, semantics);
    let limit = source_fetch_results(query, semantics);
    let sql = format!(
        "SELECT rowid, id, name, moniker, latest_version, hash \
         FROM packages WHERE {where_clause} LIMIT {}",
        limit + 1
    );
    let mut rows = query_rows(
        connection,
        &sql,
        params.into_iter().map(SqlValue::Text).collect(),
        |row| {
            Ok(V2SearchRow {
                package_rowid: row_i64(row, 0)?,
                id: row_string(row, 1)?,
                name: row_string(row, 2)?,
                moniker: row_opt_string(row, 3)?,
                version: row_string(row, 4)?,
                package_hash: row_hex_string(row, 5)?,
                match_criteria: None,
            })
        },
    )?;
    for row in &mut rows {
        row.match_criteria = infer_preindexed_match_criteria_v2(connection, row, query, semantics)?;
    }
    let truncated = rows.len() > limit;
    rows.truncate(limit);
    Ok((rows, truncated))
}

fn query_v1_matches(
    connection: &Connection,
    query: &PackageQuery,
    semantics: SearchSemantics,
) -> Result<(Vec<V1SearchRow>, bool)> {
    let (where_clause, params) = build_preindexed_where_clause(query, false, semantics);
    let limit = source_fetch_results(query, semantics);
    let sql = format!(
        "SELECT manifest.rowid, manifest.id, versions.version, channels.channel, ids.id, names.name, monikers.moniker \
         FROM manifest \
         JOIN ids ON manifest.id = ids.rowid \
         JOIN names ON manifest.name = names.rowid \
         LEFT JOIN monikers ON manifest.moniker = monikers.rowid \
         JOIN versions ON manifest.version = versions.rowid \
         JOIN channels ON manifest.channel = channels.rowid \
         WHERE {where_clause} LIMIT {}",
        limit + 1
    );
    let mut rows = query_rows(
        connection,
        &sql,
        params.into_iter().map(SqlValue::Text).collect(),
        |row| {
            Ok(V1SearchRow {
                manifest_rowid: row_i64(row, 0)?,
                package_rowid: row_i64(row, 1)?,
                version: row_string(row, 2)?,
                channel: row_string(row, 3)?,
                id: row_string(row, 4)?,
                name: row_string(row, 5)?,
                moniker: row_opt_string(row, 6)?,
                match_criteria: None,
            })
        },
    )?;
    for row in &mut rows {
        row.match_criteria = infer_preindexed_match_criteria_v1(connection, row, query, semantics)?;
    }
    let truncated = rows.len() > limit;
    rows.truncate(limit);
    Ok((rows, truncated))
}

fn group_v1_rows(rows: Vec<V1SearchRow>) -> Vec<V1SearchRow> {
    let mut grouped = BTreeMap::<i64, V1SearchRow>::new();

    for row in rows {
        match grouped.get(&row.package_rowid) {
            Some(existing)
                if compare_version_and_channel(&existing.version, &existing.channel, &row.version, &row.channel)
                    != Ordering::Less => {}
            _ => {
                grouped.insert(row.package_rowid, row);
            }
        }
    }

    grouped.into_values().collect()
}

fn query_v1_versions(connection: &Connection, package_rowid: i64) -> Result<Vec<V1VersionRow>> {
    let has_hash = table_has_column(connection, "manifest", "hash")?;
    let hash_sql = if has_hash { "manifest.hash" } else { "NULL" };
    let sql = format!(
        "SELECT versions.version, channels.channel, manifest.pathpart, {hash_sql} \
         FROM manifest \
         JOIN versions ON manifest.version = versions.rowid \
         JOIN channels ON manifest.channel = channels.rowid \
         WHERE manifest.id = ?1"
    );
    query_rows(connection, &sql, vec![SqlValue::Integer(package_rowid)], |row| {
        Ok(V1VersionRow {
            version: row_string(row, 0)?,
            channel: row_string(row, 1)?,
            pathpart_id: row_i64(row, 2)?,
            manifest_hash: row_opt_hex_string(row, 3)?,
        })
    })
}

fn resolve_v1_relative_path(connection: &Connection, pathpart_id: i64) -> Result<String> {
    let mut parts = Vec::new();
    let mut current = Some(pathpart_id);

    while let Some(id) = current {
        let (parent, pathpart) = query_optional_value(
            connection,
            "SELECT parent, pathpart FROM pathparts WHERE rowid = ?1",
            vec![SqlValue::Integer(id)],
            |row| {
                Ok((
                    row_opt_string(row, 0)?
                        .and_then(|value| value.parse::<i64>().ok())
                        .filter(|value| *value != 0),
                    row_string(row, 1)?,
                ))
            },
        )?
        .ok_or_else(|| anyhow!("failed to resolve pathpart {id}"))?;
        parts.push(pathpart);
        current = parent;
    }

    parts.reverse();
    Ok(parts.join("/"))
}

fn package_version_data_relative_path(package_id: &str, package_hash: &str) -> String {
    format!(
        "packages/{}/{}/versionData.mszyml",
        package_id,
        package_hash[..package_hash.len().min(8)].to_ascii_lowercase()
    )
}

fn can_fallback_to_v1(error: &anyhow::Error) -> bool {
    let message = format!("{error:#}").to_ascii_lowercase();
    message.contains("no such table") && message.contains("packages")
}

fn table_has_column(connection: &Connection, table: &str, column: &str) -> Result<bool> {
    for value in query_rows(connection, &format!("PRAGMA table_info({table})"), Vec::new(), |row| {
        row_string(row, 1)
    })? {
        if value.eq_ignore_ascii_case(column) {
            return Ok(true);
        }
    }

    Ok(false)
}

fn build_preindexed_where_clause(query: &PackageQuery, v2: bool, semantics: SearchSemantics) -> (String, Vec<String>) {
    let rowid_column = if v2 { "packages.rowid" } else { "manifest.rowid" };
    let id_column = if v2 { "id" } else { "ids.id" };
    let name_column = if v2 { "name" } else { "names.name" };
    let moniker_column = if v2 { "moniker" } else { "monikers.moniker" };
    let exact_match = query.exact || semantics == SearchSemantics::Single;
    let mut params = Vec::new();

    if let Some(value) = &query.id {
        return (
            single_field_condition(id_column, value, exact_match, &mut params),
            params,
        );
    }
    if let Some(value) = &query.name {
        return (
            single_field_condition(name_column, value, exact_match, &mut params),
            params,
        );
    }
    if let Some(value) = &query.moniker {
        return (
            single_field_condition(moniker_column, value, exact_match, &mut params),
            params,
        );
    }
    if let Some(value) = &query.tag {
        return (
            mapped_field_condition(v2, "tag", value, rowid_column, true, &mut params),
            params,
        );
    }
    if let Some(value) = &query.command {
        return (
            mapped_field_condition(v2, "command", value, rowid_column, true, &mut params),
            params,
        );
    }
    if let Some(value) = &query.query {
        if exact_match {
            let conditions = [
                single_field_condition(id_column, value, true, &mut params),
                single_field_condition(name_column, value, true, &mut params),
                single_field_condition(moniker_column, value, true, &mut params),
            ];
            return (format!("({})", conditions.join(" OR ")), params);
        }

        let mut conditions = vec![
            single_field_condition(id_column, value, false, &mut params),
            single_field_condition(name_column, value, false, &mut params),
            single_field_condition(moniker_column, value, false, &mut params),
        ];
        if semantics == SearchSemantics::Many {
            conditions.push(mapped_field_condition(
                v2,
                "tag",
                value,
                rowid_column,
                false,
                &mut params,
            ));
            conditions.push(mapped_field_condition(
                v2,
                "command",
                value,
                rowid_column,
                false,
                &mut params,
            ));
        }
        return (format!("({})", conditions.join(" OR ")), params);
    }

    ("1 = 1".to_owned(), Vec::new())
}

fn single_field_condition(column: &str, value: &str, exact: bool, params: &mut Vec<String>) -> String {
    params.push(match_parameter(value, exact));
    format!("{column} LIKE ?{}", params.len())
}

fn mapped_field_condition(
    v2: bool,
    value_name: &str,
    value: &str,
    rowid_column: &str,
    exact: bool,
    params: &mut Vec<String>,
) -> String {
    let (table_name, map_table_name, map_value_column, map_owner_column) = if v2 {
        (
            format!("{value_name}s2"),
            format!("{value_name}s2_map"),
            value_name.to_owned(),
            "package".to_owned(),
        )
    } else {
        (
            format!("{value_name}s"),
            format!("{value_name}s_map"),
            value_name.to_owned(),
            "manifest".to_owned(),
        )
    };
    params.push(match_parameter(value, exact));
    let parameter = params.len();
    format!(
        "EXISTS (SELECT 1 FROM {map_table_name} JOIN {table_name} ON \
         {map_table_name}.{value_name} = {table_name}.rowid \
         WHERE {map_table_name}.{map_owner_column} = {rowid_column} \
         AND {table_name}.{map_value_column} LIKE ?{parameter})"
    )
}

fn match_parameter(value: &str, exact: bool) -> String {
    if exact { value.to_owned() } else { format!("%{value}%") }
}

fn build_rest_search_body(
    query: &PackageQuery,
    info: &RestInformation,
    semantics: SearchSemantics,
) -> Result<JsonValue> {
    let mut root = serde_json::Map::new();
    root.insert(
        "MaximumResults".to_owned(),
        JsonValue::from(source_fetch_results(query, semantics) as u64),
    );
    let exact_match = query.exact || semantics == SearchSemantics::Single;

    if let Some(value) = &query.query {
        if semantics == SearchSemantics::Single {
            let mut filters = vec![
                rest_filter("PackageIdentifier", value, true),
                rest_filter("PackageName", value, true),
                rest_filter("Moniker", value, true),
            ];
            append_required_rest_filters(&mut filters, info);
            root.insert("Filters".to_owned(), JsonValue::Array(filters));
            return Ok(JsonValue::Object(root));
        }

        root.insert(
            "Query".to_owned(),
            serde_json::json!({
                "KeyWord": value,
                "MatchType": if exact_match { "Exact" } else { "Substring" },
            }),
        );
    }

    let mut filters = Vec::new();
    if let Some(value) = &query.id {
        filters.push(rest_filter("PackageIdentifier", value, exact_match));
    }
    if let Some(value) = &query.name {
        filters.push(rest_filter("PackageName", value, exact_match));
    }
    if let Some(value) = &query.moniker {
        filters.push(rest_filter("Moniker", value, exact_match));
    }
    if let Some(value) = &query.tag {
        filters.push(rest_filter("Tag", value, true));
    }
    if let Some(value) = &query.command {
        filters.push(rest_filter("Command", value, true));
    }
    append_required_rest_filters(&mut filters, info);

    if !filters.is_empty() {
        root.insert("Filters".to_owned(), JsonValue::Array(filters));
    }

    Ok(JsonValue::Object(root))
}

fn append_required_rest_filters(filters: &mut Vec<JsonValue>, info: &RestInformation) {
    if info
        .required_package_match_fields
        .iter()
        .any(|field| field.eq_ignore_ascii_case("Market"))
    {
        filters.push(rest_filter("Market", &default_market(), true));
    }
}

fn rest_filter(field: &str, value: &str, exact: bool) -> JsonValue {
    serde_json::json!({
        "PackageMatchField": field,
        "RequestMatch": {
            "KeyWord": value,
            "MatchType": if exact { "Exact" } else { "Substring" },
        }
    })
}

fn max_results(query: &PackageQuery) -> usize {
    query.count.unwrap_or(DEFAULT_MAX_RESULTS).max(1)
}

fn source_fetch_results(query: &PackageQuery, semantics: SearchSemantics) -> usize {
    match semantics {
        SearchSemantics::Many => max_results(query).max(DEFAULT_MAX_RESULTS),
        SearchSemantics::Single => max_results(query),
    }
}

fn infer_preindexed_match_criteria_v2(
    connection: &Connection,
    row: &V2SearchRow,
    query: &PackageQuery,
    semantics: SearchSemantics,
) -> Result<Option<String>> {
    infer_match_criteria(
        &row.id,
        &row.name,
        row.moniker.as_deref(),
        query,
        semantics,
        |value| {
            find_mapped_value_v2(
                connection,
                "tags2",
                "tags2_map",
                "tag",
                row.package_rowid,
                value,
                query.exact,
            )
        },
        |value| {
            find_mapped_value_v2(
                connection,
                "commands2",
                "commands2_map",
                "command",
                row.package_rowid,
                value,
                query.exact,
            )
        },
    )
}

fn infer_preindexed_match_criteria_v1(
    connection: &Connection,
    row: &V1SearchRow,
    query: &PackageQuery,
    semantics: SearchSemantics,
) -> Result<Option<String>> {
    infer_match_criteria(
        &row.id,
        &row.name,
        row.moniker.as_deref(),
        query,
        semantics,
        |value| {
            find_mapped_value_v1(
                connection,
                "tags",
                "tags_map",
                "tag",
                row.manifest_rowid,
                value,
                query.exact,
            )
        },
        |value| {
            find_mapped_value_v1(
                connection,
                "commands",
                "commands_map",
                "command",
                row.manifest_rowid,
                value,
                query.exact,
            )
        },
    )
}

fn infer_match_criteria<FTag, FCommand>(
    id: &str,
    name: &str,
    moniker: Option<&str>,
    query: &PackageQuery,
    semantics: SearchSemantics,
    find_tag: FTag,
    find_command: FCommand,
) -> Result<Option<String>>
where
    FTag: FnOnce(&str) -> Result<Option<String>>,
    FCommand: FnOnce(&str) -> Result<Option<String>>,
{
    if let Some(value) = &query.tag {
        return Ok(Some(format_match_criteria("Tag", value)));
    }
    if let Some(value) = &query.command {
        return Ok(Some(format_match_criteria("Command", value)));
    }
    if let Some(value) = &query.moniker {
        return Ok(Some(format_match_criteria("Moniker", moniker.unwrap_or(value))));
    }
    if semantics == SearchSemantics::Single {
        return Ok(None);
    }
    if let Some(value) = &query.query {
        if matches_text(id, value, query.exact) || matches_text(name, value, query.exact) {
            return Ok(None);
        }
        if let Some(moniker_value) = moniker.filter(|candidate| matches_text(candidate, value, query.exact)) {
            return Ok(Some(format_match_criteria("Moniker", moniker_value)));
        }
        if let Some(tag) = find_tag(value)? {
            return Ok(Some(format_match_criteria("Tag", &tag)));
        }
        if let Some(command) = find_command(value)? {
            return Ok(Some(format_match_criteria("Command", &command)));
        }
    }
    Ok(None)
}

fn find_mapped_value_v2(
    connection: &Connection,
    table_name: &str,
    map_table_name: &str,
    value_name: &str,
    package_rowid: i64,
    query: &str,
    exact: bool,
) -> Result<Option<String>> {
    let sql = format!(
        "SELECT {table_name}.{value_name} FROM {map_table_name} \
         JOIN {table_name} ON {map_table_name}.{value_name} = {table_name}.rowid \
         WHERE {map_table_name}.package = ?1 AND {table_name}.{value_name} LIKE ?2"
    );
    let values = query_rows(
        connection,
        &sql,
        vec![
            SqlValue::Integer(package_rowid),
            SqlValue::Text(match_parameter(query, exact)),
        ],
        |row| row_string(row, 0),
    )?;
    Ok(select_best_text_match(values, query, exact))
}

fn find_mapped_value_v1(
    connection: &Connection,
    table_name: &str,
    map_table_name: &str,
    value_name: &str,
    manifest_rowid: i64,
    query: &str,
    exact: bool,
) -> Result<Option<String>> {
    let sql = format!(
        "SELECT {table_name}.{value_name} FROM {map_table_name} \
         JOIN {table_name} ON {map_table_name}.{value_name} = {table_name}.rowid \
         WHERE {map_table_name}.manifest = ?1 AND {table_name}.{value_name} LIKE ?2"
    );
    let values = query_rows(
        connection,
        &sql,
        vec![
            SqlValue::Integer(manifest_rowid),
            SqlValue::Text(match_parameter(query, exact)),
        ],
        |row| row_string(row, 0),
    )?;
    Ok(select_best_text_match(values, query, exact))
}

fn rest_match_criteria(item: &JsonValue, query: &PackageQuery, semantics: SearchSemantics) -> Option<String> {
    if let Some(value) = &query.tag {
        return Some(format_match_criteria("Tag", value));
    }
    if let Some(value) = &query.command {
        return Some(format_match_criteria("Command", value));
    }
    if let Some(value) = &query.moniker {
        return Some(format_match_criteria("Moniker", value));
    }
    if semantics == SearchSemantics::Single {
        return None;
    }
    let value = query.query.as_deref()?;
    if json_string(item, "PackageIdentifier")
        .as_deref()
        .is_some_and(|candidate| matches_text(candidate, value, query.exact))
        || json_string(item, "PackageName")
            .as_deref()
            .is_some_and(|candidate| matches_text(candidate, value, query.exact))
    {
        return None;
    }
    json_string(item, "Moniker")
        .filter(|candidate| matches_text(candidate, value, query.exact))
        .map(|candidate| format_match_criteria("Moniker", &candidate))
}

fn format_match_criteria(field: &str, value: &str) -> String {
    format!("{field}: {value}")
}

fn matches_text(candidate: &str, query: &str, exact: bool) -> bool {
    if exact {
        candidate.eq_ignore_ascii_case(query)
    } else {
        candidate.to_ascii_lowercase().contains(&query.to_ascii_lowercase())
    }
}

fn search_match_sort_score(candidate: &SearchMatch, query: &PackageQuery) -> usize {
    let mut score = 0;

    if let Some(value) = &query.query {
        score = score.max(score_text_match(&candidate.name, value, query.exact, 140, 50, 30));
        score = score.max(score_text_match(&candidate.id, value, query.exact, 135, 45, 25));
        if let Some(moniker) = candidate.moniker.as_deref() {
            score = score.max(score_text_match(moniker, value, query.exact, 130, 55, 35));
        }
    }

    if let Some(value) = &query.id {
        score = score.max(score_text_match(&candidate.id, value, query.exact, 220, 200, 180));
    }
    if let Some(value) = &query.name {
        score = score.max(score_text_match(&candidate.name, value, query.exact, 210, 190, 170));
    }
    if let Some(value) = &query.moniker
        && let Some(moniker) = candidate.moniker.as_deref()
    {
        score = score.max(score_text_match(moniker, value, query.exact, 205, 185, 165));
    }
    if let Some(value) = &query.tag
        && let Some(("Tag", matched_value)) = candidate.match_criteria.as_deref().and_then(parse_match_criteria)
    {
        score = score.max(score_text_match(matched_value, value, query.exact, 160, 150, 140));
    }
    if let Some(value) = &query.command
        && let Some(("Command", matched_value)) = candidate.match_criteria.as_deref().and_then(parse_match_criteria)
    {
        score = score.max(score_text_match(matched_value, value, query.exact, 155, 145, 135));
    }

    if matches!(candidate.source_kind, SourceKind::PreIndexed) {
        score += 5;
    }
    if search_match_has_unknown_version(candidate) {
        score = score.saturating_sub(10);
    }

    score
}

fn search_match_has_unknown_version(candidate: &SearchMatch) -> bool {
    candidate
        .version
        .as_deref()
        .is_some_and(|value| value.eq_ignore_ascii_case("Unknown"))
}

fn resolve_manifest_path(manifest_path: &Path) -> Result<PathBuf> {
    if manifest_path.is_file() {
        return Ok(manifest_path.to_path_buf());
    }

    if !manifest_path.is_dir() {
        bail!("Manifest path not found: {}", manifest_path.display());
    }

    let candidate = fs::read_dir(manifest_path)?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| {
            path.extension()
                .and_then(|value| value.to_str())
                .is_some_and(|ext| ext.eq_ignore_ascii_case("yaml") || ext.eq_ignore_ascii_case("yml"))
        })
        .min();

    candidate.ok_or_else(|| anyhow!("No manifest file found under: {}", manifest_path.display()))
}

fn score_text_match(
    candidate: &str,
    query: &str,
    exact: bool,
    exact_score: usize,
    prefix_score: usize,
    substring_score: usize,
) -> usize {
    if candidate.eq_ignore_ascii_case(query) {
        return exact_score;
    }
    if exact {
        return 0;
    }

    let candidate_lower = candidate.to_ascii_lowercase();
    let query_lower = query.to_ascii_lowercase();
    if candidate_lower.starts_with(&query_lower) {
        prefix_score
    } else if candidate_lower.contains(&query_lower) {
        substring_score
    } else {
        0
    }
}

fn select_best_text_match<I>(values: I, query: &str, exact: bool) -> Option<String>
where
    I: IntoIterator<Item = String>,
{
    values
        .into_iter()
        .filter_map(|candidate| {
            let score = score_text_match(&candidate, query, exact, 3, 2, 1);
            (score > 0).then_some((score, candidate))
        })
        .max_by(|(left_score, left_value), (right_score, right_value)| {
            left_score
                .cmp(right_score)
                .then_with(|| right_value.len().cmp(&left_value.len()))
                .then_with(|| right_value.to_ascii_lowercase().cmp(&left_value.to_ascii_lowercase()))
        })
        .map(|(_, candidate)| candidate)
}

fn parse_match_criteria(criteria: &str) -> Option<(&str, &str)> {
    let (field, value) = criteria.split_once(": ")?;
    Some((field, value))
}

fn parse_rest_versions(item: &JsonValue) -> Result<Vec<VersionKey>> {
    let versions = item
        .get("Versions")
        .and_then(JsonValue::as_array)
        .ok_or_else(|| anyhow!("REST search result missing Versions"))?;
    let mut result = Vec::new();
    for version in versions {
        let value = json_string(version, "PackageVersion")
            .ok_or_else(|| anyhow!("REST version entry missing PackageVersion"))?;
        let channel = json_string(version, "Channel").unwrap_or_default();
        result.push(VersionKey {
            version: value,
            channel,
        });
    }
    Ok(result)
}

fn parse_yaml_manifest_bundle(bytes: &[u8]) -> Result<(Manifest, JsonValue)> {
    let mut merged = YamlMapping::new();
    let mut documents = Vec::new();
    for document in serde_yaml::Deserializer::from_slice(bytes) {
        let value = YamlValue::deserialize(document).context("failed to deserialize YAML document")?;
        documents.push(serde_json::to_value(&value).context("failed to convert YAML manifest document")?);
        if let Some(mapping) = value.as_mapping() {
            merge_yaml_mapping(&mut merged, mapping);
        }
    }

    let id = yaml_string_from_root(&merged, "PackageIdentifier")
        .ok_or_else(|| anyhow!("manifest missing PackageIdentifier"))?;
    let version =
        yaml_string_from_root(&merged, "PackageVersion").ok_or_else(|| anyhow!("manifest missing PackageVersion"))?;
    let name = yaml_localized_string(&merged, "PackageName").ok_or_else(|| anyhow!("manifest missing PackageName"))?;

    let installers = parse_yaml_installers(&merged);

    Ok((
        Manifest {
            id,
            name,
            version,
            channel: yaml_string_from_root(&merged, "Channel").unwrap_or_default(),
            publisher: yaml_localized_string(&merged, "Publisher"),
            description: yaml_localized_string(&merged, "Description")
                .or_else(|| yaml_localized_string(&merged, "ShortDescription")),
            moniker: yaml_string_from_root(&merged, "Moniker"),
            package_url: yaml_localized_string(&merged, "PackageUrl"),
            publisher_url: yaml_localized_string(&merged, "PublisherUrl"),
            publisher_support_url: yaml_localized_string(&merged, "PublisherSupportUrl"),
            license: yaml_localized_string(&merged, "License"),
            license_url: yaml_localized_string(&merged, "LicenseUrl"),
            privacy_url: yaml_localized_string(&merged, "PrivacyUrl"),
            author: yaml_localized_string(&merged, "Author"),
            copyright: yaml_localized_string(&merged, "Copyright"),
            copyright_url: yaml_localized_string(&merged, "CopyrightUrl"),
            release_notes: yaml_localized_string(&merged, "ReleaseNotes"),
            release_notes_url: yaml_localized_string(&merged, "ReleaseNotesUrl"),
            tags: yaml_string_list(&merged, "Tags"),
            agreements: yaml_agreement_list(&merged),
            package_dependencies: yaml_package_dependencies(&merged),
            documentation: yaml_documentation_list(&merged),
            installers,
        },
        collapse_structured_document(&JsonValue::Array(documents)),
    ))
}

fn parse_yaml_manifest(bytes: &[u8]) -> Result<Manifest> {
    parse_yaml_manifest_bundle(bytes).map(|(manifest, _)| manifest)
}

fn parse_rest_manifest(bytes: &[u8], package_id: &str, version: &str, channel: &str) -> Result<(Manifest, JsonValue)> {
    let root = serde_json::from_slice::<JsonValue>(bytes).context("failed to deserialize REST manifest JSON")?;
    let data = root
        .get("Data")
        .ok_or_else(|| anyhow!("REST manifest response missing Data"))?;
    let versions = data
        .get("Versions")
        .and_then(JsonValue::as_array)
        .ok_or_else(|| anyhow!("REST manifest response missing Versions"))?;
    let selected = versions
        .iter()
        .find(|item| {
            json_string(item, "PackageVersion").as_deref() == Some(version)
                && json_string(item, "Channel").unwrap_or_default() == channel
        })
        .or_else(|| versions.first())
        .ok_or_else(|| anyhow!("REST manifest response did not contain a version payload"))?;

    let default_locale = selected
        .get("DefaultLocale")
        .ok_or_else(|| anyhow!("REST manifest response missing DefaultLocale"))?;
    let name = json_string(default_locale, "PackageName")
        .ok_or_else(|| anyhow!("REST manifest response missing PackageName"))?;
    let installer_switch_defaults = json_installer_switches(selected);
    let top_platforms = json_string_list(selected, "Platform");
    let top_minimum_os_version = json_string(selected, "MinimumOSVersion");

    let installers = selected
        .get("Installers")
        .and_then(JsonValue::as_array)
        .map(|items| {
            items
                .iter()
                .map(|item| Installer {
                    platforms: {
                        let platforms = json_string_list(item, "Platform");
                        if platforms.is_empty() {
                            top_platforms.clone()
                        } else {
                            platforms
                        }
                    },
                    minimum_os_version: json_string(item, "MinimumOSVersion")
                        .or_else(|| top_minimum_os_version.clone()),
                    architecture: json_string(item, "Architecture"),
                    installer_type: json_string(item, "InstallerType"),
                    url: json_string(item, "InstallerUrl"),
                    sha256: json_string(item, "InstallerSha256"),
                    product_code: json_string(item, "ProductCode"),
                    locale: json_string(item, "InstallerLocale"),
                    scope: json_string(item, "Scope"),
                    release_date: json_string(item, "ReleaseDate"),
                    package_family_name: json_string(item, "PackageFamilyName"),
                    upgrade_code: json_string(item, "UpgradeCode"),
                    switches: json_installer_switches(item).with_fallback(&installer_switch_defaults),
                    commands: json_string_list(item, "Commands"),
                    package_dependencies: json_package_dependencies(item),
                })
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    let manifest = Manifest {
        id: package_id.to_owned(),
        name,
        version: version.to_owned(),
        channel: channel.to_owned(),
        publisher: json_string(default_locale, "Publisher"),
        description: json_string(default_locale, "Description")
            .or_else(|| json_string(default_locale, "ShortDescription")),
        moniker: json_string(default_locale, "Moniker"),
        package_url: json_string(default_locale, "PackageUrl"),
        publisher_url: json_string(default_locale, "PublisherUrl"),
        publisher_support_url: json_string(default_locale, "PublisherSupportUrl"),
        license: json_string(default_locale, "License"),
        license_url: json_string(default_locale, "LicenseUrl"),
        privacy_url: json_string(default_locale, "PrivacyUrl"),
        author: json_string(default_locale, "Author"),
        copyright: json_string(default_locale, "Copyright"),
        copyright_url: json_string(default_locale, "CopyrightUrl"),
        release_notes: json_string(default_locale, "ReleaseNotes"),
        release_notes_url: json_string(default_locale, "ReleaseNotesUrl"),
        tags: json_string_list(default_locale, "Tags"),
        agreements: json_agreement_list(default_locale),
        package_dependencies: json_package_dependencies(selected),
        documentation: json_documentation_list(default_locale),
        installers,
    };

    Ok((
        manifest,
        build_rest_manifest_documents(data, selected, default_locale, package_id, version, channel),
    ))
}

fn collapse_structured_document(document: &JsonValue) -> JsonValue {
    match document {
        JsonValue::Array(documents) if documents.len() == 1 && is_merged_manifest_document(&documents[0]) => {
            merge_manifest_documents(&split_merged_manifest_document(&documents[0]))
        }
        JsonValue::Array(documents) => merge_manifest_documents(documents),
        JsonValue::Object(_) if is_merged_manifest_document(document) => {
            merge_manifest_documents(&split_merged_manifest_document(document))
        }
        _ => document.clone(),
    }
}

fn collapse_structured_documents(documents: &[JsonValue]) -> Vec<JsonValue> {
    documents.iter().map(collapse_structured_document).collect()
}

fn merge_manifest_documents(documents: &[JsonValue]) -> JsonValue {
    let version = documents.iter().find(|document| {
        document
            .get("ManifestType")
            .and_then(JsonValue::as_str)
            .is_some_and(|manifest_type| manifest_type.eq_ignore_ascii_case("version"))
    });
    let default_locale = documents.iter().find(|document| {
        document
            .get("ManifestType")
            .and_then(JsonValue::as_str)
            .is_some_and(|manifest_type| manifest_type.eq_ignore_ascii_case("defaultLocale"))
    });
    let installer = documents.iter().find(|document| {
        document
            .get("ManifestType")
            .and_then(JsonValue::as_str)
            .is_some_and(|manifest_type| manifest_type.eq_ignore_ascii_case("installer"))
    });

    let mut singleton = JsonMap::new();
    copy_manifest_keys(&mut singleton, version, &["PackageIdentifier", "PackageVersion"]);
    copy_all_manifest_keys_except(&mut singleton, default_locale, &["ManifestType", "ManifestVersion"]);
    copy_all_manifest_keys_except(&mut singleton, installer, &["ManifestType", "ManifestVersion"]);

    if !singleton.contains_key("PackageLocale")
        && let Some(package_locale) = default_locale
            .and_then(|document| json_string(document, "PackageLocale"))
            .or_else(|| version.and_then(|document| json_string(document, "DefaultLocale")))
    {
        singleton.insert("PackageLocale".to_owned(), JsonValue::String(package_locale));
    }

    singleton.insert("ManifestType".to_owned(), JsonValue::String("singleton".to_owned()));
    singleton.insert(
        "ManifestVersion".to_owned(),
        JsonValue::String(
            installer
                .and_then(|document| json_string(document, "ManifestVersion"))
                .or_else(|| default_locale.and_then(|document| json_string(document, "ManifestVersion")))
                .or_else(|| version.and_then(|document| json_string(document, "ManifestVersion")))
                .unwrap_or_else(|| "1.10.0".to_owned()),
        ),
    );
    singleton.remove("DefaultLocale");

    JsonValue::Object(singleton)
}

fn is_merged_manifest_document(document: &JsonValue) -> bool {
    document
        .get("ManifestType")
        .and_then(JsonValue::as_str)
        .is_some_and(|manifest_type| manifest_type.eq_ignore_ascii_case("merged"))
}

fn split_merged_manifest_document(merged: &JsonValue) -> Vec<JsonValue> {
    let package_identifier = json_string(merged, "PackageIdentifier").unwrap_or_default();
    let package_version = json_string(merged, "PackageVersion").unwrap_or_default();
    let package_locale = json_string(merged, "PackageLocale").unwrap_or_else(|| "en-US".to_owned());
    let manifest_version = json_string(merged, "ManifestVersion").unwrap_or_else(|| "1.10.0".to_owned());

    let version_document = serde_json::json!({
        "PackageIdentifier": package_identifier,
        "PackageVersion": package_version,
        "DefaultLocale": package_locale,
        "ManifestType": "version",
        "ManifestVersion": manifest_version,
    });

    let mut default_locale_document = project_manifest_document(
        merged,
        &[
            "PackageIdentifier",
            "PackageVersion",
            "PackageLocale",
            "Publisher",
            "PublisherUrl",
            "PublisherSupportUrl",
            "PrivacyUrl",
            "Author",
            "PackageName",
            "PackageUrl",
            "License",
            "LicenseUrl",
            "Copyright",
            "CopyrightUrl",
            "ShortDescription",
            "Description",
            "Moniker",
            "Tags",
            "Agreements",
            "ReleaseNotes",
            "ReleaseNotesUrl",
            "PurchaseUrl",
            "InstallationNotes",
            "Documentations",
            "Icons",
        ],
    );
    default_locale_document.insert(
        "PackageIdentifier".to_owned(),
        JsonValue::String(json_string(merged, "PackageIdentifier").unwrap_or_default()),
    );
    default_locale_document.insert(
        "PackageVersion".to_owned(),
        JsonValue::String(json_string(merged, "PackageVersion").unwrap_or_default()),
    );
    default_locale_document.insert(
        "PackageLocale".to_owned(),
        JsonValue::String(json_string(merged, "PackageLocale").unwrap_or_else(|| "en-US".to_owned())),
    );
    default_locale_document.insert("ManifestType".to_owned(), JsonValue::String("defaultLocale".to_owned()));
    default_locale_document.insert(
        "ManifestVersion".to_owned(),
        JsonValue::String(json_string(merged, "ManifestVersion").unwrap_or_else(|| "1.10.0".to_owned())),
    );

    let mut installer_document = project_manifest_document(
        merged,
        &[
            "PackageIdentifier",
            "PackageVersion",
            "Channel",
            "InstallerLocale",
            "Platform",
            "MinimumOSVersion",
            "InstallerType",
            "NestedInstallerType",
            "NestedInstallerFiles",
            "Scope",
            "InstallModes",
            "InstallerSwitches",
            "InstallerSuccessCodes",
            "ExpectedReturnCodes",
            "UpgradeBehavior",
            "Commands",
            "Protocols",
            "FileExtensions",
            "Dependencies",
            "PackageFamilyName",
            "ProductCode",
            "Capabilities",
            "RestrictedCapabilities",
            "Markets",
            "InstallerAbortsTerminal",
            "ReleaseDate",
            "InstallLocationRequired",
            "RequireExplicitUpgrade",
            "DisplayInstallWarnings",
            "UnsupportedOSArchitectures",
            "UnsupportedArguments",
            "AppsAndFeaturesEntries",
            "ElevationRequirement",
            "InstallationMetadata",
            "DownloadCommandProhibited",
            "RepairBehavior",
            "ArchiveBinariesDependOnPath",
            "Authentication",
            "Installers",
        ],
    );
    installer_document.insert(
        "PackageIdentifier".to_owned(),
        JsonValue::String(json_string(merged, "PackageIdentifier").unwrap_or_default()),
    );
    installer_document.insert(
        "PackageVersion".to_owned(),
        JsonValue::String(json_string(merged, "PackageVersion").unwrap_or_default()),
    );
    installer_document.insert("ManifestType".to_owned(), JsonValue::String("installer".to_owned()));
    installer_document.insert(
        "ManifestVersion".to_owned(),
        JsonValue::String(json_string(merged, "ManifestVersion").unwrap_or_else(|| "1.10.0".to_owned())),
    );

    vec![
        version_document,
        JsonValue::Object(default_locale_document),
        JsonValue::Object(installer_document),
    ]
}

fn project_manifest_document(source: &JsonValue, keys: &[&str]) -> JsonMap<String, JsonValue> {
    let mut result = JsonMap::new();
    if let Some(source) = source.as_object() {
        for key in keys {
            if let Some(value) = source.get(*key) {
                result.insert((*key).to_owned(), value.clone());
            }
        }
    }

    result
}

fn copy_manifest_keys(target: &mut JsonMap<String, JsonValue>, source: Option<&JsonValue>, keys: &[&str]) {
    if let Some(source) = source.and_then(JsonValue::as_object) {
        for key in keys {
            if let Some(value) = source.get(*key) {
                target.insert((*key).to_owned(), value.clone());
            }
        }
    }
}

fn copy_all_manifest_keys_except(
    target: &mut JsonMap<String, JsonValue>,
    source: Option<&JsonValue>,
    excluded_keys: &[&str],
) {
    if let Some(source) = source.and_then(JsonValue::as_object) {
        for (key, value) in source {
            if !excluded_keys.iter().any(|excluded| key.eq_ignore_ascii_case(excluded)) {
                target.insert(key.clone(), value.clone());
            }
        }
    }
}

fn build_rest_manifest_documents(
    data: &JsonValue,
    installer_source: &JsonValue,
    default_locale: &JsonValue,
    package_id: &str,
    version: &str,
    channel: &str,
) -> JsonValue {
    let package_identifier = json_string(data, "PackageIdentifier").unwrap_or_else(|| package_id.to_owned());
    let package_locale = json_string(default_locale, "PackageLocale").unwrap_or_else(|| "en-US".to_owned());
    let manifest_version = json_string(installer_source, "ManifestVersion")
        .or_else(|| json_string(default_locale, "ManifestVersion"))
        .or_else(|| json_string(data, "ManifestVersion"))
        .unwrap_or_else(|| "1.10.0".to_owned());

    let mut version_document = JsonMap::new();
    version_document.insert(
        "PackageIdentifier".to_owned(),
        JsonValue::String(package_identifier.clone()),
    );
    version_document.insert("PackageVersion".to_owned(), JsonValue::String(version.to_owned()));
    version_document.insert("DefaultLocale".to_owned(), JsonValue::String(package_locale.clone()));
    version_document.insert("ManifestType".to_owned(), JsonValue::String("version".to_owned()));
    version_document.insert(
        "ManifestVersion".to_owned(),
        JsonValue::String(manifest_version.clone()),
    );

    let mut default_locale_document = default_locale.as_object().cloned().unwrap_or_else(JsonMap::new);
    default_locale_document.insert(
        "PackageIdentifier".to_owned(),
        JsonValue::String(package_identifier.clone()),
    );
    default_locale_document.insert("PackageVersion".to_owned(), JsonValue::String(version.to_owned()));
    default_locale_document.insert("PackageLocale".to_owned(), JsonValue::String(package_locale));
    default_locale_document.insert("ManifestType".to_owned(), JsonValue::String("defaultLocale".to_owned()));
    default_locale_document.insert(
        "ManifestVersion".to_owned(),
        JsonValue::String(manifest_version.clone()),
    );

    let mut installer_document = installer_source.as_object().cloned().unwrap_or_else(JsonMap::new);
    installer_document.remove("DefaultLocale");
    installer_document.remove("Locales");
    installer_document.insert("PackageIdentifier".to_owned(), JsonValue::String(package_identifier));
    installer_document.insert("PackageVersion".to_owned(), JsonValue::String(version.to_owned()));
    if !channel.is_empty() {
        installer_document.insert("Channel".to_owned(), JsonValue::String(channel.to_owned()));
    }
    installer_document.insert("ManifestType".to_owned(), JsonValue::String("installer".to_owned()));
    installer_document.insert("ManifestVersion".to_owned(), JsonValue::String(manifest_version));

    let documents = vec![
        JsonValue::Object(version_document),
        JsonValue::Object(default_locale_document),
        JsonValue::Object(installer_document),
    ];

    merge_manifest_documents(&documents)
}

fn parse_yaml_installers(root: &YamlMapping) -> Vec<Installer> {
    let base = installer_defaults(root);
    let base_switches = yaml_installer_switches(root);
    if let Some(items) = root.get(YamlValue::from("Installers")).and_then(YamlValue::as_sequence) {
        let installers = items
            .iter()
            .filter_map(YamlValue::as_mapping)
            .map(|item| {
                let mut merged = base.clone();
                merge_yaml_mapping(&mut merged, item);
                let switches = yaml_installer_switches(item).with_fallback(&base_switches);
                installer_from_yaml(&merged, switches)
            })
            .collect::<Vec<_>>();
        if !installers.is_empty() {
            return installers;
        }
    }

    if base.is_empty() {
        Vec::new()
    } else {
        vec![installer_from_yaml(&base, base_switches)]
    }
}

fn installer_defaults(root: &YamlMapping) -> YamlMapping {
    let keys = [
        "Architecture",
        "InstallerType",
        "InstallerUrl",
        "InstallerSha256",
        "ProductCode",
        "InstallerLocale",
        "Platform",
        "MinimumOSVersion",
        "Scope",
        "ReleaseDate",
        "PackageFamilyName",
        "UpgradeCode",
        "Commands",
    ];
    let mut defaults = YamlMapping::new();
    for key in keys {
        if let Some(value) = root.get(YamlValue::from(key)) {
            defaults.insert(YamlValue::from(key), value.clone());
        }
    }
    defaults
}

fn installer_from_yaml(root: &YamlMapping, switches: InstallerSwitches) -> Installer {
    Installer {
        architecture: yaml_string(root, "Architecture"),
        installer_type: yaml_string(root, "InstallerType"),
        url: yaml_string(root, "InstallerUrl"),
        sha256: yaml_string(root, "InstallerSha256"),
        product_code: yaml_string(root, "ProductCode"),
        locale: yaml_string(root, "InstallerLocale"),
        scope: yaml_string(root, "Scope"),
        release_date: yaml_string(root, "ReleaseDate"),
        package_family_name: yaml_string(root, "PackageFamilyName"),
        upgrade_code: yaml_string(root, "UpgradeCode"),
        platforms: yaml_string_list(root, "Platform"),
        minimum_os_version: yaml_string(root, "MinimumOSVersion"),
        switches,
        commands: yaml_string_list(root, "Commands"),
        package_dependencies: yaml_package_dependencies(root),
    }
}

fn yaml_installer_switches(root: &YamlMapping) -> InstallerSwitches {
    let mapping = root
        .get(YamlValue::from("InstallerSwitches"))
        .or_else(|| root.get(YamlValue::from("Switches")))
        .and_then(YamlValue::as_mapping);
    yaml_installer_switches_from_mapping(mapping)
}

fn yaml_installer_switches_from_mapping(mapping: Option<&YamlMapping>) -> InstallerSwitches {
    let string = |key: &str| mapping.and_then(|mapping| mapping.get(YamlValue::from(key)).and_then(yaml_scalar_string));

    InstallerSwitches {
        silent: string("Silent"),
        silent_with_progress: string("SilentWithProgress"),
        interactive: string("Interactive"),
        custom: string("Custom"),
        log: string("Log"),
        install_location: string("InstallLocation"),
    }
}

fn yaml_localized_string(root: &YamlMapping, key: &str) -> Option<String> {
    yaml_string(root, key).or_else(|| {
        root.get(YamlValue::from("DefaultLocale"))
            .and_then(YamlValue::as_mapping)
            .and_then(|mapping| yaml_string(mapping, key))
    })
}

fn yaml_string_from_root(root: &YamlMapping, key: &str) -> Option<String> {
    yaml_string(root, key)
}

fn yaml_string(root: &YamlMapping, key: &str) -> Option<String> {
    root.get(YamlValue::from(key)).and_then(yaml_scalar_string)
}

fn yaml_scalar_string(value: &YamlValue) -> Option<String> {
    match value {
        YamlValue::Null => None,
        YamlValue::Bool(value) => Some(value.to_string()),
        YamlValue::Number(value) => Some(value.to_string()),
        YamlValue::String(value) => Some(value.clone()),
        YamlValue::Tagged(tagged) => yaml_scalar_string(&tagged.value),
        YamlValue::Sequence(_) | YamlValue::Mapping(_) => None,
    }
}

fn yaml_string_list(root: &YamlMapping, key: &str) -> Vec<String> {
    root.get(YamlValue::from(key))
        .and_then(YamlValue::as_sequence)
        .map(|items| items.iter().filter_map(YamlValue::as_str).map(str::to_owned).collect())
        .unwrap_or_default()
}

fn yaml_documentation_list(root: &YamlMapping) -> Vec<Documentation> {
    root.get(YamlValue::from("Documentations"))
        .and_then(YamlValue::as_sequence)
        .map(|items| {
            items
                .iter()
                .filter_map(YamlValue::as_mapping)
                .filter_map(|item| {
                    let url = yaml_string(item, "DocumentUrl")?;
                    Some(Documentation {
                        label: yaml_string(item, "DocumentLabel"),
                        url,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

fn yaml_agreement_list(root: &YamlMapping) -> Vec<PackageAgreement> {
    root.get(YamlValue::from("Agreements"))
        .and_then(YamlValue::as_sequence)
        .map(|items| {
            items
                .iter()
                .filter_map(YamlValue::as_mapping)
                .map(|item| PackageAgreement {
                    label: yaml_string(item, "AgreementLabel"),
                    text: yaml_string(item, "Agreement"),
                    url: yaml_string(item, "AgreementUrl"),
                })
                .collect()
        })
        .unwrap_or_default()
}

fn yaml_package_dependencies(root: &YamlMapping) -> Vec<String> {
    root.get(YamlValue::from("Dependencies"))
        .and_then(YamlValue::as_mapping)
        .and_then(|deps| deps.get(YamlValue::from("PackageDependencies")))
        .and_then(YamlValue::as_sequence)
        .map(|items| {
            items
                .iter()
                .filter_map(YamlValue::as_mapping)
                .filter_map(|item| yaml_string(item, "PackageIdentifier"))
                .collect()
        })
        .unwrap_or_default()
}

fn json_string(value: &JsonValue, key: &str) -> Option<String> {
    value.get(key).and_then(JsonValue::as_str).map(str::to_owned)
}

fn json_string_list(value: &JsonValue, key: &str) -> Vec<String> {
    value
        .get(key)
        .and_then(JsonValue::as_array)
        .map(|items| items.iter().filter_map(JsonValue::as_str).map(str::to_owned).collect())
        .unwrap_or_default()
}

fn json_documentation_list(value: &JsonValue) -> Vec<Documentation> {
    value
        .get("Documentations")
        .and_then(JsonValue::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| {
                    let url = json_string(item, "DocumentUrl")?;
                    Some(Documentation {
                        label: json_string(item, "DocumentLabel"),
                        url,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

fn json_agreement_list(value: &JsonValue) -> Vec<PackageAgreement> {
    value
        .get("Agreements")
        .and_then(JsonValue::as_array)
        .map(|items| {
            items
                .iter()
                .map(|item| PackageAgreement {
                    label: json_string(item, "AgreementLabel"),
                    text: json_string(item, "Agreement"),
                    url: json_string(item, "AgreementUrl"),
                })
                .collect()
        })
        .unwrap_or_default()
}

fn json_package_dependencies(value: &JsonValue) -> Vec<String> {
    value
        .get("Dependencies")
        .and_then(JsonValue::as_object)
        .and_then(|deps| deps.get("PackageDependencies"))
        .and_then(JsonValue::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| json_string(item, "PackageIdentifier"))
                .collect()
        })
        .unwrap_or_default()
}

fn json_installer_switches(value: &JsonValue) -> InstallerSwitches {
    let switches = value
        .get("InstallerSwitches")
        .or_else(|| value.get("Switches"))
        .and_then(JsonValue::as_object);

    let string = |key: &str| {
        switches
            .and_then(|switches| switches.get(key))
            .and_then(JsonValue::as_str)
            .map(str::to_owned)
    };

    InstallerSwitches {
        silent: string("Silent"),
        silent_with_progress: string("SilentWithProgress"),
        interactive: string("Interactive"),
        custom: string("Custom"),
        log: string("Log"),
        install_location: string("InstallLocation"),
    }
}

fn merge_yaml_mapping(target: &mut YamlMapping, source: &YamlMapping) {
    for (key, value) in source {
        target.insert(key.clone(), value.clone());
    }
}

fn extract_zip_member(bytes: &[u8], member_name: &str) -> Result<Vec<u8>> {
    let reader = Cursor::new(bytes.to_vec());
    let mut archive = ZipArchive::new(reader).context("failed to read zip payload")?;
    let mut file = archive
        .by_name(member_name)
        .with_context(|| format!("zip payload missing {member_name}"))?;
    let mut output = Vec::new();
    file.read_to_end(&mut output)
        .with_context(|| format!("failed to read {member_name} from zip payload"))?;
    Ok(output)
}

fn decompress_mszyml(bytes: &[u8]) -> Result<String> {
    if let Ok(output) = decompress_mszyml_payload(if bytes.starts_with(b"CK") { &bytes[2..] } else { bytes }) {
        return Ok(output);
    }

    if !bytes.starts_with(b"CK")
        && let Some(offset) = bytes.windows(2).position(|window| window == b"CK")
    {
        return decompress_mszyml_payload(&bytes[offset + 2..]);
    }

    decompress_mszyml_payload(bytes).context("failed to decompress MSZIP payload")
}

fn decompress_mszyml_payload(payload: &[u8]) -> Result<String> {
    let mut decoder = flate2::read::DeflateDecoder::new(payload);
    let mut output = String::new();
    decoder.read_to_string(&mut output)?;
    Ok(output)
}

fn cache_control_max_age(response: &Response) -> u64 {
    response
        .headers()
        .get(reqwest::header::CACHE_CONTROL)
        .and_then(|value| value.to_str().ok())
        .and_then(|header| {
            header.split(',').find_map(|part| {
                let trimmed = part.trim();
                trimmed
                    .strip_prefix("max-age=")
                    .and_then(|value| value.parse::<u64>().ok())
            })
        })
        .unwrap_or(60)
}

fn choose_contract(server_versions: &[String]) -> Option<&'static str> {
    REST_SUPPORTED_CONTRACTS.iter().copied().find(|candidate| {
        let (major, minor) = major_minor(candidate);
        server_versions.iter().any(|version| {
            let (server_major, server_minor) = major_minor(version);
            server_major == major && server_minor == minor
        })
    })
}

fn major_minor(version: &str) -> (String, String) {
    let mut parts = version.split('.');
    (
        parts.next().unwrap_or_default().to_owned(),
        parts.next().unwrap_or_default().to_owned(),
    )
}

fn default_market() -> String {
    std::env::var("WINGET_RS_MARKET").unwrap_or_else(|_| DEFAULT_MARKET.to_owned())
}

fn select_installer(installers: &[Installer], query: &PackageQuery) -> Option<Installer> {
    let requested_locale = query.locale.as_deref();
    let requested_architecture = query.installer_architecture.as_deref();
    let requested_type = query.installer_type.as_deref();
    let requested_scope = query.install_scope.as_deref();
    let requested_platform = query.platform.as_deref();
    let requested_os_version = query.os_version.as_deref();
    let system_architecture = current_architecture();
    let system_platform = current_platform();
    let current_os_version = current_os_version();

    installers
        .iter()
        .enumerate()
        .filter(|(_, installer)| {
            installer_matches_requested(
                installer,
                requested_type,
                requested_scope,
                requested_platform,
                requested_os_version,
                system_platform,
                current_os_version.as_deref(),
            )
        })
        .filter(|(_, installer)| installer_matches_architecture(installer, requested_architecture, system_architecture))
        .max_by_key(|(index, installer)| {
            (
                installer_rank(installer, requested_locale, requested_architecture, system_architecture),
                std::cmp::Reverse(*index),
            )
        })
        .map(|(_, installer)| installer)
        .cloned()
}

fn installer_matches_requested(
    installer: &Installer,
    requested_type: Option<&str>,
    requested_scope: Option<&str>,
    requested_platform: Option<&str>,
    requested_os_version: Option<&str>,
    system_platform: Option<&str>,
    current_os_version: Option<&str>,
) -> bool {
    matches_optional_ci(installer.installer_type.as_deref(), requested_type)
        && matches_optional_ci(installer.scope.as_deref(), requested_scope)
        && installer_matches_platform(installer, requested_platform, system_platform)
        && installer_matches_os_version(installer, requested_os_version, current_os_version)
}

fn installer_matches_platform(
    installer: &Installer,
    requested_platform: Option<&str>,
    system_platform: Option<&str>,
) -> bool {
    if installer.platforms.is_empty() {
        return true;
    }

    let Some(effective_platform) = requested_platform.or(system_platform) else {
        return true;
    };

    installer
        .platforms
        .iter()
        .any(|platform| platform.eq_ignore_ascii_case(effective_platform))
}

fn installer_matches_os_version(
    installer: &Installer,
    requested_os_version: Option<&str>,
    current_os_version: Option<&str>,
) -> bool {
    let Some(minimum_os_version) = installer.minimum_os_version.as_deref() else {
        return true;
    };

    let Some(actual_os_version) = requested_os_version.or(current_os_version) else {
        return true;
    };

    match (
        parse_dotted_version(minimum_os_version),
        parse_dotted_version(actual_os_version),
    ) {
        (Some(minimum), Some(actual)) => actual.cmp(&minimum) != Ordering::Less,
        _ => minimum_os_version.eq_ignore_ascii_case(actual_os_version),
    }
}

fn installer_matches_architecture(
    installer: &Installer,
    requested_architecture: Option<&str>,
    system_architecture: &str,
) -> bool {
    let Some(architecture) = installer.architecture.as_deref() else {
        return true;
    };

    let architecture = architecture.to_ascii_lowercase();
    if let Some(requested) = requested_architecture {
        return architecture.eq_ignore_ascii_case(requested);
    }

    preferred_architectures(system_architecture)
        .iter()
        .any(|candidate| architecture.eq_ignore_ascii_case(candidate))
}

fn installer_rank(
    installer: &Installer,
    requested_locale: Option<&str>,
    requested_architecture: Option<&str>,
    system_architecture: &str,
) -> (i32, i32, i32) {
    let architecture_rank = architecture_rank(
        installer.architecture.as_deref(),
        requested_architecture.unwrap_or(system_architecture),
        requested_architecture.is_some(),
        system_architecture,
    );
    let locale_rank = locale_rank(installer.locale.as_deref(), requested_locale);
    let command_rank = if installer.commands.is_empty() { 0 } else { 1 };
    (architecture_rank, locale_rank, command_rank)
}

fn architecture_rank(
    installer_architecture: Option<&str>,
    preferred_architecture: &str,
    strict: bool,
    system_architecture: &str,
) -> i32 {
    let Some(value) = installer_architecture else {
        return 0;
    };
    if value.eq_ignore_ascii_case(preferred_architecture) {
        return 5;
    }
    if value.eq_ignore_ascii_case("neutral") {
        return 4;
    }
    if strict {
        return -1;
    }

    preferred_architectures(system_architecture)
        .iter()
        .rev()
        .position(|candidate| value.eq_ignore_ascii_case(candidate))
        .and_then(|index| i32::try_from(index).ok())
        .and_then(|index| index.checked_add(1))
        .unwrap_or(-1)
}

fn preferred_architectures(system_architecture: &str) -> &'static [&'static str] {
    match system_architecture {
        "arm64" => &["arm64", "neutral", "x64", "x86"],
        "x64" => &["x64", "neutral", "x86"],
        "x86" => &["x86", "neutral"],
        _ => &["neutral"],
    }
}

fn locale_rank(installer_locale: Option<&str>, requested_locale: Option<&str>) -> i32 {
    match (installer_locale, requested_locale) {
        (Some(installer), Some(requested)) if installer.eq_ignore_ascii_case(requested) => 3,
        (Some(installer), Some(requested))
            if installer
                .split('-')
                .next()
                .zip(requested.split('-').next())
                .is_some_and(|(left, right)| left.eq_ignore_ascii_case(right)) =>
        {
            2
        }
        (None, Some(_)) => 1,
        (Some(_), None) => 1,
        (None, None) => 0,
        _ => -1,
    }
}

fn matches_optional_ci(value: Option<&str>, requested: Option<&str>) -> bool {
    match requested {
        Some(requested) => value.is_some_and(|value| value.eq_ignore_ascii_case(requested)),
        None => true,
    }
}

fn current_architecture() -> &'static str {
    match std::env::consts::ARCH {
        "x86_64" => "x64",
        "x86" => "x86",
        "aarch64" => "arm64",
        _ => "neutral",
    }
}

fn current_platform() -> Option<&'static str> {
    if cfg!(windows) { Some("Windows.Desktop") } else { None }
}

#[cfg(windows)]
fn current_os_version() -> Option<String> {
    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let key = hklm.open_subkey(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion").ok()?;
    let build: String = key.get_value("CurrentBuildNumber").ok()?;
    let ubr: u32 = key.get_value("UBR").unwrap_or(0);
    let major: u32 = key.get_value("CurrentMajorVersionNumber").unwrap_or(10);
    let minor: u32 = key.get_value("CurrentMinorVersionNumber").unwrap_or(0);
    Some(format!("{major}.{minor}.{build}.{ubr}"))
}

#[cfg(not(windows))]
fn current_os_version() -> Option<String> {
    None
}

fn parse_dotted_version(version: &str) -> Option<Vec<u64>> {
    let mut parsed = Vec::new();
    for component in version.split('.') {
        if component.is_empty() {
            continue;
        }
        parsed.push(component.parse().ok()?);
    }

    if parsed.is_empty() { None } else { Some(parsed) }
}

fn select_v1_version(
    versions: &[V1VersionRow],
    requested_version: Option<&str>,
    requested_channel: Option<&str>,
) -> Result<V1VersionRow> {
    let mut ordered = versions.to_vec();
    ordered.sort_by(|left, right| {
        compare_version_and_channel(&right.version, &right.channel, &left.version, &left.channel)
    });

    if let Some(version) = requested_version {
        let channel = requested_channel.unwrap_or_default();
        return ordered
            .iter()
            .find(|candidate| {
                candidate.version.eq_ignore_ascii_case(version)
                    && (channel.is_empty() || candidate.channel.eq_ignore_ascii_case(channel))
            })
            .cloned()
            .ok_or_else(|| anyhow!("requested version {version} was not found"));
    }

    ordered
        .into_iter()
        .next()
        .ok_or_else(|| anyhow!("package had no versions"))
}

fn select_v2_version(
    versions: &[PackageVersionDataEntry],
    requested_version: Option<&str>,
) -> Result<PackageVersionDataEntry> {
    let mut ordered = versions.to_vec();
    ordered.sort_by(|left, right| compare_version(&right.version, &left.version));

    if let Some(version) = requested_version {
        return ordered
            .iter()
            .find(|candidate| candidate.version.eq_ignore_ascii_case(version))
            .cloned()
            .ok_or_else(|| anyhow!("requested version {version} was not found"));
    }

    ordered
        .into_iter()
        .next()
        .ok_or_else(|| anyhow!("package had no V2 versions"))
}

fn select_rest_version<'a>(
    versions: &'a [VersionKey],
    requested_version: Option<&str>,
    requested_channel: Option<&str>,
) -> Result<&'a VersionKey> {
    if let Some(version) = requested_version {
        let channel = requested_channel.unwrap_or_default();
        return versions
            .iter()
            .find(|candidate| {
                candidate.version.eq_ignore_ascii_case(version)
                    && (channel.is_empty() || candidate.channel.eq_ignore_ascii_case(channel))
            })
            .ok_or_else(|| anyhow!("requested version {version} was not found"));
    }

    versions.first().ok_or_else(|| anyhow!("package had no REST versions"))
}

fn sort_versions_desc(versions: &mut [VersionKey]) {
    versions.sort_by(|left, right| {
        compare_version_and_channel(&right.version, &right.channel, &left.version, &left.channel)
    });
}

fn compare_version_and_channel(
    left_version: &str,
    left_channel: &str,
    right_version: &str,
    right_channel: &str,
) -> Ordering {
    compare_version(left_version, right_version).then_with(|| {
        left_channel
            .to_ascii_lowercase()
            .cmp(&right_channel.to_ascii_lowercase())
    })
}

fn compare_version(left: &str, right: &str) -> Ordering {
    let left_parts = tokenize_version(left);
    let right_parts = tokenize_version(right);
    let max_len = left_parts.len().max(right_parts.len());

    for index in 0..max_len {
        let left_part = left_parts.get(index).map(String::as_str).unwrap_or("0");
        let right_part = right_parts.get(index).map(String::as_str).unwrap_or("0");
        let numeric = left_part.parse::<u64>().ok().zip(right_part.parse::<u64>().ok());

        let ordering = if let Some((left_number, right_number)) = numeric {
            left_number.cmp(&right_number)
        } else {
            left_part.to_ascii_lowercase().cmp(&right_part.to_ascii_lowercase())
        };

        if ordering != Ordering::Equal {
            return ordering;
        }
    }

    Ordering::Equal
}

fn tokenize_version(value: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut current = String::new();
    let mut current_is_digit = None;

    for ch in value.chars() {
        if ch == '.' || ch == '-' || ch == '_' || ch == '+' {
            if !current.is_empty() {
                parts.push(std::mem::take(&mut current));
            }
            current_is_digit = None;
            continue;
        }

        let is_digit = ch.is_ascii_digit();
        match current_is_digit {
            Some(previous) if previous != is_digit => {
                parts.push(std::mem::take(&mut current));
                current.push(ch);
                current_is_digit = Some(is_digit);
            }
            Some(_) => current.push(ch),
            None => {
                current.push(ch);
                current_is_digit = Some(is_digit);
            }
        }
    }

    if !current.is_empty() {
        parts.push(current);
    }

    parts
}

fn verify_hash(expected_hash: &str, bytes: &[u8]) -> Result<()> {
    if !hash_matches(Some(expected_hash), bytes) {
        bail!("downloaded file hash did not match expected SHA256");
    }
    Ok(())
}

fn verify_installer_hash(expected_hash: Option<&str>, bytes: &[u8], ignore_security_hash: bool) -> Result<()> {
    let Some(expected_hash) = expected_hash else {
        return Ok(());
    };
    if ignore_security_hash {
        return Ok(());
    }

    if !hash_matches(Some(expected_hash), bytes) {
        let actual = sha256_hex(bytes);
        bail!("Installer hash mismatch. Expected: {expected_hash}, Got: {actual}");
    }
    Ok(())
}

fn hash_matches(expected_hash: Option<&str>, bytes: &[u8]) -> bool {
    expected_hash
        .map(|expected| sha256_hex(bytes).eq_ignore_ascii_case(expected))
        .unwrap_or(true)
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let mut output = String::with_capacity(digest.len() * 2);
    for byte in digest {
        output.push_str(&format!("{byte:02X}"));
    }
    output
}

fn pins_db_path(app_root: &Path) -> PathBuf {
    if uses_packaged_layout(app_root) {
        app_root.join("pinning.db")
    } else {
        app_root.join("pins.db")
    }
}

fn resolve_pin_type_column(conn: &Connection) -> Result<Option<&'static str>> {
    let mut stmt = conn.prepare("PRAGMA table_info(pin)")?;
    let mut has_current_column = false;
    let mut has_legacy_column = false;

    let column_names = stmt.query_map([], |row| row.get::<_, String>(1))?;
    for column_name in column_names {
        match column_name?.as_str() {
            "type" => has_current_column = true,
            "pin_type" => has_legacy_column = true,
            _ => {}
        }
    }

    Ok(if has_current_column {
        Some("type")
    } else if has_legacy_column {
        Some("pin_type")
    } else {
        None
    })
}

fn decode_pin_type(pin_type_int: i64) -> PinType {
    match pin_type_int {
        4 => PinType::Blocking,
        3 => PinType::Gating,
        _ => PinType::Pinning,
    }
}

#[cfg(windows)]
fn dispatch_installer(
    installer_path: &Path,
    installer_type: &str,
    request: &InstallRequest,
    manifest: &Manifest,
    installer: &Installer,
) -> Result<i32> {
    use std::process::Command;

    match installer_type {
        "msi" | "wix" => {
            let mut cmd = Command::new("msiexec");
            cmd.args(installer_command_arguments(
                installer_type,
                request,
                manifest,
                installer_path,
                installer,
            ));
            let status = cmd.status().context("failed to run msiexec")?;
            Ok(status.code().unwrap_or(-1))
        }
        "msix" | "appx" => {
            let mut cmd = Command::new("powershell");
            cmd.arg("-NoProfile")
                .arg("-Command")
                .arg(format!("Add-AppxPackage -Path '{}'", installer_path.display()));
            let status = cmd.status().context("failed to run Add-AppxPackage")?;
            Ok(status.code().unwrap_or(-1))
        }
        "zip" => {
            let target = dirs::data_local_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("Programs");
            fs::create_dir_all(&target)?;
            let file = fs::File::open(installer_path)?;
            let mut archive = ZipArchive::new(file)?;
            archive.extract(&target)?;
            Ok(0)
        }
        // exe, inno, nullsoft, burn, etc.
        _ => {
            let mut cmd = Command::new(installer_path);
            cmd.args(installer_command_arguments(
                installer_type,
                request,
                manifest,
                installer_path,
                installer,
            ));
            let status = cmd.status().context("failed to run installer")?;
            Ok(status.code().unwrap_or(-1))
        }
    }
}

#[cfg(not(windows))]
fn dispatch_installer(
    _installer_path: &Path,
    _installer_type: &str,
    _request: &InstallRequest,
    _manifest: &Manifest,
    _installer: &Installer,
) -> Result<i32> {
    bail!("Installing packages is only supported on Windows")
}

#[cfg(any(windows, test))]
fn installer_command_arguments(
    installer_type: &str,
    request: &InstallRequest,
    _manifest: &Manifest,
    installer_path: &Path,
    installer: &Installer,
) -> Vec<String> {
    if let Some(override_args) = request.override_args.as_deref() {
        return split_installer_switches(override_args);
    }

    let mut args = Vec::new();
    match installer_type {
        "msi" | "wix" => {
            args.push("/i".to_owned());
            args.push(installer_path.display().to_string());
        }
        "msix" | "appx" | "zip" => return Vec::new(),
        _ => {}
    }

    let configured = match request.mode {
        InstallerMode::Interactive => installer.switches.interactive.as_deref(),
        InstallerMode::SilentWithProgress => installer
            .switches
            .silent_with_progress
            .as_deref()
            .or(installer.switches.silent.as_deref()),
        InstallerMode::Silent => installer
            .switches
            .silent
            .as_deref()
            .or(installer.switches.silent_with_progress.as_deref()),
    };

    if let Some(value) = configured.filter(|value| !value.trim().is_empty()) {
        args.extend(split_installer_switches(value));
    } else {
        args.extend(default_installer_arguments(installer_type, request.mode));
    }

    append_switches(
        &mut args,
        resolve_template_switch(
            installer.switches.log.as_deref(),
            default_log_switch(installer_type),
            request.log_path.as_ref().map(|path| path.display().to_string()),
            "<LOGPATH>",
        ),
    );
    append_switches(&mut args, installer.switches.custom.clone());
    append_switches(&mut args, request.custom.clone());
    append_switches(
        &mut args,
        resolve_template_switch(
            installer.switches.install_location.as_deref(),
            default_install_location_switch(installer_type),
            request.install_location.clone(),
            "<INSTALLPATH>",
        ),
    );
    args
}

#[cfg(any(windows, test))]
fn default_installer_arguments(installer_type: &str, mode: InstallerMode) -> Vec<String> {
    match mode {
        InstallerMode::Interactive => Vec::new(),
        InstallerMode::SilentWithProgress => match installer_type {
            "inno" => vec![
                "/SP-".to_owned(),
                "/SILENT".to_owned(),
                "/SUPPRESSMSGBOXES".to_owned(),
                "/NORESTART".to_owned(),
            ],
            "burn" | "wix" | "msi" => vec!["/passive".to_owned(), "/norestart".to_owned()],
            "nullsoft" | "nsis" => vec!["/S".to_owned()],
            _ => vec!["/SILENT".to_owned()],
        },
        InstallerMode::Silent => match installer_type {
            "inno" => vec![
                "/SP-".to_owned(),
                "/VERYSILENT".to_owned(),
                "/SUPPRESSMSGBOXES".to_owned(),
                "/NORESTART".to_owned(),
            ],
            "burn" | "wix" | "msi" => vec!["/quiet".to_owned(), "/norestart".to_owned()],
            "nullsoft" | "nsis" => vec!["/S".to_owned()],
            _ => vec!["/S".to_owned()],
        },
    }
}

#[cfg(any(windows, test))]
fn default_log_switch(installer_type: &str) -> Option<&'static str> {
    match installer_type {
        "burn" | "wix" | "msi" => Some("/log \"<LOGPATH>\""),
        "inno" => Some("/LOG=\"<LOGPATH>\""),
        _ => None,
    }
}

#[cfg(any(windows, test))]
fn default_install_location_switch(installer_type: &str) -> Option<&'static str> {
    match installer_type {
        "burn" | "wix" | "msi" => Some("TARGETDIR=\"<INSTALLPATH>\""),
        "nullsoft" | "nsis" => Some("/D=<INSTALLPATH>"),
        "inno" => Some("/DIR=\"<INSTALLPATH>\""),
        _ => None,
    }
}

#[cfg(any(windows, test))]
fn resolve_template_switch(
    manifest_value: Option<&str>,
    fallback: Option<&str>,
    replacement: Option<String>,
    token: &str,
) -> Option<String> {
    let template = manifest_value.filter(|value| !value.trim().is_empty()).or(fallback)?;
    let replacement = replacement?;
    Some(template.replace(token, &replacement))
}

#[cfg(any(windows, test))]
fn append_switches(args: &mut Vec<String>, value: Option<String>) {
    if let Some(value) = value.filter(|value| !value.trim().is_empty()) {
        args.extend(split_installer_switches(&value));
    }
}

#[cfg(any(windows, test))]
fn split_installer_switches(value: &str) -> Vec<String> {
    let mut args = Vec::new();
    let mut current = String::new();
    let mut in_quotes = false;

    for ch in value.chars() {
        match ch {
            '"' => in_quotes = !in_quotes,
            c if c.is_whitespace() && !in_quotes => {
                if !current.is_empty() {
                    args.push(std::mem::take(&mut current));
                }
            }
            _ => current.push(ch),
        }
    }

    if !current.is_empty() {
        args.push(current);
    }

    args
}

#[cfg(windows)]
fn uninstall_package(installed: &ListMatch, request: &UninstallRequest) -> Result<i32> {
    if request.purge && request.preserve {
        bail!("--purge and --preserve cannot be used together.");
    }

    if installed
        .installer_category
        .as_deref()
        .is_some_and(|value| value.eq_ignore_ascii_case("portable"))
    {
        return uninstall_portable(installed, request);
    }

    if (request.purge || request.preserve) && !request.force {
        bail!("--purge and --preserve are currently only supported for portable packages.");
    }

    if let Some(exit_code) = try_uninstall_arp(installed, request)? {
        return Ok(exit_code);
    }

    if let Some(exit_code) = try_uninstall_msix(installed)? {
        return Ok(exit_code);
    }

    bail!(
        "No uninstall command found for installed package '{}' ({})",
        installed.name,
        installed.local_id
    );
}

#[cfg(windows)]
fn try_uninstall_arp(installed: &ListMatch, request: &UninstallRequest) -> Result<Option<i32>> {
    use std::process::Command;

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);

    let arp_paths = [
        r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    for root in [&hklm, &hkcu] {
        for arp_path in &arp_paths {
            if let Ok(key) = root.open_subkey(arp_path) {
                for subkey_name in key.enum_keys().filter_map(|k| k.ok()) {
                    if let Ok(subkey) = key.open_subkey(&subkey_name) {
                        let display_name: String = subkey.get_value("DisplayName").unwrap_or_default();
                        let product_code = subkey.get_value::<String, _>("ProductCode").ok();
                        if !registry_entry_matches_installed_package(
                            &subkey_name,
                            &display_name,
                            product_code.as_deref(),
                            installed,
                        ) {
                            continue;
                        }

                        if let Some(exit_code) =
                            try_run_msi_uninstall(installed, &subkey_name, product_code.as_deref(), request)?
                        {
                            return Ok(Some(exit_code));
                        }

                        let quiet_uninstall_cmd = subkey.get_value::<String, _>("QuietUninstallString").ok();
                        let uninstall_cmd: String = if request.mode == InstallerMode::Interactive {
                            subkey.get_value("UninstallString").ok()
                        } else {
                            quiet_uninstall_cmd
                                .clone()
                                .or_else(|| subkey.get_value("UninstallString").ok())
                        }
                        .context("No uninstall command found in registry")?;

                        if is_winget_uninstall_command(&uninstall_cmd)
                            && let Some(exit_code) = try_uninstall_via_winget(installed, request)?
                        {
                            return Ok(Some(exit_code));
                        }

                        let mut cmd = Command::new("cmd");
                        let log_path = request.log_path.as_ref().map(|value| value.display().to_string());
                        cmd.arg("/C").arg(build_uninstall_command_with_mode(
                            &uninstall_cmd,
                            request.mode,
                            quiet_uninstall_cmd.is_some(),
                            log_path.as_deref(),
                        ));
                        let status = cmd.status().context("failed to run uninstaller")?;
                        return Ok(Some(status.code().unwrap_or(-1)));
                    }
                }
            }
        }
    }

    Ok(None)
}

#[cfg(windows)]
fn try_uninstall_msix(installed: &ListMatch) -> Result<Option<i32>> {
    use std::process::Command;

    if !installed.local_id.starts_with("MSIX\\") && installed.package_family_names.is_empty() {
        return Ok(None);
    }

    let package_full_name = installed.local_id.strip_prefix("MSIX\\");
    let mut cmd = Command::new("powershell");
    cmd.arg("-NoProfile").arg("-Command").arg(build_msix_uninstall_script(
        package_full_name,
        &installed.package_family_names,
    ));
    let status = cmd.status().context("failed to run Remove-AppxPackage")?;
    Ok(Some(status.code().unwrap_or(-1)))
}

#[cfg(windows)]
fn arp_subkey_name(local_id: &str) -> Option<&str> {
    local_id.strip_prefix("ARP\\")?.splitn(3, '\\').nth(2)
}

#[cfg(windows)]
fn registry_entry_matches_installed_package(
    subkey_name: &str,
    display_name: &str,
    product_code: Option<&str>,
    installed: &ListMatch,
) -> bool {
    if let Some(arp_subkey_name) = arp_subkey_name(&installed.local_id)
        && subkey_name.eq_ignore_ascii_case(arp_subkey_name)
    {
        return true;
    }

    if installed.product_codes.iter().any(|code| {
        code.eq_ignore_ascii_case(subkey_name)
            || product_code
                .map(|value| code.eq_ignore_ascii_case(value))
                .unwrap_or(false)
    }) {
        return true;
    }

    display_name.eq_ignore_ascii_case(&installed.name)
}

#[cfg(all(windows, test))]
fn build_uninstall_command(uninstall_cmd: &str, silent: bool, has_quiet_command: bool) -> String {
    build_uninstall_command_with_mode(
        uninstall_cmd,
        if silent {
            InstallerMode::Silent
        } else {
            InstallerMode::Interactive
        },
        has_quiet_command,
        None,
    )
}

#[cfg(windows)]
fn build_uninstall_command_with_mode(
    uninstall_cmd: &str,
    mode: InstallerMode,
    has_quiet_command: bool,
    log_path: Option<&str>,
) -> String {
    let uninstall_cmd = log_path
        .map(|value| uninstall_cmd.replace("<LOGPATH>", value))
        .unwrap_or_else(|| uninstall_cmd.to_owned());
    if mode == InstallerMode::Interactive || has_quiet_command {
        return uninstall_cmd;
    }

    let lower = uninstall_cmd.to_ascii_lowercase();
    if is_winget_uninstall_command_lower(&lower) {
        return uninstall_cmd;
    }
    if lower.contains("/quiet")
        || lower.contains("/passive")
        || lower.contains("/verysilent")
        || lower.contains("/silent")
        || lower.contains(" /s")
    {
        return uninstall_cmd;
    }

    format!("{uninstall_cmd} /S")
}

#[cfg(windows)]
fn try_uninstall_via_winget(installed: &ListMatch, request: &UninstallRequest) -> Result<Option<i32>> {
    let Some(args) = build_winget_uninstall_arguments(installed, request) else {
        return Ok(None);
    };

    let exit_code = run_winget_uninstall(&args)?;
    if exit_code == WINGET_PACKAGE_NOT_FOUND_EXIT_CODE && request.query.install_scope.is_some() {
        let Some(args_without_scope) = build_winget_uninstall_arguments_with_scope(installed, request, false) else {
            return Ok(Some(exit_code));
        };

        return Ok(Some(run_winget_uninstall(&args_without_scope)?));
    }

    Ok(Some(exit_code))
}

#[cfg(windows)]
fn run_winget_uninstall(args: &[String]) -> Result<i32> {
    use std::process::Command;

    let status = Command::new("winget")
        .args(args)
        .status()
        .context("failed to run delegated winget uninstall")?;
    Ok(status.code().unwrap_or(-1))
}

#[cfg(windows)]
fn build_winget_uninstall_arguments(installed: &ListMatch, request: &UninstallRequest) -> Option<Vec<String>> {
    build_winget_uninstall_arguments_with_scope(installed, request, true)
}

#[cfg(windows)]
fn build_winget_uninstall_arguments_with_scope(
    installed: &ListMatch,
    request: &UninstallRequest,
    include_scope: bool,
) -> Option<Vec<String>> {
    if installed.id.eq_ignore_ascii_case(&installed.local_id) {
        return None;
    }

    let mut args = vec![
        "uninstall".to_owned(),
        "--id".to_owned(),
        installed.id.clone(),
        "--exact".to_owned(),
        "--disable-interactivity".to_owned(),
    ];

    if let Some(source_name) = installed.source_name.as_deref().filter(|value| !value.is_empty()) {
        args.push("--source".to_owned());
        args.push(source_name.to_owned());
    }

    if include_scope && let Some(scope) = request.query.install_scope.as_deref().filter(|value| !value.is_empty()) {
        args.push("--scope".to_owned());
        args.push(scope.to_owned());
    }

    if request.mode == InstallerMode::Silent {
        args.push("--silent".to_owned());
    }

    Some(args)
}

#[cfg(windows)]
fn is_winget_uninstall_command(command: &str) -> bool {
    is_winget_uninstall_command_lower(&command.to_ascii_lowercase())
}

#[cfg(windows)]
fn is_winget_uninstall_command_lower(command: &str) -> bool {
    command.contains("winget uninstall") || command.contains("winget.exe uninstall")
}

#[cfg(windows)]
fn build_msix_uninstall_script(package_full_name: Option<&str>, package_family_names: &[String]) -> String {
    let full_name_literal = package_full_name
        .map(|value| format!("'{}'", value.replace('\'', "''")))
        .unwrap_or_else(|| "$null".to_owned());
    let family_names_literal = if package_family_names.is_empty() {
        "@()".to_owned()
    } else {
        format!(
            "@({})",
            package_family_names
                .iter()
                .map(|value| format!("'{}'", value.replace('\'', "''")))
                .collect::<Vec<_>>()
                .join(", ")
        )
    };

    format!(
        "$fullName = {full_name_literal}; \
         $familyNames = {family_names_literal}; \
         $targets = Get-AppxPackage | Where-Object {{ \
         (($fullName -ne $null) -and $_.PackageFullName -eq $fullName) -or \
         ($familyNames.Count -gt 0 -and ($familyNames -contains $_.PackageFamilyName)) }}; \
         if (-not $targets) {{ exit 1 }}; \
         $targets | Remove-AppxPackage"
    )
}

#[cfg(windows)]
fn try_run_msi_uninstall(
    installed: &ListMatch,
    subkey_name: &str,
    product_code: Option<&str>,
    request: &UninstallRequest,
) -> Result<Option<i32>> {
    use std::process::Command;

    let uninstall_code = installed
        .product_codes
        .iter()
        .map(String::as_str)
        .chain(product_code)
        .chain(Some(subkey_name))
        .find(|value| is_product_code_like(value));

    let Some(uninstall_code) = uninstall_code else {
        return Ok(None);
    };

    let mut cmd = Command::new("msiexec");
    cmd.arg("/x").arg(uninstall_code);
    match request.mode {
        InstallerMode::Interactive => {}
        InstallerMode::SilentWithProgress => {
            cmd.arg("/passive").arg("/norestart");
        }
        InstallerMode::Silent => {
            cmd.arg("/quiet").arg("/norestart");
        }
    }
    if let Some(log_path) = &request.log_path {
        cmd.arg("/log").arg(log_path);
    }

    let status = cmd.status().context("failed to run msiexec uninstall")?;
    Ok(Some(status.code().unwrap_or(-1)))
}

#[cfg(windows)]
fn uninstall_portable(installed: &ListMatch, request: &UninstallRequest) -> Result<i32> {
    let Some(location) = installed.install_location.as_deref() else {
        if request.force {
            return Ok(0);
        }
        bail!(
            "Portable package '{}' does not expose an install location.",
            installed.name
        );
    };

    if request.preserve {
        return Ok(0);
    }

    let path = Path::new(location);
    if path.is_dir() {
        fs::remove_dir_all(path)?;
        return Ok(0);
    }
    if path.is_file() {
        fs::remove_file(path)?;
        return Ok(0);
    }
    if request.force {
        return Ok(0);
    }

    bail!("Portable package location not found: {location}")
}

#[cfg(windows)]
fn is_product_code_like(value: &str) -> bool {
    value.starts_with('{') && value.ends_with('}')
}

#[cfg(not(windows))]
fn uninstall_package(_installed: &ListMatch, _request: &UninstallRequest) -> Result<i32> {
    bail!("Uninstalling packages is only supported on Windows")
}

#[cfg(test)]
mod tests {
    use std::io::Write;
    use std::path::{Path, PathBuf};

    use flate2::Compression;
    use flate2::write::DeflateEncoder;

    use super::*;

    fn temp_app_root(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "pinget-rs-tests-{label}-{}",
            Utc::now().timestamp_nanos_opt().unwrap_or_default()
        ))
    }

    #[test]
    fn repository_options_capture_custom_host_settings() {
        let options =
            RepositoryOptions::new(PathBuf::from(r"C:\temp\pinget-test")).with_user_agent("pinget-rs-tests/1.0");

        assert_eq!(options.app_root, PathBuf::from(r"C:\temp\pinget-test"));
        assert_eq!(options.user_agent, "pinget-rs-tests/1.0");
    }

    #[test]
    fn storage_paths_use_configured_app_root() {
        let app_root = PathBuf::from(r"C:\temp\pinget-test");
        let source = SourceRecord {
            name: "winget/test".to_owned(),
            kind: SourceKind::PreIndexed,
            arg: "https://example.com/cache".to_owned(),
            identifier: "Test.Source".to_owned(),
            trust_level: "None".to_owned(),
            explicit: false,
            priority: 0,
            last_update: None,
            source_version: None,
        };

        assert_eq!(store_path(&app_root), app_root.join("sources.json"));
        assert_eq!(
            source_state_dir(&app_root, &source),
            app_root.join("sources").join("winget_test")
        );
        assert_eq!(pins_db_path(&app_root), app_root.join("pins.db"));
    }

    #[cfg(windows)]
    #[test]
    fn packaged_layout_defaults_to_packaged_paths() {
        let app_root = default_app_root().expect("default app root");
        assert!(uses_packaged_layout(&app_root));
        assert!(
            user_settings_path(&app_root)
                .display()
                .to_string()
                .ends_with(&format!(r"Packages\{}\LocalState\settings.json", PACKAGED_FAMILY_NAME))
        );
        assert!(
            packaged_file_cache_root(&app_root)
                .display()
                .to_string()
                .ends_with(&format!(
                    r"Packages\{}\LocalState\Microsoft\Windows Package Manager",
                    PACKAGED_FAMILY_NAME
                ))
        );
    }

    #[test]
    fn packaged_source_stream_overlays_defaults_and_metadata() {
        let store = parse_packaged_source_store(
            Some(
                "Sources:\n  - Name: winget\n    Type: Microsoft.PreIndexed.Package\n    Arg: https://cdn.winget.microsoft.com/cache\n    Data: Microsoft.Winget.Source_8wekyb3d8bbwe\n    IsTombstone: true\n  - Name: corp\n    Type: Microsoft.Rest\n    Arg: https://packages.contoso.test/api\n    Data: Contoso.Rest\n    Explicit: true\n    Priority: 7\n    TrustLevel: 1\n    IsTombstone: false\n",
            ),
            Some(
                "Sources:\n  - Name: corp\n    LastUpdate: 1700000000\n    SourceVersion: 1.2.3\n",
            ),
        )
        .expect("packaged store");

        assert!(!store.sources.iter().any(|source| source.name == "winget"));
        let corp = store
            .sources
            .iter()
            .find(|source| source.name == "corp")
            .expect("corp source");
        assert_eq!(corp.kind, SourceKind::Rest);
        assert_eq!(corp.identifier, "Contoso.Rest");
        assert_eq!(corp.trust_level, "Trusted");
        assert!(corp.explicit);
        assert_eq!(corp.priority, 7);
        assert_eq!(corp.source_version.as_deref(), Some("1.2.3"));
        assert_eq!(corp.last_update, DateTime::<Utc>::from_timestamp(1_700_000_000, 0));
    }

    #[test]
    fn source_metadata_and_named_reset_round_trip() {
        let app_root = temp_app_root("source-reset");
        let result = (|| -> Result<()> {
            let mut repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
            repository.add_source_with_metadata(
                "test",
                "https://example.com/test",
                SourceKind::Rest,
                Some("trusted"),
                true,
                4,
            )?;

            repository.edit_source("test", Some(false), None)?;
            let edited = repository
                .list_sources()
                .into_iter()
                .find(|source| source.name == "test")
                .expect("source");
            assert_eq!(edited.trust_level, "Trusted");
            assert!(!edited.explicit);
            assert_eq!(edited.priority, 4);

            repository.reset_source("test")?;
            let reset = repository
                .list_sources()
                .into_iter()
                .find(|source| source.name == "test")
                .expect("source");
            assert_eq!(reset.trust_level, "Trusted");
            assert!(!reset.explicit);
            assert_eq!(reset.priority, 4);
            assert!(reset.last_update.is_none());
            assert!(reset.source_version.is_none());
            Ok(())
        })();

        let _ = fs::remove_dir_all(&app_root);
        result.expect("source metadata");
    }

    #[test]
    fn explicit_sources_are_skipped_by_default_search_resolution() {
        let app_root = temp_app_root("source-resolution");
        let result = (|| -> Result<()> {
            let mut repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
            repository.add_source_with_metadata(
                "explicit-test",
                "https://example.com/explicit",
                SourceKind::Rest,
                Some("trusted"),
                true,
                10,
            )?;

            let indexes = repository.resolve_source_indexes(None)?;
            let names: Vec<_> = indexes
                .into_iter()
                .map(|index| repository.list_sources()[index].name.clone())
                .collect();
            assert!(!names.iter().any(|name| name == "explicit-test"));
            Ok(())
        })();

        let _ = fs::remove_dir_all(&app_root);
        result.expect("source resolution");
    }

    #[test]
    fn user_and_admin_settings_round_trip() {
        let app_root = temp_app_root("settings");
        let result = (|| -> Result<()> {
            let repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
            repository.set_user_settings(
                &serde_json::json!({
                    "visual": {
                        "progressBar": "retro"
                    }
                }),
                false,
            )?;
            repository.set_user_settings(
                &serde_json::json!({
                    "experimentalFeatures": {
                        "directMSI": true
                    }
                }),
                true,
            )?;

            let user_settings = repository.get_user_settings()?;
            assert_eq!(
                user_settings["visual"]["progressBar"],
                serde_json::Value::String("retro".to_owned())
            );
            assert_eq!(
                user_settings["experimentalFeatures"]["directMSI"],
                serde_json::Value::Bool(true)
            );
            assert!(repository.test_user_settings(
                &serde_json::json!({
                    "experimentalFeatures": {
                        "directMSI": true
                    }
                }),
                true
            )?);

            repository.set_admin_setting("LocalManifestFiles", true)?;
            repository.set_admin_setting("InstallerHashOverride", true)?;
            let admin_settings = repository.get_admin_settings()?;
            assert_eq!(admin_settings["LocalManifestFiles"], serde_json::Value::Bool(true));
            assert_eq!(admin_settings["InstallerHashOverride"], serde_json::Value::Bool(true));

            repository.reset_admin_setting(Some("LocalManifestFiles"), false)?;
            let reset_one = repository.get_admin_settings()?;
            assert_eq!(reset_one["LocalManifestFiles"], serde_json::Value::Bool(false));

            repository.reset_admin_setting(None, true)?;
            let reset_all = repository.get_admin_settings()?;
            for name in Repository::supported_admin_settings() {
                assert_eq!(reset_all[*name], serde_json::Value::Bool(false));
            }
            Ok(())
        })();

        let _ = fs::remove_dir_all(&app_root);
        result.expect("settings");
    }

    #[test]
    fn show_result_structured_document_is_manifest_oriented() {
        let result = ShowResult {
            package: SearchMatch {
                source_name: "winget".to_owned(),
                source_kind: SourceKind::PreIndexed,
                id: "Test.Package".to_owned(),
                name: "Test Package".to_owned(),
                moniker: Some("testpkg".to_owned()),
                version: Some("1.2.3".to_owned()),
                channel: Some("stable".to_owned()),
                match_criteria: Some("Id".to_owned()),
            },
            manifest: Manifest {
                id: "Test.Package".to_owned(),
                name: "Test Package".to_owned(),
                version: "1.2.3".to_owned(),
                channel: "stable".to_owned(),
                publisher: Some("Contoso".to_owned()),
                description: Some("Structured output".to_owned()),
                moniker: Some("testpkg".to_owned()),
                package_url: None,
                publisher_url: None,
                publisher_support_url: None,
                license: None,
                license_url: None,
                privacy_url: None,
                author: None,
                copyright: None,
                copyright_url: None,
                release_notes: None,
                release_notes_url: None,
                tags: vec!["utility".to_owned()],
                agreements: Vec::new(),
                package_dependencies: vec!["Microsoft.VCRedist.2015+.x64".to_owned()],
                documentation: vec![Documentation {
                    label: Some("Docs".to_owned()),
                    url: "https://example.test/docs".to_owned(),
                }],
                installers: vec![Installer {
                    architecture: Some("x64".to_owned()),
                    installer_type: Some("msix".to_owned()),
                    url: Some("https://example.test/Test.Package.msix".to_owned()),
                    sha256: Some("ABC123".to_owned()),
                    product_code: None,
                    locale: Some("en-US".to_owned()),
                    scope: Some("machine".to_owned()),
                    release_date: None,
                    package_family_name: None,
                    upgrade_code: None,
                    platforms: Vec::new(),
                    minimum_os_version: None,
                    switches: InstallerSwitches {
                        silent: Some("/quiet".to_owned()),
                        ..InstallerSwitches::default()
                    },
                    commands: vec!["testpkg".to_owned()],
                    package_dependencies: vec!["Microsoft.UI.Xaml.2.8".to_owned()],
                }],
            },
            selected_installer: Some(Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("msix".to_owned()),
                url: Some("https://example.test/Test.Package.msix".to_owned()),
                sha256: Some("ABC123".to_owned()),
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("machine".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: None,
                switches: InstallerSwitches {
                    silent: Some("/quiet".to_owned()),
                    ..InstallerSwitches::default()
                },
                commands: vec!["testpkg".to_owned()],
                package_dependencies: vec!["Microsoft.UI.Xaml.2.8".to_owned()],
            }),
            cached_files: vec![PathBuf::from(r"C:\temp\cache\Test.Package.yaml")],
            warnings: vec!["cache warmed".to_owned()],
            manifest_documents: JsonValue::Array(vec![
                serde_json::json!({
                    "PackageIdentifier": "Test.Package",
                    "PackageVersion": "1.2.3",
                    "DefaultLocale": "en-US",
                    "ManifestType": "version",
                    "ManifestVersion": "1.10.0"
                }),
                serde_json::json!({
                    "PackageIdentifier": "Test.Package",
                    "PackageVersion": "1.2.3",
                    "PackageLocale": "en-US",
                    "PackageName": "Test Package",
                    "Publisher": "Example",
                    "License": "MIT",
                    "ShortDescription": "Structured output",
                    "ManifestType": "defaultLocale",
                    "ManifestVersion": "1.10.0"
                }),
                serde_json::json!({
                    "PackageIdentifier": "Test.Package",
                    "PackageVersion": "1.2.3",
                    "ManifestType": "installer",
                    "ManifestVersion": "1.10.0",
                    "Installers": [
                        {
                            "Architecture": "x64",
                            "InstallerType": "msix",
                            "InstallerUrl": "https://example.test/Test.Package.msix",
                            "InstallerSha256": "ABC123",
                            "Commands": ["testpkg"],
                            "InstallerSwitches": { "Silent": "/quiet" },
                            "Dependencies": {
                                "PackageDependencies": [
                                    { "PackageIdentifier": "Microsoft.VCRedist.2015+.x64" }
                                ]
                            }
                        }
                    ]
                }),
            ]),
        };

        let document = result.structured_document();

        assert_eq!(document["ManifestType"].as_str(), Some("singleton"));
        assert_eq!(document["ManifestVersion"].as_str(), Some("1.10.0"));
        assert_eq!(document["PackageLocale"].as_str(), Some("en-US"));
        assert_eq!(
            document["Installers"][0]["Dependencies"]["PackageDependencies"][0]["PackageIdentifier"].as_str(),
            Some("Microsoft.VCRedist.2015+.x64")
        );
        assert_eq!(document["Installers"][0]["Commands"][0].as_str(), Some("testpkg"));
        assert_eq!(
            document["Installers"][0]["InstallerSwitches"]["Silent"].as_str(),
            Some("/quiet")
        );
    }

    #[test]
    fn parse_yaml_manifest_bundle_returns_singleton_document() {
        let yaml = r#"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
---
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
PackageLocale: en-US
PackageName: Test Package
Publisher: Example
License: MIT
ShortDescription: Structured output
ManifestType: defaultLocale
ManifestVersion: 1.10.0
---
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
ManifestType: installer
ManifestVersion: 1.10.0
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.exe
    InstallerSha256: ABC123
"#;

        let (_manifest, documents) = parse_yaml_manifest_bundle(yaml.as_bytes()).expect("bundle");

        assert_eq!(documents["ManifestType"].as_str(), Some("singleton"));
        assert_eq!(documents["PackageIdentifier"].as_str(), Some("Test.Package"));
        assert_eq!(documents["PackageName"].as_str(), Some("Test Package"));
    }

    #[test]
    fn collapse_structured_documents_returns_plural_show_documents() {
        let documents = collapse_structured_documents(&[
            JsonValue::Array(vec![
                serde_json::json!({
                    "PackageIdentifier": "Test.Package.One",
                    "PackageVersion": "1.0.0",
                    "DefaultLocale": "en-US",
                    "ManifestType": "version",
                    "ManifestVersion": "1.10.0"
                }),
                serde_json::json!({
                    "PackageIdentifier": "Test.Package.One",
                    "PackageVersion": "1.0.0",
                    "PackageLocale": "en-US",
                    "PackageName": "Test Package One",
                    "ManifestType": "defaultLocale",
                    "ManifestVersion": "1.10.0"
                }),
                serde_json::json!({
                    "PackageIdentifier": "Test.Package.One",
                    "PackageVersion": "1.0.0",
                    "ManifestType": "installer",
                    "ManifestVersion": "1.10.0",
                    "Installers": [
                        {
                            "Architecture": "x64",
                            "InstallerType": "exe",
                            "InstallerUrl": "https://example.test/one.exe",
                            "InstallerSha256": "ABC123"
                        }
                    ]
                }),
            ]),
            serde_json::json!({
                "PackageIdentifier": "Test.Package.Two",
                "PackageVersion": "2.0.0",
                "PackageLocale": "en-US",
                "PackageName": "Test Package Two",
                "ManifestType": "singleton",
                "ManifestVersion": "1.12.0"
            }),
        ]);

        assert_eq!(documents.len(), 2);
        assert_eq!(documents[0]["ManifestType"].as_str(), Some("singleton"));
        assert_eq!(documents[0]["PackageIdentifier"].as_str(), Some("Test.Package.One"));
        assert_eq!(documents[0]["PackageName"].as_str(), Some("Test Package One"));
        assert_eq!(documents[1]["ManifestType"].as_str(), Some("singleton"));
        assert_eq!(documents[1]["PackageIdentifier"].as_str(), Some("Test.Package.Two"));
    }

    #[test]
    fn unsupported_action_result_marks_noop_and_warning() {
        let result = unsupported_action_result("Contoso.App", "1.2.3", "install", INSTALL_UNSUPPORTED_WARNING);

        assert!(result.success);
        assert!(result.no_op);
        assert_eq!(result.exit_code, 0);
        assert_eq!(result.warnings, vec![INSTALL_UNSUPPORTED_WARNING.to_owned()]);
    }

    #[test]
    fn create_install_no_op_result_honors_no_upgrade() {
        let mut request = InstallRequest::new(PackageQuery {
            id: Some("Contoso.App".to_owned()),
            ..PackageQuery::default()
        });
        request.no_upgrade = true;

        let manifest = Manifest {
            id: "Contoso.App".to_owned(),
            name: "Contoso App".to_owned(),
            version: "2.0.0".to_owned(),
            channel: String::new(),
            publisher: None,
            description: None,
            moniker: None,
            package_url: None,
            publisher_url: None,
            publisher_support_url: None,
            license: None,
            license_url: None,
            privacy_url: None,
            author: None,
            copyright: None,
            copyright_url: None,
            release_notes: None,
            release_notes_url: None,
            tags: Vec::new(),
            agreements: Vec::new(),
            package_dependencies: Vec::new(),
            documentation: Vec::new(),
            installers: Vec::new(),
        };
        let existing = ListMatch {
            name: "Contoso App".to_owned(),
            id: "Contoso.App".to_owned(),
            local_id: "Contoso.App".to_owned(),
            installed_version: "1.0.0".to_owned(),
            available_version: Some("2.0.0".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: None,
            installer_category: None,
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };

        let result =
            Repository::create_install_no_op_result(&request, &manifest, Some(&existing)).expect("no-op result");

        assert!(result.success);
        assert!(result.no_op);
        assert_eq!(result.version, "1.0.0");
        assert_eq!(
            result.warnings,
            vec!["Package is already installed; skipping because --no-upgrade was specified.".to_owned()]
        );
    }

    #[test]
    fn create_install_no_op_result_skips_reinstall_when_current() {
        let request = InstallRequest::new(PackageQuery {
            id: Some("Contoso.App".to_owned()),
            ..PackageQuery::default()
        });

        let manifest = Manifest {
            id: "Contoso.App".to_owned(),
            name: "Contoso App".to_owned(),
            version: "2.0.0".to_owned(),
            channel: String::new(),
            publisher: None,
            description: None,
            moniker: None,
            package_url: None,
            publisher_url: None,
            publisher_support_url: None,
            license: None,
            license_url: None,
            privacy_url: None,
            author: None,
            copyright: None,
            copyright_url: None,
            release_notes: None,
            release_notes_url: None,
            tags: Vec::new(),
            agreements: Vec::new(),
            package_dependencies: Vec::new(),
            documentation: Vec::new(),
            installers: Vec::new(),
        };
        let existing = ListMatch {
            name: "Contoso App".to_owned(),
            id: "Contoso.App".to_owned(),
            local_id: "Contoso.App".to_owned(),
            installed_version: "2.0.0".to_owned(),
            available_version: Some("2.0.0".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: None,
            installer_category: None,
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };

        let result =
            Repository::create_install_no_op_result(&request, &manifest, Some(&existing)).expect("no-op result");

        assert!(result.success);
        assert!(result.no_op);
        assert_eq!(result.version, "2.0.0");
        assert_eq!(
            result.warnings,
            vec!["Package is already installed and up to date; rerun with --force to reinstall.".to_owned()]
        );
    }

    #[test]
    fn collapse_structured_document_projects_merged_manifest() {
        let document = collapse_structured_document(&serde_json::json!({
            "PackageIdentifier": "Test.Package",
            "PackageVersion": "1.2.3",
            "PackageLocale": "en-US",
            "PackageName": "Test Package",
            "Publisher": "Example",
            "InstallerType": "exe",
            "Installers": [
                {
                    "Architecture": "x64",
                    "InstallerUrl": "https://example.test/Test.Package.exe",
                    "InstallerSha256": "ABC123"
                }
            ],
            "ManifestType": "merged",
            "ManifestVersion": "1.10.0"
        }));

        assert_eq!(document["ManifestType"].as_str(), Some("singleton"));
        assert_eq!(document["PackageIdentifier"].as_str(), Some("Test.Package"));
        assert_eq!(document["PackageName"].as_str(), Some("Test Package"));
        assert_eq!(document["InstallerType"].as_str(), Some("exe"));
        assert_eq!(document["Installers"][0]["Architecture"].as_str(), Some("x64"));
    }

    #[test]
    fn parses_fixture_manifest() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("tests")
            .join("fixtures")
            .join("ManifestV1_28-Singleton.yaml");
        let bytes = fs::read(root).expect("fixture bytes");
        let manifest = parse_yaml_manifest(&bytes).expect("manifest");
        assert_eq!(manifest.id, "microsoft.msixsdk");
        assert_eq!(manifest.version, "1.7.32");
        assert_eq!(manifest.name, "MSIX SDK");
        assert!(!manifest.installers.is_empty());
    }

    #[test]
    fn parses_installer_switches_from_yaml_manifest() {
        let yaml = r#"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
PackageName: Test Package
InstallerSwitches:
  SilentWithProgress: /SILENT
Installers:
  - Architecture: x64
    InstallerType: inno
    InstallerUrl: https://example.test/Test.Package.exe
    InstallerSha256: ABC123
    InstallerSwitches:
      Silent: /VERYSILENT
      Interactive: /HELP
"#;

        let manifest = parse_yaml_manifest(yaml.as_bytes()).expect("manifest");
        let installer = &manifest.installers[0];

        assert_eq!(installer.switches.silent_with_progress.as_deref(), Some("/SILENT"));
        assert_eq!(installer.switches.silent.as_deref(), Some("/VERYSILENT"));
        assert_eq!(installer.switches.interactive.as_deref(), Some("/HELP"));
    }

    #[test]
    fn parses_platform_and_minimum_os_version_from_yaml_manifest() {
        let yaml = r#"
PackageIdentifier: Test.Package
PackageVersion: 1.2.3
PackageName: Test Package
Platform:
  - Windows.Desktop
MinimumOSVersion: 10.0.19041.0
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Package.exe
    InstallerSha256: ABC123
  - Architecture: x64
    InstallerType: msix
    Platform:
      - Windows.Universal
    MinimumOSVersion: 10.0.22621.0
    InstallerUrl: https://example.test/Test.Package.msix
    InstallerSha256: DEF456
"#;

        let manifest = parse_yaml_manifest(yaml.as_bytes()).expect("manifest");

        assert_eq!(manifest.installers[0].platforms, vec!["Windows.Desktop".to_owned()]);
        assert_eq!(
            manifest.installers[0].minimum_os_version.as_deref(),
            Some("10.0.19041.0")
        );
        assert_eq!(manifest.installers[1].platforms, vec!["Windows.Universal".to_owned()]);
        assert_eq!(
            manifest.installers[1].minimum_os_version.as_deref(),
            Some("10.0.22621.0")
        );
    }

    #[test]
    fn installer_switch_arguments_prefer_manifest_switches() {
        let installer = Installer {
            architecture: None,
            installer_type: Some("inno".to_owned()),
            url: None,
            sha256: None,
            product_code: None,
            locale: None,
            scope: None,
            release_date: None,
            package_family_name: None,
            upgrade_code: None,
            platforms: Vec::new(),
            minimum_os_version: None,
            switches: InstallerSwitches {
                silent: Some("/mysilent".to_owned()),
                silent_with_progress: Some("/mysilentwithprogress".to_owned()),
                interactive: Some("/myinteractive".to_owned()),
                ..InstallerSwitches::default()
            },
            commands: Vec::new(),
            package_dependencies: Vec::new(),
        };
        let manifest = Manifest {
            id: "Test.Package".to_owned(),
            name: "Test Package".to_owned(),
            version: "1.0.0".to_owned(),
            channel: String::new(),
            publisher: None,
            description: None,
            moniker: None,
            package_url: None,
            publisher_url: None,
            publisher_support_url: None,
            license: None,
            license_url: None,
            privacy_url: None,
            author: None,
            copyright: None,
            copyright_url: None,
            release_notes: None,
            release_notes_url: None,
            tags: Vec::new(),
            agreements: Vec::new(),
            package_dependencies: Vec::new(),
            documentation: Vec::new(),
            installers: Vec::new(),
        };

        let mut silent_request = InstallRequest::new(PackageQuery::default());
        silent_request.mode = InstallerMode::Silent;
        let mut progress_request = InstallRequest::new(PackageQuery::default());
        progress_request.mode = InstallerMode::SilentWithProgress;
        let mut interactive_request = InstallRequest::new(PackageQuery::default());
        interactive_request.mode = InstallerMode::Interactive;
        assert_eq!(
            installer_command_arguments(
                "inno",
                &silent_request,
                &manifest,
                Path::new("installer.exe"),
                &installer
            ),
            vec!["/mysilent".to_owned()]
        );
        assert_eq!(
            installer_command_arguments(
                "inno",
                &progress_request,
                &manifest,
                Path::new("installer.exe"),
                &installer
            ),
            vec!["/mysilentwithprogress".to_owned()]
        );
        assert_eq!(
            installer_command_arguments(
                "inno",
                &interactive_request,
                &manifest,
                Path::new("installer.exe"),
                &installer
            ),
            vec!["/myinteractive".to_owned()]
        );
    }

    #[test]
    fn installer_switch_arguments_use_inno_defaults_without_manifest_switches() {
        let installer = Installer {
            architecture: None,
            installer_type: Some("inno".to_owned()),
            url: None,
            sha256: None,
            product_code: None,
            locale: None,
            scope: None,
            release_date: None,
            package_family_name: None,
            upgrade_code: None,
            platforms: Vec::new(),
            minimum_os_version: None,
            switches: InstallerSwitches::default(),
            commands: Vec::new(),
            package_dependencies: Vec::new(),
        };
        let manifest = Manifest {
            id: "Test.Package".to_owned(),
            name: "Test Package".to_owned(),
            version: "1.0.0".to_owned(),
            channel: String::new(),
            publisher: None,
            description: None,
            moniker: None,
            package_url: None,
            publisher_url: None,
            publisher_support_url: None,
            license: None,
            license_url: None,
            privacy_url: None,
            author: None,
            copyright: None,
            copyright_url: None,
            release_notes: None,
            release_notes_url: None,
            tags: Vec::new(),
            agreements: Vec::new(),
            package_dependencies: Vec::new(),
            documentation: Vec::new(),
            installers: Vec::new(),
        };
        let mut progress_request = InstallRequest::new(PackageQuery::default());
        progress_request.mode = InstallerMode::SilentWithProgress;
        let mut silent_request = InstallRequest::new(PackageQuery::default());
        silent_request.mode = InstallerMode::Silent;

        assert_eq!(
            installer_command_arguments(
                "inno",
                &progress_request,
                &manifest,
                Path::new("installer.exe"),
                &installer
            ),
            vec![
                "/SP-".to_owned(),
                "/SILENT".to_owned(),
                "/SUPPRESSMSGBOXES".to_owned(),
                "/NORESTART".to_owned()
            ]
        );
        assert_eq!(
            installer_command_arguments(
                "inno",
                &silent_request,
                &manifest,
                Path::new("installer.exe"),
                &installer
            ),
            vec![
                "/SP-".to_owned(),
                "/VERYSILENT".to_owned(),
                "/SUPPRESSMSGBOXES".to_owned(),
                "/NORESTART".to_owned()
            ]
        );
    }

    #[test]
    fn installer_command_arguments_append_manifest_and_cli_switches() {
        let installer = Installer {
            architecture: None,
            installer_type: Some("msi".to_owned()),
            url: None,
            sha256: None,
            product_code: None,
            locale: None,
            scope: None,
            release_date: None,
            package_family_name: None,
            upgrade_code: None,
            platforms: Vec::new(),
            minimum_os_version: None,
            switches: InstallerSwitches {
                custom: Some("ADDLOCAL=Core".to_owned()),
                log: Some("/log \"<LOGPATH>\"".to_owned()),
                install_location: Some("TARGETDIR=\"<INSTALLPATH>\"".to_owned()),
                ..InstallerSwitches::default()
            },
            commands: Vec::new(),
            package_dependencies: Vec::new(),
        };
        let manifest = Manifest {
            id: "ShareX.ShareX".to_owned(),
            name: "ShareX".to_owned(),
            version: "19.0.2".to_owned(),
            channel: String::new(),
            publisher: None,
            description: None,
            moniker: None,
            package_url: None,
            publisher_url: None,
            publisher_support_url: None,
            license: None,
            license_url: None,
            privacy_url: None,
            author: None,
            copyright: None,
            copyright_url: None,
            release_notes: None,
            release_notes_url: None,
            tags: Vec::new(),
            agreements: Vec::new(),
            package_dependencies: Vec::new(),
            documentation: Vec::new(),
            installers: Vec::new(),
        };
        let mut request = InstallRequest::new(PackageQuery::default());
        request.mode = InstallerMode::Silent;
        request.log_path = Some(PathBuf::from(r"C:\temp\winget.log"));
        request.custom = Some("REBOOT=ReallySuppress".to_owned());
        request.install_location = Some(r"C:\Apps\ShareX".to_owned());

        assert_eq!(
            installer_command_arguments("msi", &request, &manifest, Path::new(r"C:\temp\ShareX.msi"), &installer),
            vec![
                "/i".to_owned(),
                r"C:\temp\ShareX.msi".to_owned(),
                "/quiet".to_owned(),
                "/norestart".to_owned(),
                "/log".to_owned(),
                r"C:\temp\winget.log".to_owned(),
                "ADDLOCAL=Core".to_owned(),
                "REBOOT=ReallySuppress".to_owned(),
                r"TARGETDIR=C:\Apps\ShareX".to_owned(),
            ]
        );
    }

    #[test]
    fn decompresses_mszyml_payload() {
        let payload = "sV: 1.0.0\nvD:\n  - v: 1.2.3\n    rP: manifests/test.yaml\n    s256H: ABCD\n";
        let mut encoder = DeflateEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(payload.as_bytes()).expect("write payload");
        let compressed = encoder.finish().expect("finish payload");
        let mut mszip = b"CK".to_vec();
        mszip.extend_from_slice(&compressed);

        let decompressed = decompress_mszyml(&mszip).expect("decompress");
        let parsed = serde_yaml::from_str::<PackageVersionDataDocument>(&decompressed).expect("parse");
        assert_eq!(parsed.versions[0].version, "1.2.3");
    }

    #[test]
    fn decompresses_mszyml_payload_with_prefix() {
        let payload = "sV: 1.0.0\nvD:\n  - v: 1.2.3\n    rP: manifests/test.yaml\n    s256H: ABCD\n";
        let mut encoder = DeflateEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(payload.as_bytes()).expect("write payload");
        let compressed = encoder.finish().expect("finish payload");
        let mut prefixed = vec![0u8; 28];
        prefixed.extend_from_slice(b"CK");
        prefixed.extend_from_slice(&compressed);

        let decompressed = decompress_mszyml(&prefixed).expect("decompress");
        let parsed = serde_yaml::from_str::<PackageVersionDataDocument>(&decompressed).expect("parse");
        assert_eq!(parsed.versions[0].version, "1.2.3");
    }

    #[test]
    fn compares_versions_naturally() {
        assert_eq!(compare_version("1.10.0", "1.9.9"), Ordering::Greater);
        assert_eq!(compare_version("2.0", "10.0"), Ordering::Less);
        assert_eq!(compare_version("1.0.0-preview2", "1.0.0-preview1"), Ordering::Greater);
    }

    #[test]
    fn installer_hash_verification_fails_on_mismatch_without_override() {
        let result = verify_installer_hash(Some("DEADBEEF"), b"pinget", false);
        assert!(result.is_err());
        assert!(
            result
                .expect_err("hash mismatch")
                .to_string()
                .contains("Installer hash mismatch")
        );
    }

    #[test]
    fn installer_hash_verification_allows_mismatch_with_override() {
        verify_installer_hash(Some("DEADBEEF"), b"pinget", true).expect("hash override");
        verify_installer_hash(None, b"pinget", false).expect("missing hash");
    }

    #[test]
    fn search_query_many_includes_tag_and_command_conditions() {
        let query = PackageQuery {
            query: Some("terminal".to_owned()),
            ..PackageQuery::default()
        };

        let (where_clause, params) = build_preindexed_where_clause(&query, true, SearchSemantics::Many);

        assert!(where_clause.contains("tags2"));
        assert!(where_clause.contains("commands2"));
        assert_eq!(params.len(), 5);
    }

    #[test]
    fn show_query_single_omits_tag_and_command_conditions() {
        let query = PackageQuery {
            query: Some("terminal".to_owned()),
            ..PackageQuery::default()
        };

        let (where_clause, params) = build_preindexed_where_clause(&query, true, SearchSemantics::Single);

        assert!(!where_clause.contains("tags2"));
        assert!(!where_clause.contains("commands2"));
        assert_eq!(params.len(), 3);
    }

    #[test]
    fn explicit_tag_filter_uses_exact_match() {
        let query = PackageQuery {
            tag: Some("terminal".to_owned()),
            ..PackageQuery::default()
        };

        let (where_clause, params) = build_preindexed_where_clause(&query, true, SearchSemantics::Many);

        assert!(where_clause.contains("tags2"));
        assert_eq!(params, vec!["terminal".to_owned()]);
    }

    #[test]
    fn rest_tag_filter_uses_exact_match() {
        let query = PackageQuery {
            tag: Some("terminal".to_owned()),
            ..PackageQuery::default()
        };
        let info = RestInformation {
            required_package_match_fields: Vec::new(),
            unsupported_package_match_fields: Vec::new(),
            required_query_parameters: Vec::new(),
            ..RestInformation::default()
        };

        let body = build_rest_search_body(&query, &info, SearchSemantics::Many).expect("rest body");
        let filters = body["Filters"].as_array().expect("filters");

        assert_eq!(filters.len(), 1);
        assert_eq!(filters[0]["PackageMatchField"], "Tag");
        assert_eq!(filters[0]["RequestMatch"]["MatchType"], "Exact");
        assert_eq!(filters[0]["RequestMatch"]["KeyWord"], "terminal");
    }

    #[test]
    fn selects_installer_using_requested_filters() {
        let installers = vec![
            Installer {
                architecture: Some("x86".to_owned()),
                installer_type: Some("zip".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("msix".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: vec!["demo".to_owned()],
                package_dependencies: Vec::new(),
            },
        ];
        let query = PackageQuery {
            installer_type: Some("msix".to_owned()),
            installer_architecture: Some("x64".to_owned()),
            install_scope: Some("user".to_owned()),
            locale: Some("en-US".to_owned()),
            ..PackageQuery::default()
        };

        let selected = select_installer(&installers, &query).expect("selected installer");
        assert_eq!(selected.installer_type.as_deref(), Some("msix"));
        assert_eq!(selected.architecture.as_deref(), Some("x64"));
    }

    #[test]
    fn selects_first_installer_when_rank_ties() {
        let installers = vec![
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("machine".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
        ];

        let selected = select_installer(&installers, &PackageQuery::default()).expect("selected installer");
        assert_eq!(selected.scope.as_deref(), Some("machine"));
    }

    #[test]
    fn selects_installer_using_requested_platform() {
        let installers = vec![
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: vec!["Windows.Universal".to_owned()],
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: vec!["Windows.Desktop".to_owned()],
                minimum_os_version: None,
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
        ];
        let query = PackageQuery {
            installer_type: Some("exe".to_owned()),
            platform: Some("Windows.Desktop".to_owned()),
            ..PackageQuery::default()
        };

        let selected = select_installer(&installers, &query).expect("selected installer");
        assert_eq!(selected.platforms, vec!["Windows.Desktop".to_owned()]);
    }

    #[test]
    fn selects_installer_using_requested_os_version() {
        let installers = vec![
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: Some("10.0.22621.0".to_owned()),
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
            Installer {
                architecture: Some("x64".to_owned()),
                installer_type: Some("exe".to_owned()),
                url: None,
                sha256: None,
                product_code: None,
                locale: Some("en-US".to_owned()),
                scope: Some("user".to_owned()),
                release_date: None,
                package_family_name: None,
                upgrade_code: None,
                platforms: Vec::new(),
                minimum_os_version: Some("10.0.19041.0".to_owned()),
                switches: InstallerSwitches::default(),
                commands: Vec::new(),
                package_dependencies: Vec::new(),
            },
        ];
        let query = PackageQuery {
            installer_type: Some("exe".to_owned()),
            os_version: Some("10.0.19045.0".to_owned()),
            ..PackageQuery::default()
        };

        let selected = select_installer(&installers, &query).expect("selected installer");
        assert_eq!(selected.minimum_os_version.as_deref(), Some("10.0.19041.0"));
    }

    #[test]
    fn derives_correlation_name_candidates() {
        let candidates = correlation_name_candidates("PowerToys (Preview) x64");
        assert!(candidates.contains(&"PowerToys (Preview) x64".to_owned()));
        assert!(candidates.contains(&"PowerToys".to_owned()));
    }

    #[test]
    fn correlates_installed_package_to_available_match() {
        let installed = InstalledPackage {
            name: "PowerToys (Preview) x64".to_owned(),
            local_id: r"ARP\Machine\X64\PowerToys".to_owned(),
            installed_version: "0.98.1".to_owned(),
            publisher: None,
            scope: Some("Machine".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };
        let candidates = vec![SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "Microsoft.PowerToys".to_owned(),
            name: "PowerToys".to_owned(),
            moniker: None,
            version: Some("0.98.1".to_owned()),
            channel: None,
            match_criteria: None,
        }];

        let correlated = correlate_installed_package(&installed, &candidates, true).expect("correlated");
        assert_eq!(correlated.id, "Microsoft.PowerToys");
    }

    #[test]
    fn list_query_uses_available_lookup_for_tag_filters() {
        let query = ListQuery {
            tag: Some("terminal".to_owned()),
            ..ListQuery::default()
        };

        assert!(list_query_needs_available_lookup(&query));
    }

    #[test]
    fn list_tag_filter_requires_correlated_match() {
        let package = InstalledPackage {
            name: "PowerToys (Preview) x64".to_owned(),
            local_id: r"ARP\Machine\X64\PowerToys".to_owned(),
            installed_version: "0.98.1".to_owned(),
            publisher: None,
            scope: Some("Machine".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };
        let query = ListQuery {
            tag: Some("powertoys".to_owned()),
            ..ListQuery::default()
        };

        assert!(!list_package_matches(&package, &query));
    }

    #[test]
    fn list_lookup_ignores_display_count() {
        let query = ListQuery {
            query: Some("git".to_owned()),
            count: Some(5),
            ..ListQuery::default()
        };

        let package_query = package_query_from_list_query(&query);
        assert_eq!(package_query.count, Some(LIST_LOOKUP_MAX_RESULTS));
    }

    #[test]
    fn strict_list_correlation_avoids_short_substring_matches() {
        let installed = InstalledPackage {
            name: "GitHub".to_owned(),
            local_id: r"ARP\User\X64\GitHub".to_owned(),
            installed_version: "1.0.0".to_owned(),
            publisher: None,
            scope: Some("User".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };
        let candidates = vec![SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "Git.Git.PreRelease".to_owned(),
            name: "Git".to_owned(),
            moniker: None,
            version: Some("2.54.0".to_owned()),
            channel: None,
            match_criteria: None,
        }];

        assert!(correlate_installed_package(&installed, &candidates, false).is_none());
    }

    #[test]
    fn detects_available_upgrade_from_correlated_package() {
        let package = InstalledPackage {
            name: "AzCopy v10".to_owned(),
            local_id: r"ARP\Machine\X64\AzCopy".to_owned(),
            installed_version: "10.32.2".to_owned(),
            publisher: None,
            scope: Some("Machine".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: Some(SearchMatch {
                source_name: "winget".to_owned(),
                source_kind: SourceKind::PreIndexed,
                id: "Microsoft.Azure.AZCopy.10".to_owned(),
                name: "AzCopy v10".to_owned(),
                moniker: None,
                version: Some("10.32.3".to_owned()),
                channel: None,
                match_criteria: None,
            }),
        };

        assert!(installed_package_has_upgrade(&package));
    }

    #[test]
    fn include_unknown_treats_unknown_version_as_upgradable_when_correlated() {
        let package = InstalledPackage {
            name: "Example Tool".to_owned(),
            local_id: r"ARP\User\X64\ExampleTool".to_owned(),
            installed_version: "Unknown".to_owned(),
            publisher: None,
            scope: Some("User".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: Some(SearchMatch {
                source_name: "winget".to_owned(),
                source_kind: SourceKind::PreIndexed,
                id: "Contoso.ExampleTool".to_owned(),
                name: "Example Tool".to_owned(),
                moniker: None,
                version: Some("2.0.0".to_owned()),
                channel: None,
                match_criteria: None,
            }),
        };
        let query = ListQuery {
            upgrade_only: true,
            include_unknown: true,
            ..Default::default()
        };

        assert!(installed_package_matches_upgrade_filter(&package, &query));
        assert_eq!(
            list_match_from_installed(package).available_version.as_deref(),
            Some("2.0.0")
        );
    }

    #[test]
    fn create_repair_list_query_includes_installed_selectors() {
        let request = RepairRequest {
            query: PackageQuery {
                query: Some("powertoys".to_owned()),
                id: Some("Microsoft.PowerToys".to_owned()),
                name: Some("PowerToys".to_owned()),
                moniker: Some("powertoys".to_owned()),
                source: Some("winget".to_owned()),
                exact: true,
                version: Some("0.98.1".to_owned()),
                install_scope: Some("Machine".to_owned()),
                ..PackageQuery::default()
            },
            manifest_path: None,
            product_code: Some("{GUID}".to_owned()),
            mode: InstallerMode::Silent,
            log_path: Some(PathBuf::from("repair.log")),
            accept_package_agreements: true,
            force: true,
            ignore_security_hash: true,
        };

        let query = Repository::create_repair_list_query(&request);
        assert_eq!(query.query.as_deref(), Some("powertoys"));
        assert_eq!(query.id.as_deref(), Some("Microsoft.PowerToys"));
        assert_eq!(query.name.as_deref(), Some("PowerToys"));
        assert_eq!(query.moniker.as_deref(), Some("powertoys"));
        assert_eq!(query.product_code.as_deref(), Some("{GUID}"));
        assert_eq!(query.version.as_deref(), Some("0.98.1"));
        assert_eq!(query.source.as_deref(), Some("winget"));
        assert!(query.exact);
        assert_eq!(query.install_scope.as_deref(), Some("Machine"));
        assert_eq!(query.count, Some(100));
    }

    #[test]
    fn create_repair_install_request_forces_reinstall_of_resolved_installed_package() {
        let request = RepairRequest {
            query: PackageQuery {
                name: Some("PowerToys".to_owned()),
                source: Some("winget".to_owned()),
                locale: Some("en-US".to_owned()),
                installer_architecture: Some("x64".to_owned()),
                install_scope: Some("Machine".to_owned()),
                ..PackageQuery::default()
            },
            manifest_path: None,
            product_code: None,
            mode: InstallerMode::SilentWithProgress,
            log_path: Some(PathBuf::from("repair.log")),
            accept_package_agreements: true,
            force: false,
            ignore_security_hash: true,
        };
        let installed = ListMatch {
            name: "PowerToys".to_owned(),
            id: "Microsoft.PowerToys".to_owned(),
            local_id: r"ARP\Machine\X64\Microsoft.PowerToys".to_owned(),
            installed_version: "0.98.1".to_owned(),
            available_version: Some("0.98.2".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: Some("Machine".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };

        let install_request = Repository::create_repair_install_request(&request, Some(&installed));
        assert_eq!(install_request.query.id.as_deref(), Some("Microsoft.PowerToys"));
        assert!(install_request.query.query.is_none());
        assert!(install_request.query.name.is_none());
        assert!(install_request.query.moniker.is_none());
        assert_eq!(install_request.query.source.as_deref(), Some("winget"));
        assert_eq!(install_request.query.version.as_deref(), Some("0.98.1"));
        assert_eq!(install_request.query.locale.as_deref(), Some("en-US"));
        assert_eq!(install_request.query.installer_architecture.as_deref(), Some("x64"));
        assert_eq!(install_request.query.install_scope.as_deref(), Some("Machine"));
        assert!(install_request.query.exact);
        assert_eq!(install_request.mode, InstallerMode::SilentWithProgress);
        assert_eq!(install_request.log_path, Some(PathBuf::from("repair.log")));
        assert!(install_request.accept_package_agreements);
        assert!(install_request.force);
        assert!(install_request.ignore_security_hash);
    }

    #[test]
    fn source_scoped_pin_operations_round_trip() {
        let app_root = temp_app_root("pins");
        let result = (|| -> Result<()> {
            let repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
            repository.add_pin("Contoso.Tool", "1.0", "winget", PinType::Pinning)?;
            repository.add_pin("Contoso.Tool", "*", "test", PinType::Blocking)?;
            repository.add_pin("Fabrikam.Tool", "*", "", PinType::Pinning)?;

            assert_eq!(repository.list_pins(None)?.len(), 3);
            assert_eq!(repository.list_pins(Some("winget"))?.len(), 1);
            assert_eq!(repository.list_pins(Some("test"))?.len(), 1);

            assert!(repository.remove_pin("Contoso.Tool", Some("winget"))?);
            assert_eq!(repository.list_pins(None)?.len(), 2);
            assert!(!repository.remove_pin("Contoso.Tool", Some("winget"))?);

            repository.reset_pins(Some("test"))?;
            let remaining = repository.list_pins(None)?;
            assert_eq!(remaining.len(), 1);
            assert_eq!(remaining[0].package_id, "Fabrikam.Tool");

            repository.reset_pins(None)?;
            assert!(repository.list_pins(None)?.is_empty());
            Ok(())
        })();

        let _ = fs::remove_dir_all(&app_root);
        result.expect("pin round trip");
    }

    #[test]
    fn pin_operations_work_with_packaged_pin_schema() {
        let app_root = temp_app_root("pins_packaged_schema");
        let result = (|| -> Result<()> {
            let db_path = pins_db_path(&app_root);
            if let Some(parent) = db_path.parent() {
                fs::create_dir_all(parent)?;
            }

            let conn = Connection::open(&db_path)?;
            conn.execute_batch(
                "CREATE TABLE pin (
                    package_id TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    type INTEGER NOT NULL,
                    version TEXT NOT NULL,
                    PRIMARY KEY (package_id, source_id)
                )",
            )?;
            drop(conn);

            let repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
            repository.add_pin("Contoso.Tool", "1.2.*", "winget", PinType::Gating)?;

            let pins = repository.list_pins(None)?;
            assert_eq!(pins.len(), 1);
            assert_eq!(pins[0].package_id, "Contoso.Tool");
            assert_eq!(pins[0].version, "1.2.*");
            assert_eq!(pins[0].source_id, "winget");
            assert_eq!(pins[0].pin_type, PinType::Gating);

            let conn = Connection::open(&db_path)?;
            let stored_pin = conn.query_row(
                "SELECT type, version FROM pin WHERE package_id = ?1 AND source_id = ?2",
                rusqlite::params!["Contoso.Tool", "winget"],
                |row| Ok((row.get::<_, i64>(0)?, row.get::<_, String>(1)?)),
            )?;
            assert_eq!(stored_pin.0, 3);
            assert_eq!(stored_pin.1, "1.2.*");

            Ok(())
        })();

        let _ = fs::remove_dir_all(&app_root);
        result.expect("packaged pin schema round trip");
    }

    #[test]
    fn version_pin_patterns_match_exact_and_prefix_values() {
        assert!(version_matches_pin_pattern("1.2.3", "*"));
        assert!(version_matches_pin_pattern("1.2.3", "1.2.3"));
        assert!(version_matches_pin_pattern("1.2.3", "1.2.*"));
        assert!(!version_matches_pin_pattern("1.3.0", "1.2.*"));
        assert!(!version_matches_pin_pattern("1.2.3", "2.0.0"));
    }

    #[test]
    fn source_specific_pins_override_source_agnostic_pins() {
        let item = ListMatch {
            name: "Contoso Tool".to_owned(),
            id: "Contoso.Tool".to_owned(),
            local_id: r"ARP\User\X64\Contoso.Tool".to_owned(),
            installed_version: "1.0.0".to_owned(),
            available_version: Some("2.0.0".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: None,
            installer_category: None,
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };
        let pins = vec![
            PinRecord {
                package_id: "Contoso.Tool".to_owned(),
                version: "*".to_owned(),
                source_id: String::new(),
                pin_type: PinType::Blocking,
            },
            PinRecord {
                package_id: "Contoso.Tool".to_owned(),
                version: "2.0.*".to_owned(),
                source_id: "winget".to_owned(),
                pin_type: PinType::Pinning,
            },
        ];

        assert!(!is_upgrade_blocked_by_pin(&item, &pins));
    }

    #[test]
    fn blocking_pin_blocks_upgrade() {
        let item = ListMatch {
            name: "Contoso Tool".to_owned(),
            id: "Contoso.Tool".to_owned(),
            local_id: r"ARP\User\X64\Contoso.Tool".to_owned(),
            installed_version: "1.0.0".to_owned(),
            available_version: Some("2.0.0".to_owned()),
            source_name: Some("winget".to_owned()),
            publisher: None,
            scope: None,
            installer_category: None,
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };
        let pins = vec![PinRecord {
            package_id: "Contoso.Tool".to_owned(),
            version: "*".to_owned(),
            source_id: "winget".to_owned(),
            pin_type: PinType::Blocking,
        }];

        assert!(is_upgrade_blocked_by_pin(&item, &pins));
    }

    #[cfg(windows)]
    #[test]
    fn parses_msix_package_full_name_into_version_and_family() {
        let parsed = parse_msix_package_full_name("Microsoft.PowerToys.SparseApp_0.98.1.0_neutral__8wekyb3d8bbwe")
            .expect("package metadata");

        assert_eq!(parsed.version, "0.98.1.0");
        assert_eq!(parsed.family_name, "Microsoft.PowerToys.SparseApp_8wekyb3d8bbwe");
    }

    #[cfg(windows)]
    #[test]
    fn recognizes_windows_system_paths() {
        assert!(is_windows_system_path(r"C:\Windows\SystemApps\Contoso"));
        assert!(!is_windows_system_path(
            r"C:\Users\mamoreau\AppData\Local\PowerToys\WinUI3Apps"
        ));
    }

    #[test]
    fn search_ranking_prefers_exact_name_over_tag_match() {
        let query = PackageQuery {
            query: Some("PowerToys".to_owned()),
            ..Default::default()
        };
        let exact_name = SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "Microsoft.PowerToys".to_owned(),
            name: "PowerToys".to_owned(),
            moniker: Some("powertoys".to_owned()),
            version: Some("0.98.1".to_owned()),
            channel: None,
            match_criteria: Some("Moniker: powertoys".to_owned()),
        };
        let tag_match = SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "JiriPolasek.QRCodesforCommandPalette".to_owned(),
            name: "QR Codes for Command Palette".to_owned(),
            moniker: None,
            version: Some("0.4.0.0".to_owned()),
            channel: None,
            match_criteria: Some("Tag: microsoft-powertoys".to_owned()),
        };

        assert!(search_match_sort_score(&exact_name, &query) > search_match_sort_score(&tag_match, &query));
    }

    #[test]
    fn select_best_text_match_prefers_exact_tag_value() {
        let values = vec![
            "microsoft-powertoys".to_owned(),
            "powertoys-run".to_owned(),
            "powertoys".to_owned(),
        ];

        assert_eq!(
            select_best_text_match(values, "PowerToys", false).as_deref(),
            Some("powertoys")
        );
    }

    #[test]
    fn search_match_criteria_is_blank_for_name_match() {
        let query = PackageQuery {
            query: Some("PowerToys".to_owned()),
            ..Default::default()
        };

        let result = infer_match_criteria(
            "Microsoft.PowerToys",
            "PowerToys",
            Some("powertoys"),
            &query,
            SearchSemantics::Many,
            |_| Ok(Some("powertoys".to_owned())),
            |_| Ok(None),
        )
        .expect("match criteria");

        assert_eq!(result, None);
    }

    #[test]
    fn search_ranking_prefers_direct_name_match_even_with_unknown_version() {
        let query = PackageQuery {
            query: Some("PowerToys".to_owned()),
            ..Default::default()
        };
        let unknown_version = SearchMatch {
            source_name: "msstore".to_owned(),
            source_kind: SourceKind::Rest,
            id: "XP89DCGQ3K6VLD".to_owned(),
            name: "Microsoft PowerToys".to_owned(),
            moniker: None,
            version: Some("Unknown".to_owned()),
            channel: None,
            match_criteria: None,
        };
        let tag_match = SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "Riri.QRCodeforCmdPal".to_owned(),
            name: "QR Code for CmdPal".to_owned(),
            moniker: None,
            version: Some("0.0.1.0".to_owned()),
            channel: None,
            match_criteria: Some("Tag: powertoys".to_owned()),
        };

        assert!(search_match_sort_score(&unknown_version, &query) > search_match_sort_score(&tag_match, &query));
    }

    #[test]
    fn search_source_fetch_results_ignore_small_display_count() {
        let query = PackageQuery {
            count: Some(5),
            ..Default::default()
        };

        assert_eq!(source_fetch_results(&query, SearchSemantics::Many), 50);
        assert_eq!(source_fetch_results(&query, SearchSemantics::Single), 5);
    }

    #[test]
    fn search_ranking_prefers_direct_name_prefix_over_tag_only_match() {
        let query = PackageQuery {
            query: Some("PowerToys".to_owned()),
            ..Default::default()
        };
        let prefix_name = SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "advaith.CurrencyConverterPowerToys".to_owned(),
            name: "PowerToys-Run-Currency-Converter".to_owned(),
            moniker: None,
            version: Some("1.5.4".to_owned()),
            channel: None,
            match_criteria: None,
        };
        let exact_tag = SearchMatch {
            source_name: "winget".to_owned(),
            source_kind: SourceKind::PreIndexed,
            id: "Riri.QRCodeforCmdPal".to_owned(),
            name: "QR Code for CmdPal".to_owned(),
            moniker: None,
            version: Some("0.0.1.0".to_owned()),
            channel: None,
            match_criteria: Some("Tag: powertoys".to_owned()),
        };

        assert!(search_match_sort_score(&prefix_name, &query) > search_match_sort_score(&exact_tag, &query));
    }

    #[test]
    fn list_sort_prefers_main_package_then_sparse_app() {
        let main = InstalledPackage {
            name: "PowerToys (Preview) x64".to_owned(),
            local_id: r"ARP\User\X64\PowerToys".to_owned(),
            installed_version: "0.98.1".to_owned(),
            publisher: None,
            scope: Some("User".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };
        let sparse = InstalledPackage {
            name: "PowerToys.SparseApp".to_owned(),
            local_id: r"MSIX\Microsoft.PowerToys.SparseApp_0.98.1.0_neutral__8wekyb3d8bbwe".to_owned(),
            installed_version: "0.98.1.0".to_owned(),
            publisher: None,
            scope: Some("User".to_owned()),
            installer_category: Some("msix".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };
        let extension = InstalledPackage {
            name: "PowerToys FileLocksmith Context Menu".to_owned(),
            local_id: r"MSIX\Microsoft.PowerToys.FileLocksmithContextMenu_0.98.1.0_neutral__8wekyb3d8bbwe".to_owned(),
            installed_version: "0.98.1.0".to_owned(),
            publisher: None,
            scope: Some("User".to_owned()),
            installer_category: Some("msix".to_owned()),
            install_location: None,
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
            correlated: None,
        };

        assert!(list_sort_weight(&main) < list_sort_weight(&sparse));
        assert!(list_sort_weight(&sparse) < list_sort_weight(&extension));
    }

    #[test]
    #[cfg(windows)]
    fn arp_subkey_name_extracts_registry_subkey() {
        assert_eq!(Some("ShareX"), arp_subkey_name(r"ARP\Machine\X64\ShareX"));
        assert_eq!(None, arp_subkey_name(r"MSIX\ShareX_19.0.2_x64__name"));
    }

    #[test]
    #[cfg(windows)]
    fn registry_entry_matching_prefers_local_identity_over_correlated_id() {
        let installed = ListMatch {
            name: "ShareX".to_owned(),
            id: "ShareX.ShareX".to_owned(),
            local_id: r"ARP\Machine\X64\ShareX".to_owned(),
            installed_version: "19.0.2".to_owned(),
            available_version: None,
            source_name: Some("winget".to_owned()),
            publisher: Some("ShareX Team".to_owned()),
            scope: Some("Machine".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: Some(r"C:\Program Files\ShareX".to_owned()),
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        };

        assert!(registry_entry_matches_installed_package(
            "ShareX", "ShareX", None, &installed
        ));
        assert!(!registry_entry_matches_installed_package(
            "ShareX.ShareX",
            "ShareX.ShareX",
            None,
            &installed
        ));
    }

    #[test]
    #[cfg(windows)]
    fn build_uninstall_command_only_appends_silent_flag_when_needed() {
        assert_eq!(
            r#""C:\Program Files\ShareX\unins000.exe" /S"#,
            build_uninstall_command(r#""C:\Program Files\ShareX\unins000.exe""#, true, false)
        );
        assert_eq!(
            r#""C:\Program Files\ShareX\unins000.exe" /VERYSILENT"#,
            build_uninstall_command(r#""C:\Program Files\ShareX\unins000.exe" /VERYSILENT"#, true, false)
        );
        assert_eq!(
            r#""C:\Program Files\ShareX\unins000.exe""#,
            build_uninstall_command(r#""C:\Program Files\ShareX\unins000.exe""#, false, false)
        );
        assert_eq!(
            "winget uninstall --product-code JesseDuffield.lazygit_Microsoft.Winget.Source_8wekyb3d8bbwe",
            build_uninstall_command(
                "winget uninstall --product-code JesseDuffield.lazygit_Microsoft.Winget.Source_8wekyb3d8bbwe",
                true,
                false,
            )
        );
    }

    #[test]
    #[cfg(windows)]
    fn build_winget_uninstall_arguments_prefers_correlated_identity() {
        let installed = ListMatch {
            name: "lazygit".to_owned(),
            id: "JesseDuffield.lazygit".to_owned(),
            local_id: r"ARP\User\X64\JesseDuffield.lazygit".to_owned(),
            installed_version: "0.61.1".to_owned(),
            available_version: None,
            source_name: Some("winget".to_owned()),
            publisher: Some("Jesse Duffield".to_owned()),
            scope: Some("User".to_owned()),
            installer_category: Some("exe".to_owned()),
            install_location: Some(
                r"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\JesseDuffield.lazygit".to_owned(),
            ),
            package_family_names: Vec::new(),
            product_codes: vec!["JesseDuffield.lazygit_Microsoft.Winget.Source_8wekyb3d8bbwe".to_owned()],
            upgrade_codes: Vec::new(),
        };
        let mut request = UninstallRequest::new(PackageQuery {
            id: Some("JesseDuffield.lazygit".to_owned()),
            install_scope: Some("user".to_owned()),
            ..PackageQuery::default()
        });
        request.mode = InstallerMode::Silent;

        assert_eq!(
            Some(vec![
                "uninstall".to_owned(),
                "--id".to_owned(),
                "JesseDuffield.lazygit".to_owned(),
                "--exact".to_owned(),
                "--disable-interactivity".to_owned(),
                "--source".to_owned(),
                "winget".to_owned(),
                "--scope".to_owned(),
                "user".to_owned(),
                "--silent".to_owned(),
            ]),
            build_winget_uninstall_arguments(&installed, &request)
        );

        assert_eq!(
            Some(vec![
                "uninstall".to_owned(),
                "--id".to_owned(),
                "JesseDuffield.lazygit".to_owned(),
                "--exact".to_owned(),
                "--disable-interactivity".to_owned(),
                "--source".to_owned(),
                "winget".to_owned(),
                "--silent".to_owned(),
            ]),
            build_winget_uninstall_arguments_with_scope(&installed, &request, false)
        );
    }
}
