//! Builds and publishes signed WinGet v2 static preindexed sources.
//!
//! The publisher reads standard manifest YAML without opening client source
//! state, writes immutable snapshots, and publishes only from those snapshots.
//! It enforces an explicit supported `ManifestVersion` set and structural
//! publishing invariants; it is not a full manifest-schema validator.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process::Command;
use std::time::{SystemTime, UNIX_EPOCH};

use chrono::Utc;
use quick_xml::Reader;
use quick_xml::events::Event;
use rusqlite::{Connection, OptionalExtension, params};
use serde::ser::{SerializeMap, SerializeSeq, Serializer};
use serde::{Deserialize, Serialize};
use serde_yaml::{Mapping, Value};
use sha2::{Digest, Sha256};
use thiserror::Error;
use zip::ZipArchive;

const SUPPORTED_MANIFEST_VERSIONS: &[&str] = &[
    "1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "1.6.0", "1.7.0", "1.8.0", "1.9.0", "1.10.0",
];
const INDEX_SCHEMA: &str = include_str!("index-schema.sql");
const STATE_SCHEMA: &str = include_str!("state-schema.sql");
const MAX_MSZIP_SIZE: usize = 64 * 1024 * 1024;
const PUBLISHER_FORMAT_VERSION: u32 = 2;

/// Errors returned by the repository publisher.
#[derive(Debug, Error)]
pub enum PublisherError {
    /// An underlying filesystem operation failed.
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    /// An underlying SQLite operation failed.
    #[error("SQLite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    /// A manifest could not be parsed.
    #[error("manifest YAML error in {path}: {source}")]
    Yaml {
        /// Manifest path.
        path: PathBuf,
        /// Parser error.
        #[source]
        source: serde_yaml::Error,
    },
    /// A valid parsed YAML value could not be serialized.
    #[error("manifest YAML serialization error: {0}")]
    YamlSerialization(#[from] serde_yaml::Error),
    /// State or ownership data could not be parsed.
    #[error("publisher metadata error: {0}")]
    Json(#[from] serde_json::Error),
    /// Manifest input violated a publishing invariant.
    #[error("{0}")]
    Invalid(String),
    /// A requested platform tool is unavailable or failed.
    #[error("{0}")]
    Tool(String),
}

/// Result type used by publisher operations.
pub type Result<T> = std::result::Result<T, PublisherError>;

/// Input locations for a new immutable repository snapshot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuildOptions {
    /// Directory containing WinGet manifest YAML files.
    pub input: PathBuf,
    /// Publisher work directory holding state and immutable snapshots.
    pub state: PathBuf,
}

/// Summary of a completed build snapshot.
///
/// The serialized form is the shared CLI JSON contract for `repository build`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BuildReport {
    /// Immutable snapshot identifier.
    pub build_id: String,
    /// Absolute snapshot directory under the state root.
    pub snapshot_directory: PathBuf,
    /// Number of merged hosted manifest artifacts.
    #[serde(rename = "manifestCount")]
    pub manifests: usize,
    /// Number of package identifiers.
    #[serde(rename = "packageCount")]
    pub packages: usize,
    /// Input files added or changed relative to the previous build (0 for the first build).
    pub changed_files: usize,
    /// Input files removed relative to the previous build.
    pub removed_files: usize,
    /// Number of immutable snapshot files (feed artifacts plus the index).
    #[serde(skip)]
    pub files: usize,
}

/// Difference between current inputs and the most recent successful build.
///
/// The serialized form is the shared CLI JSON contract for `repository diff`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DiffReport {
    /// Most recent successful build, if any.
    pub last_build_id: Option<String>,
    /// Whether the last successful build has a completed publication record.
    #[serde(rename = "published")]
    pub last_build_published: bool,
    /// Relative paths added since the last build.
    pub added: Vec<String>,
    /// Relative paths whose bytes changed since the last build.
    pub changed: Vec<String>,
    /// Relative paths removed since the last build.
    pub removed: Vec<String>,
}

/// Summary of publisher state without rescanning input manifests.
///
/// The serialized form is the shared CLI JSON contract for `repository status`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct StatusReport {
    /// Most recent successful build, if any.
    pub last_build_id: Option<String>,
    /// Most recently published build, if any.
    pub last_published_build_id: Option<String>,
    /// Number of builds recorded in state.
    pub build_count: usize,
    /// Creation time (UTC) of the most recent build.
    pub last_build_timestamp: Option<String>,
    /// Most recent build details, if one exists.
    #[serde(skip)]
    pub last_build: Option<BuildReport>,
    /// Whether the most recent build has been published.
    #[serde(skip)]
    pub last_build_published: bool,
}

/// Result of a successful snapshot or destination verification.
///
/// The serialized form is the shared CLI JSON contract for `repository verify`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct VerifyReport {
    /// Always true; failures are returned as errors.
    pub valid: bool,
    /// What was verified.
    pub subject: String,
    /// Number of verified artifacts.
    pub artifact_count: usize,
    /// Verified build, when known.
    pub build_id: Option<String>,
}

/// Explicit packaging policy for a local publication.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Signing {
    /// Sign with the supplied PFX and obtain its password from the named environment variable.
    Pfx { path: PathBuf, password_env: String },
    /// Permit publishing an unsigned package for development only.
    UnsignedDevelopment,
}

/// Inputs required to package and publish an immutable snapshot.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublishOptions {
    /// Publisher state directory.
    pub state: PathBuf,
    /// Snapshot to publish; `None` selects the latest successful snapshot.
    pub build_id: Option<String>,
    /// Local static hosting directory.
    pub destination: PathBuf,
    /// Stable AppX package identity name.
    pub name: String,
    /// Publisher distinguished name used by the package and signing certificate.
    pub publisher: String,
    /// Four-component AppX package version.
    pub version: String,
    /// Signing policy.
    pub signing: Signing,
    /// Optional path to `makeappx.exe`; PATH and installed Windows SDKs are searched when omitted.
    pub makeappx: Option<PathBuf>,
    /// Optional path to `signtool.exe`; PATH and installed Windows SDKs are searched when omitted.
    pub signtool: Option<PathBuf>,
    /// Optional RFC 3161 timestamp URL used for signed publication.
    pub timestamp_url: Option<String>,
}

/// Local publication result.
///
/// The serialized form is the shared CLI JSON contract for `repository publish`:
/// the verification result of the published destination.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PublishReport {
    /// True only after the destination passed full verification.
    pub valid: bool,
    /// What was verified.
    pub subject: String,
    /// Number of published artifacts listed in the ownership marker.
    pub artifact_count: usize,
    /// Snapshot that was published.
    pub build_id: String,
    /// Destination directory.
    #[serde(skip)]
    pub destination: PathBuf,
    /// Whether the result is unsigned and development-only.
    #[serde(skip)]
    pub unsigned_development: bool,
    /// Whether this was an idempotent retry of an already-published build.
    #[serde(skip)]
    pub reused: bool,
}

/// Tool and platform capabilities discovered by `doctor`.
///
/// The serialized form is the shared CLI JSON contract for `repository doctor`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DoctorReport {
    /// Informational host operating-system description.
    pub operating_system: String,
    /// Resolved `makeappx.exe`, if found.
    #[serde(rename = "makeAppxPath")]
    pub makeappx_path: Option<PathBuf>,
    /// Resolved `signtool.exe`, if found.
    #[serde(rename = "signToolPath")]
    pub signtool_path: Option<PathBuf>,
    /// Host can generate the native WinGet MSZIP stream.
    #[serde(rename = "nativeMsZipAvailable")]
    pub native_mszip_available: bool,
    /// Whether this host supports signed MSIX publication.
    #[serde(skip)]
    pub signed_publish: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Inventory {
    files: BTreeMap<String, String>,
    manifests: Vec<ManifestInventory>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SnapshotMetadata {
    schema_version: u32,
    #[serde(default)]
    artifact_format_version: u32,
    build_id: String,
    created_utc: String,
    manifest_count: usize,
    package_count: usize,
    index_hash: String,
    #[serde(serialize_with = "serialize_inventory")]
    inventory: BTreeMap<String, String>,
    #[serde(serialize_with = "serialize_inventory")]
    artifacts: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct ManifestInventory {
    id: String,
    name: String,
    version: String,
    moniker: Option<String>,
    latest_version: String,
    manifest_path: String,
    manifest_hash: String,
    sidecar_path: String,
    sidecar_hash: String,
    tags: Vec<String>,
    commands: Vec<String>,
    pfns: Vec<String>,
    product_codes: Vec<String>,
    upgrade_codes: Vec<String>,
    normalized_names: Vec<String>,
    normalized_publishers: Vec<String>,
    arp_min_version: Option<String>,
    arp_max_version: Option<String>,
}

#[derive(Debug, Clone)]
struct ManifestDocument {
    path: PathBuf,
    kind: String,
    id: String,
    version: String,
    locale: Option<String>,
    document: Value,
}

type ManifestGroups = BTreeMap<(String, String), Vec<ManifestDocument>>;

#[derive(Debug, Clone)]
struct PackageManifest {
    id: String,
    name: String,
    version: String,
    moniker: Option<String>,
    locale_docs: Vec<LocaleDocument>,
}

#[derive(Debug, Clone)]
struct LocaleDocument {
    locale: String,
    document: Value,
}

struct CanonicalYaml<'a>(&'a Value);

impl Serialize for CanonicalYaml<'_> {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        match self.0 {
            Value::Null => serializer.serialize_unit(),
            Value::Bool(value) => serializer.serialize_bool(*value),
            Value::Number(value) => value.serialize(serializer),
            Value::String(value) => serializer.serialize_str(value),
            Value::Sequence(values) => {
                let mut sequence = serializer.serialize_seq(Some(values.len()))?;
                for value in values {
                    sequence.serialize_element(&CanonicalYaml(value))?;
                }
                sequence.end()
            }
            Value::Mapping(values) => {
                let mut entries: Vec<_> = values.iter().collect();
                entries.sort_by(|(left, _), (right, _)| {
                    compare_utf16(left.as_str().unwrap_or_default(), right.as_str().unwrap_or_default())
                });
                let mut map = serializer.serialize_map(Some(entries.len()))?;
                for (key, value) in entries {
                    let key = key
                        .as_str()
                        .ok_or_else(|| serde::ser::Error::custom("manifest JSON object keys must be strings"))?;
                    map.serialize_entry(key, &CanonicalYaml(value))?;
                }
                map.end()
            }
            Value::Tagged(_) => Err(serde::ser::Error::custom(
                "YAML tags are not supported in WinGet manifests",
            )),
        }
    }
}

struct CanonicalInventory<'a>(&'a BTreeMap<String, String>);

impl Serialize for CanonicalInventory<'_> {
    fn serialize<S>(&self, serializer: S) -> std::result::Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        let mut entries: Vec<_> = self.0.iter().collect();
        entries.sort_by(|(left, _), (right, _)| compare_utf16(left, right));
        let mut map = serializer.serialize_map(Some(entries.len()))?;
        for (path, hash) in entries {
            map.serialize_entry(path, hash)?;
        }
        map.end()
    }
}

struct WriterLock {
    _file: File,
}

/// Builds a complete, self-contained v2 index snapshot from manifest files.
///
/// The input and state roots must not overlap. On Windows, version data is
/// compressed using the native MSZIP implementation used by WinGet.
///
/// # Errors
///
/// Returns an error for unsafe paths, unsupported manifest schema versions,
/// malformed or incomplete manifest sets, filesystem failures, or SQLite failures.
pub fn build(options: &BuildOptions) -> Result<BuildReport> {
    reject_reparse_components(&options.input)?;
    let input = fs::canonicalize(&options.input)?;
    if !input.is_dir() {
        return Err(PublisherError::Invalid(format!(
            "manifest input is not a directory: {}",
            input.display()
        )));
    }
    let state_abs = canonicalize_future_path(&options.state)?;
    ensure_disjoint_roots(&input, &state_abs)?;
    fs::create_dir_all(&state_abs)?;
    let state = fs::canonicalize(&state_abs)?;
    ensure_disjoint_roots(&input, &state)?;
    if state.join("publisher.db").exists() {
        drop(open_state(&state, false)?);
    }
    let _lock = acquire_lock(&state)?;

    let (inventory, groups) = scan_manifests(&input)?;
    if groups.is_empty() {
        return Err(PublisherError::Invalid(format!(
            "no supported manifest YAML files found under {}",
            input.display()
        )));
    }
    let mut previous: Option<BTreeMap<String, String>> = None;
    if state.join("publisher.db").is_file() {
        let connection = open_state(&state, false)?;
        let latest: Option<String> = connection
            .query_row("SELECT value FROM metadata WHERE name='lastBuildId'", [], |row| {
                row.get(0)
            })
            .optional()?;
        if let Some(build_id) = latest {
            let old_inventory: String =
                connection.query_row("SELECT inventory FROM builds WHERE id=?1", [&build_id], |row| {
                    row.get(0)
                })?;
            let old: BTreeMap<String, String> = serde_json::from_str(&old_inventory)?;
            if old == inventory.files {
                let snapshot = state.join("builds").join(&build_id);
                verify_snapshot_path(&snapshot)?;
                let metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
                validate_recorded_snapshot(&connection, &metadata)?;
                return report_from_snapshot(&snapshot, &build_id, (0, 0));
            }
            previous = Some(old);
        }
    }
    let changes = previous.as_ref().map_or((0, 0), |old| {
        let changed = inventory
            .files
            .iter()
            .filter(|(path, hash)| old.get(*path) != Some(*hash))
            .count();
        let removed = old.keys().filter(|path| !inventory.files.contains_key(*path)).count();
        (changed, removed)
    });
    let build_id = build_identifier(&inventory)?;
    let snapshots = state.join("builds");
    reject_reparse_components(&snapshots)?;
    fs::create_dir_all(&snapshots)?;
    let snapshot = snapshots.join(&build_id);
    if snapshot.exists() {
        verify_snapshot_path(&snapshot)?;
        let snapshot_data: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
        record_build(&state, &build_id, &snapshot_data)?;
        return report_from_snapshot(&snapshot, &build_id, changes);
    }
    let staging = snapshots.join(format!(".staging-{build_id}"));
    if staging.exists() {
        remove_owned_staging(&staging)?;
    }
    fs::create_dir(&staging)?;
    let result = (|| {
        build_snapshot(&staging, groups, &build_id)?;
        let snapshot_data = snapshot_metadata(&staging, &inventory, &build_id)?;
        write_json(&staging.join("snapshot.json"), &snapshot_data)?;
        Ok::<_, PublisherError>(snapshot_data)
    })();
    let snapshot_data = match result {
        Ok(snapshot_data) => snapshot_data,
        Err(error) => {
            let _ = fs::remove_dir_all(&staging);
            return Err(error);
        }
    };
    if let Err(error) = fs::rename(&staging, &snapshot) {
        let _ = fs::remove_dir_all(&staging);
        return Err(error.into());
    }
    record_build(&state, &build_id, &snapshot_data)?;
    report_from_snapshot(&snapshot, &build_id, changes)
}

/// Computes input changes relative to the most recent successful build.
///
/// # Errors
///
/// Returns an error if state is absent or unreadable, or if manifest inputs are invalid.
pub fn diff(options: &BuildOptions) -> Result<DiffReport> {
    reject_reparse_components(&options.input)?;
    reject_reparse_components(&options.state)?;
    let input = fs::canonicalize(&options.input)?;
    let state = fs::canonicalize(&options.state)?;
    ensure_disjoint_roots(&input, &state)?;
    let connection = open_state(&state, false)?;
    let previous: Option<String> = connection
        .query_row("SELECT value FROM metadata WHERE name='lastBuildId'", [], |row| {
            row.get(0)
        })
        .optional()?;
    let (current, _) = scan_manifests(&input)?;
    let Some(build_id) = previous else {
        return Ok(DiffReport {
            added: current.files.keys().cloned().collect(),
            ..DiffReport::default()
        });
    };
    let previous_inventory: String =
        connection.query_row("SELECT inventory FROM builds WHERE id=?1", [&build_id], |row| {
            row.get(0)
        })?;
    let previous_files: BTreeMap<String, String> = serde_json::from_str(&previous_inventory)?;
    let mut added = Vec::new();
    let mut changed = Vec::new();
    let mut removed = Vec::new();
    for (path, hash) in &current.files {
        match previous_files.get(path) {
            None => added.push(path.clone()),
            Some(previous_hash) if previous_hash != hash => changed.push(path.clone()),
            _ => {}
        }
    }
    for path in previous_files.keys() {
        if !current.files.contains_key(path) {
            removed.push(path.clone());
        }
    }
    let published = is_published(&connection, &build_id)?;
    Ok(DiffReport {
        last_build_id: Some(build_id),
        last_build_published: published,
        added,
        changed,
        removed,
    })
}

/// Reads publisher state without creating or changing files.
///
/// # Errors
///
/// Returns an error if the state directory is absent, incompatible, or unreadable.
pub fn status(state: impl AsRef<Path>) -> Result<StatusReport> {
    reject_reparse_components(state.as_ref())?;
    let state = fs::canonicalize(state)?;
    let connection = open_state(&state, false)?;
    let latest: Option<String> = connection
        .query_row("SELECT value FROM metadata WHERE name='lastBuildId'", [], |row| {
            row.get(0)
        })
        .optional()?;
    let last_published_build_id: Option<String> = connection
        .query_row(
            "SELECT value FROM metadata WHERE name='lastPublishedBuildId'",
            [],
            |row| row.get(0),
        )
        .optional()?;
    let build_count: i64 = connection.query_row("SELECT COUNT(*) FROM builds", [], |row| row.get(0))?;
    let build_count = usize::try_from(build_count)
        .map_err(|_| PublisherError::Invalid("publisher build count is invalid".to_owned()))?;
    let Some(build_id) = latest else {
        return Ok(StatusReport {
            last_published_build_id,
            build_count,
            ..StatusReport::default()
        });
    };
    let (inventory, summary): (String, String) = connection.query_row(
        "SELECT inventory, summary FROM builds WHERE id=?1",
        [&build_id],
        |row| Ok((row.get(0)?, row.get(1)?)),
    )?;
    let summary: serde_json::Value = serde_json::from_str(&summary)?;
    let report = BuildReport {
        build_id: build_id.clone(),
        snapshot_directory: display_path(&state.join("builds").join(&build_id)),
        packages: json_count(&summary, "packageCount")?,
        manifests: json_count(&summary, "manifestCount")?,
        changed_files: 0,
        removed_files: 0,
        files: serde_json::from_str::<BTreeMap<String, String>>(&inventory)?.len(),
    };
    Ok(StatusReport {
        last_build_published: is_published(&connection, &build_id)?,
        last_build_timestamp: summary["createdUtc"].as_str().map(str::to_owned),
        last_build_id: Some(build_id),
        last_published_build_id,
        build_count,
        last_build: Some(report),
    })
}

/// Checks a snapshot or a previously published static directory.
///
/// # Errors
///
/// Returns an error if any inventory file is missing or differs from its stored hash.
pub fn verify(state: impl AsRef<Path>, build_id: Option<&str>, destination: Option<&Path>) -> Result<VerifyReport> {
    if let Some(destination) = destination {
        if build_id.is_some() {
            return Err(PublisherError::Invalid(
                "--build cannot be used with --destination".to_owned(),
            ));
        }
        return verify_destination(destination);
    }
    reject_reparse_components(state.as_ref())?;
    let state = fs::canonicalize(state)?;
    let connection = open_state(&state, false)?;
    let selected = match build_id {
        Some(value) => value.to_owned(),
        None => connection.query_row("SELECT value FROM metadata WHERE name='lastBuildId'", [], |row| {
            row.get::<_, String>(0)
        })?,
    };
    validate_build_id(&selected)?;
    let snapshot = state.join("builds").join(&selected);
    verify_snapshot_path(&snapshot)?;
    let metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
    validate_recorded_snapshot(&connection, &metadata)?;
    Ok(VerifyReport {
        valid: true,
        subject: "unsigned build snapshot".to_owned(),
        artifact_count: metadata.artifacts.len() + 1,
        build_id: Some(selected),
    })
}

/// Reports packaging tools available on the current host.
#[must_use]
pub fn doctor() -> DoctorReport {
    doctor_with_tools(None, None)
}

/// Reports publisher capabilities using optional explicit tool paths.
#[must_use]
pub fn doctor_with_tools(makeappx_path: Option<&Path>, signtool_path: Option<&Path>) -> DoctorReport {
    let makeappx = find_tool("makeappx.exe", makeappx_path);
    let signtool = find_tool("signtool.exe", signtool_path);
    let powershell = find_tool("powershell.exe", None).is_some();
    DoctorReport {
        operating_system: format!("{} {}", std::env::consts::OS, std::env::consts::ARCH),
        signed_publish: cfg!(windows) && makeappx.is_some() && signtool.is_some() && powershell,
        makeappx_path: makeappx.map(|path| display_path(&path)),
        signtool_path: signtool.map(|path| display_path(&path)),
        native_mszip_available: cfg!(windows),
    }
}

/// Verifies a published static source and, unless unsigned-development, its MSIX signature.
///
/// # Errors
///
/// Returns an error when the ownership inventory, MSIX contents, or signature is invalid.
pub fn verify_published_destination(destination: &Path, signtool_path: Option<&Path>) -> Result<VerifyReport> {
    verify_destination_with_tool(destination, signtool_path)
}

/// Packages, signs when requested, verifies and publishes one immutable snapshot.
///
/// # Errors
///
/// Returns an error for unsupported hosts, invalid package identity/version,
/// missing tools, failed signatures, unsafe destinations, or conflicting immutable files.
pub fn publish(options: &PublishOptions) -> Result<PublishReport> {
    publish_after_copy(options, |_| Ok(()))
}

fn publish_after_copy(options: &PublishOptions, after_copy: impl FnOnce(&Path) -> Result<()>) -> Result<PublishReport> {
    if !cfg!(windows) {
        return Err(PublisherError::Tool(
            "MSIX packaging and signing are currently supported only on Windows".to_owned(),
        ));
    }
    reject_reparse_components(&options.state)?;
    reject_reparse_components(&options.destination)?;
    validate_package_identity(&options.name, &options.publisher)?;
    validate_msix_version(&options.version)?;
    if let Some(url) = &options.timestamp_url
        && (!matches!(options.signing, Signing::Pfx { .. })
            || !(url.starts_with("https://") || url.starts_with("http://"))
            || url.chars().any(char::is_whitespace))
    {
        return Err(PublisherError::Invalid(
            "--timestamp-url requires signed publication and an HTTP(S) URL".to_owned(),
        ));
    }
    let state = fs::canonicalize(&options.state)?;
    let destination_abs = canonicalize_future_path(&options.destination)?;
    ensure_disjoint_roots(&state, &destination_abs)?;
    let connection = open_state(&state, false)?;
    let _lock = acquire_lock(&state)?;
    let build_id = match &options.build_id {
        Some(value) => value.clone(),
        None => connection.query_row("SELECT value FROM metadata WHERE name='lastBuildId'", [], |row| {
            row.get::<_, String>(0)
        })?,
    };
    validate_build_id(&build_id)?;
    let snapshot = state.join("builds").join(&build_id);
    verify_snapshot_path(&snapshot)?;
    let snapshot_metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
    validate_recorded_snapshot(&connection, &snapshot_metadata)?;

    read_owner_marker(&destination_abs.join(".pinget-publisher.json"))?;
    let _destination_lock = acquire_destination_lock(&destination_abs)?;
    fs::create_dir_all(&destination_abs)?;
    let destination = fs::canonicalize(&destination_abs)?;
    ensure_disjoint_roots(&state, &destination)?;
    ensure_owned_destination(&destination)?;
    let marker_path = destination.join(".pinget-publisher.json");
    let prior = read_owner_marker(&marker_path)?;
    let unsigned_development = matches!(options.signing, Signing::UnsignedDevelopment);
    if let Some(owner) = prior.as_ref() {
        let verified = verify_destination_with_tool(&destination, options.signtool.as_deref())?;
        if owner.package_name != options.name || owner.publisher != options.publisher {
            return Err(PublisherError::Invalid(
                "destination is owned by a different package identity".to_owned(),
            ));
        }
        if owner.build_id == build_id && owner.package_version == options.version {
            if owner.unsigned_development != unsigned_development {
                return Err(PublisherError::Invalid(
                    "the package version has already been published with a different signing mode".to_owned(),
                ));
            }
            mark_published(&state, &build_id)?;
            return Ok(PublishReport {
                valid: true,
                subject: verified.subject,
                artifact_count: verified.artifact_count,
                build_id,
                destination: display_path(&destination),
                unsigned_development: owner.unsigned_development,
                reused: true,
            });
        }
        match compare_quad_version(&options.version, &owner.package_version)? {
            std::cmp::Ordering::Less => {
                return Err(PublisherError::Invalid(format!(
                    "package version {} must be greater than previously published version {}",
                    options.version, owner.package_version
                )));
            }
            std::cmp::Ordering::Equal => {
                return Err(PublisherError::Invalid(
                    "the package version has already been used for different build content".to_owned(),
                ));
            }
            std::cmp::Ordering::Greater => {}
        }
    }
    let current_inventory = published_inventory(&destination)?;
    if let Some(owner) = prior.as_ref() {
        for (path, expected) in &owner.artifacts {
            if current_inventory.get(path) != Some(expected) {
                return Err(PublisherError::Invalid(format!(
                    "owned publication file is missing or modified: {path}"
                )));
            }
        }
        for (path, actual) in &current_inventory {
            if owner.artifacts.get(path) != Some(actual) && snapshot_metadata.artifacts.get(path) != Some(actual) {
                return Err(PublisherError::Invalid(format!(
                    "destination contains an untracked or modified file: {path}"
                )));
            }
        }
    }
    let temporary = destination.join(format!(".publish-{build_id}.staging"));
    if temporary.exists() {
        remove_owned_staging(&temporary)?;
    }
    fs::create_dir(&temporary)?;
    let result = package_and_publish(
        &snapshot,
        &temporary,
        &destination,
        options,
        &build_id,
        prior,
        after_copy,
    );
    if result.is_err() {
        let _ = fs::remove_dir_all(&temporary);
    }
    result
}

fn build_snapshot(
    staging: &Path,
    groups: BTreeMap<(String, String), Vec<ManifestDocument>>,
    build_id: &str,
) -> Result<()> {
    let feed = staging.join("feed");
    fs::create_dir_all(&feed)?;
    fs::create_dir_all(staging.join("Public"))?;
    let mut db = Connection::open(staging.join("Public").join("index.db"))?;
    db.execute_batch(INDEX_SCHEMA)?;
    db.execute_batch("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;")?;
    db.execute(
        "INSERT INTO metadata(name,value) VALUES('majorVersion','2'),('minorVersion','0'),('databaseIdentifier',?1),('lastwritetime',?2)",
        params![format!("{{{}}}", uuid_string(build_id)), Utc::now().timestamp().to_string()],
    )?;
    let transaction = db.transaction()?;
    let mut by_package: BTreeMap<String, Vec<PackageManifest>> = BTreeMap::new();
    let mut package_spellings = BTreeMap::<String, String>::new();
    for ((id, _), documents) in groups {
        let package = merge_documents(&id, documents)?;
        let key = package.id.to_ascii_lowercase();
        if let Some(previous) = package_spellings.get(&key)
            && previous != &package.id
        {
            return Err(PublisherError::Invalid(format!(
                "PackageIdentifier casing is inconsistent across manifests: {previous} and {}",
                package.id
            )));
        }
        package_spellings
            .entry(key.clone())
            .or_insert_with(|| package.id.clone());
        by_package.entry(key).or_default().push(package);
    }
    for (row_index, (_, mut versions)) in by_package.into_iter().enumerate() {
        versions.sort_by(|left, right| compare_winget_versions(&left.version, &right.version));
        if versions
            .windows(2)
            .any(|pair| compare_winget_versions(&pair[0].version, &pair[1].version) == std::cmp::Ordering::Equal)
        {
            return Err(PublisherError::Invalid(format!(
                "duplicate package version {} for {}",
                versions[0].version, versions[0].id
            )));
        }
        let latest = versions
            .last()
            .ok_or_else(|| PublisherError::Invalid("empty package version group".to_owned()))?
            .clone();
        let mut version_records = Vec::new();
        for package in &versions {
            let mut locale_docs = package.locale_docs.clone();
            locale_docs.sort_by_key(|left| left.locale.to_ascii_lowercase());
            for locale in locale_docs {
                let locale_package = PackageManifest {
                    id: package.id.clone(),
                    name: package.name.clone(),
                    version: package.version.clone(),
                    moniker: package.moniker.clone(),
                    locale_docs: vec![locale.clone()],
                };
                let locale_fields = catalog_fields_all(std::slice::from_ref(&locale_package))?;
                let manifest_bytes = serialize_merged(&locale.document)?;
                let manifest_hash = sha256_hex(&manifest_bytes);
                let manifest_relative = format!(
                    "manifests/{}/{}/{}/{}.yaml",
                    safe_component(&package.id)?,
                    safe_component(&package.version)?,
                    safe_component(&locale.locale)?,
                    &manifest_hash[..16]
                );
                write_relative(&feed, &manifest_relative, &manifest_bytes)?;
                version_records.push((
                    package.version.clone(),
                    locale.locale,
                    manifest_relative,
                    manifest_hash,
                    locale_fields.7,
                    locale_fields.8,
                ));
            }
        }
        version_records
            .sort_by(|left, right| compare_winget_versions(&right.0, &left.0).then_with(|| left.1.cmp(&right.1)));
        let mut version_yaml = String::from("sV: \"1.0\"\nvD:\n");
        for (version, _, path, hash, min_version, max_version) in &version_records {
            version_yaml.push_str(&format!("  - v: {}\n", json_quote(version)?,));
            if let Some(min_version) = min_version {
                version_yaml.push_str(&format!("    aMiV: {}\n", json_quote(min_version)?));
            }
            if let Some(max_version) = max_version {
                version_yaml.push_str(&format!("    aMaV: {}\n", json_quote(max_version)?));
            }
            version_yaml.push_str(&format!(
                "    rP: {}\n    s256H: {}\n",
                json_quote(path)?,
                json_quote(hash)?
            ));
        }
        let sidecar_bytes = compress_mszip(version_yaml.as_bytes())?;
        let sidecar_hash = sha256_hex(&sidecar_bytes);
        let sidecar_relative = format!(
            "packages/{}/{}/versionData.mszyml",
            safe_component(&latest.id)?,
            &sidecar_hash[..8]
        );
        write_relative(&feed, &sidecar_relative, &sidecar_bytes)?;
        let fields = catalog_fields_all(&versions)?;
        let row_id =
            i64::try_from(row_index + 1).map_err(|_| PublisherError::Invalid("too many package rows".to_owned()))?;
        transaction.execute(
            "INSERT INTO packages(rowid,id,name,moniker,latest_version,arp_min_version,arp_max_version,hash) VALUES(?1,?2,?3,?4,?5,?6,?7,?8)",
            params![
                row_id,
                latest.id,
                latest.name,
                latest.moniker,
                latest.version,
                fields.7,
                fields.8,
                Sha256::digest(&sidecar_bytes).as_slice()
            ],
        )?;
        insert_terms(&transaction, "tags2", "tag", "tags2_map", "tag", row_id, &fields.0)?;
        insert_terms(
            &transaction,
            "commands2",
            "command",
            "commands2_map",
            "command",
            row_id,
            &fields.1,
        )?;
        insert_correlations(&transaction, "pfns2", "pfn", row_id, &fields.2)?;
        insert_correlations(&transaction, "productcodes2", "productcode", row_id, &fields.3)?;
        insert_correlations(&transaction, "upgradecodes2", "upgradecode", row_id, &fields.4)?;
        insert_correlations(&transaction, "norm_names2", "norm_name", row_id, &fields.5)?;
        insert_correlations(&transaction, "norm_publishers2", "norm_publisher", row_id, &fields.6)?;
    }
    transaction.commit()?;
    drop(db);
    let _ = build_id;
    Ok(())
}

fn scan_manifests(root: &Path) -> Result<(Inventory, ManifestGroups)> {
    let mut files = BTreeMap::new();
    let mut case_paths = BTreeSet::new();
    let mut documents = Vec::new();
    scan_directory(root, root, &mut files, &mut case_paths, &mut documents)?;
    let mut grouped: BTreeMap<(String, String), Vec<ManifestDocument>> = BTreeMap::new();
    let mut identifiers: BTreeMap<String, String> = BTreeMap::new();
    for document in documents {
        let spelling = identifiers
            .entry(document.id.to_ascii_lowercase())
            .or_insert_with(|| document.id.clone());
        if spelling != &document.id {
            return Err(PublisherError::Invalid(format!(
                "case-conflicting package identifiers: {spelling} and {}",
                document.id
            )));
        }
        grouped
            .entry((document.id.clone(), document.version.clone()))
            .or_default()
            .push(document);
    }
    let mut manifests = Vec::with_capacity(grouped.len());
    for ((_, _), docs) in &grouped {
        let first = docs
            .first()
            .ok_or_else(|| PublisherError::Invalid("empty manifest group".to_owned()))?;
        let package = merge_documents(&first.id, docs.clone())?;
        let fields = catalog_fields_all(std::slice::from_ref(&package))?;
        manifests.push(ManifestInventory {
            id: package.id.clone(),
            name: package.name.clone(),
            version: package.version.clone(),
            latest_version: package.version.clone(),
            moniker: package.moniker.clone(),
            manifest_path: String::new(),
            manifest_hash: String::new(),
            sidecar_path: String::new(),
            sidecar_hash: String::new(),
            tags: fields.0,
            commands: fields.1,
            pfns: fields.2,
            product_codes: fields.3,
            upgrade_codes: fields.4,
            normalized_names: fields.5,
            normalized_publishers: fields.6,
            arp_min_version: fields.7,
            arp_max_version: fields.8,
        });
    }
    let inventory = Inventory { files, manifests };
    Ok((inventory, grouped))
}

fn scan_directory(
    root: &Path,
    directory: &Path,
    files: &mut BTreeMap<String, String>,
    case_paths: &mut BTreeSet<String>,
    documents: &mut Vec<ManifestDocument>,
) -> Result<()> {
    let mut entries = fs::read_dir(directory)?.collect::<std::io::Result<Vec<_>>>()?;
    entries.sort_by_key(|entry| entry.file_name());
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        if is_reparse_point(&metadata) {
            return Err(PublisherError::Invalid(format!(
                "symbolic links and reparse points are not allowed in manifest input: {}",
                path.display()
            )));
        }
        if metadata.is_dir() {
            scan_directory(root, &path, files, case_paths, documents)?;
            continue;
        }
        let extension = path.extension().and_then(|v| v.to_str()).unwrap_or_default();
        if !extension.eq_ignore_ascii_case("yaml") && !extension.eq_ignore_ascii_case("yml") {
            continue;
        }
        let relative = path
            .strip_prefix(root)
            .map_err(|_| PublisherError::Invalid(format!("manifest escaped input root: {}", path.display())))?;
        validate_relative_path(relative)?;
        let path_key = relative
            .to_str()
            .ok_or_else(|| PublisherError::Invalid(format!("manifest path is not valid Unicode: {}", path.display())))?
            .replace('\\', "/");
        if !case_paths.insert(path_key.to_lowercase()) {
            return Err(PublisherError::Invalid(format!(
                "case-colliding manifest path: {path_key}"
            )));
        }
        let bytes = fs::read(&path)?;
        if bytes.len() > 8 * 1024 * 1024 {
            return Err(PublisherError::Invalid(format!(
                "manifest exceeds the 8 MiB limit: {path_key}"
            )));
        }
        files.insert(path_key, sha256_hex(&bytes));
        let deserializer = serde_yaml::Deserializer::from_slice(&bytes);
        let mut any_document = false;
        for document in deserializer {
            if any_document {
                return Err(PublisherError::Invalid(format!(
                    "expected one YAML document in {}",
                    path.display()
                )));
            }
            let value = serde::de::DeserializeSeed::deserialize(ManifestValue, document).map_err(|source| {
                PublisherError::Yaml {
                    path: path.clone(),
                    source,
                }
            })?;
            validate_yaml_value(&value, 0)?;
            if value.is_null() {
                continue;
            }
            any_document = true;
            let mapping = value.as_mapping().ok_or_else(|| {
                PublisherError::Invalid(format!("manifest document in {} is not a YAML mapping", path.display()))
            })?;
            let manifest_type = required_string(mapping, "ManifestType", &path)?;
            let manifest_version = required_string(mapping, "ManifestVersion", &path)?;
            if !SUPPORTED_MANIFEST_VERSIONS.contains(&manifest_version.as_str()) {
                return Err(PublisherError::Invalid(format!(
                    "unsupported ManifestVersion {manifest_version} in {}; supported versions: {}",
                    path.display(),
                    SUPPORTED_MANIFEST_VERSIONS.join(", ")
                )));
            }
            let kind = manifest_type.clone();
            if !["singleton", "version", "installer", "defaultLocale", "locale"].contains(&kind.as_str()) {
                return Err(PublisherError::Invalid(format!(
                    "unsupported ManifestType {manifest_type} in {}",
                    path.display()
                )));
            }
            let id = required_string(mapping, "PackageIdentifier", &path)?;
            validate_package_identifier(&id)?;
            let version = required_string(mapping, "PackageVersion", &path)?;
            validate_package_version(&version)?;
            let locale = optional_string(mapping, "PackageLocale");
            if matches!(kind.as_str(), "singleton" | "defaultLocale" | "locale") {
                validate_locale(
                    locale.as_deref().ok_or_else(|| {
                        PublisherError::Invalid(format!("missing PackageLocale in {}", path.display()))
                    })?,
                )?;
            }
            documents.push(ManifestDocument {
                path: path.clone(),
                kind: kind.to_ascii_lowercase(),
                id,
                version,
                locale,
                document: value,
            });
        }
        if !any_document {
            return Err(PublisherError::Invalid(format!(
                "manifest file is empty: {}",
                path.display()
            )));
        }
    }
    Ok(())
}

struct ManifestValue;

struct ManifestString;

impl<'de> serde::de::DeserializeSeed<'de> for ManifestString {
    type Value = Value;

    fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> std::result::Result<Value, D::Error> {
        deserializer.deserialize_string(self)
    }
}

impl<'de> serde::de::Visitor<'de> for ManifestString {
    type Value = Value;

    fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("a string-valued manifest field")
    }

    fn visit_str<E: serde::de::Error>(self, value: &str) -> std::result::Result<Value, E> {
        Ok(Value::String(value.to_owned()))
    }

    fn visit_map<M: serde::de::MapAccess<'de>>(self, access: M) -> std::result::Result<Value, M::Error> {
        serde::de::Visitor::visit_map(ManifestValue, access)
    }

    fn visit_seq<S: serde::de::SeqAccess<'de>>(self, mut access: S) -> std::result::Result<Value, S::Error> {
        let mut items = Vec::new();
        while let Some(value) = access.next_element_seed(ManifestString)? {
            items.push(value);
        }
        Ok(Value::Sequence(items))
    }
}

impl<'de> serde::de::DeserializeSeed<'de> for ManifestValue {
    type Value = Value;

    fn deserialize<D: serde::Deserializer<'de>>(self, deserializer: D) -> std::result::Result<Value, D::Error> {
        deserializer.deserialize_any(self)
    }
}

impl<'de> serde::de::Visitor<'de> for ManifestValue {
    type Value = Value;

    fn expecting(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("a manifest YAML value")
    }

    fn visit_map<M: serde::de::MapAccess<'de>>(self, mut access: M) -> std::result::Result<Value, M::Error> {
        let mut map = Mapping::new();
        while let Some(key) = access.next_key::<Value>()? {
            let key = key
                .as_str()
                .ok_or_else(|| serde::de::Error::custom("manifest mapping keys must be strings"))?
                .to_owned();
            let value = if matches!(
                key.as_str(),
                "Commands"
                    | "Tags"
                    | "FileExtensions"
                    | "Protocols"
                    | "Capabilities"
                    | "RestrictedCapabilities"
                    | "InstallModes"
                    | "Platform"
                    | "WindowsFeatures"
                    | "WindowsLibraries"
                    | "ExternalDependencies"
                    | "AllowedMarkets"
                    | "ExcludedMarkets"
                    | "UnsupportedOSArchitectures"
                    | "UnsupportedArguments"
            ) {
                Value::Sequence(
                    access
                        .next_value::<Vec<String>>()?
                        .into_iter()
                        .map(Value::String)
                        .collect(),
                )
            } else if is_manifest_string_field(&key) {
                // deserialize_string asks YAML for its original scalar text, not its inferred number.
                access.next_value_seed(ManifestString)?
            } else {
                access.next_value_seed(ManifestValue)?
            };
            if map.insert(Value::String(key.clone()), value).is_some() {
                return Err(serde::de::Error::custom(format!("duplicate manifest key {key}")));
            }
        }
        Ok(Value::Mapping(map))
    }

    fn visit_seq<S: serde::de::SeqAccess<'de>>(self, mut access: S) -> std::result::Result<Value, S::Error> {
        let mut items = Vec::new();
        while let Some(value) = access.next_element_seed(ManifestValue)? {
            items.push(value);
        }
        Ok(Value::Sequence(items))
    }

    fn visit_str<E: serde::de::Error>(self, value: &str) -> std::result::Result<Value, E> {
        Ok(Value::String(value.to_owned()))
    }

    fn visit_bool<E: serde::de::Error>(self, value: bool) -> std::result::Result<Value, E> {
        Ok(Value::Bool(value))
    }

    fn visit_i64<E: serde::de::Error>(self, value: i64) -> std::result::Result<Value, E> {
        Ok(Value::Number(value.into()))
    }

    fn visit_u64<E: serde::de::Error>(self, value: u64) -> std::result::Result<Value, E> {
        Ok(Value::Number(value.into()))
    }

    fn visit_f64<E: serde::de::Error>(self, value: f64) -> std::result::Result<Value, E> {
        if !value.is_finite() {
            return Err(E::custom(
                "non-finite numbers cannot be represented in merged manifest JSON",
            ));
        }
        Ok(Value::Number(value.into()))
    }

    fn visit_unit<E: serde::de::Error>(self) -> std::result::Result<Value, E> {
        Ok(Value::Null)
    }
}

fn is_manifest_string_field(key: &str) -> bool {
    key.ends_with("Sha256")
        || key.ends_with("Url")
        || matches!(
            key,
            "PackageIdentifier"
                | "PackageVersion"
                | "ManifestVersion"
                | "ManifestType"
                | "DefaultLocale"
                | "PackageLocale"
                | "PackageName"
                | "Publisher"
                | "Moniker"
                | "DisplayName"
                | "DisplayVersion"
                | "MinimumVersion"
                | "MinimumOSVersion"
                | "PackageFamilyName"
                | "ProductCode"
                | "UpgradeCode"
                | "InstallerUrl"
                | "InstallerLocale"
                | "InstallerType"
                | "NestedInstallerType"
                | "Architecture"
                | "Scope"
                | "License"
                | "ShortDescription"
                | "Description"
                | "Copyright"
                | "PublisherUrl"
                | "PublisherSupportUrl"
                | "PrivacyUrl"
                | "Author"
                | "PackageUrl"
                | "LicenseUrl"
                | "CopyrightUrl"
                | "ReleaseNotes"
                | "ReleaseNotesUrl"
                | "PurchaseUrl"
                | "InstallationNotes"
                | "InstallLocation"
                | "RelativeFilePath"
                | "PortableCommandAlias"
                | "IconUrl"
                | "IconFileType"
                | "IconResolution"
                | "IconTheme"
                | "DocumentLabel"
                | "DocumentUrl"
                | "Silent"
                | "SilentWithProgress"
                | "Interactive"
                | "Log"
                | "Upgrade"
                | "Custom"
                | "Repair"
                | "FileType"
                | "ReleaseDate"
                | "Commands"
                | "Tags"
                | "FileExtensions"
                | "Protocols"
                | "Capabilities"
                | "RestrictedCapabilities"
                | "InstallModes"
                | "Platform"
                | "UpgradeBehavior"
                | "AuthenticationType"
                | "Argument"
                | "Command"
                | "Uninstall"
                | "Update"
                | "Channel"
                | "InvocationParameter"
                | "ReturnResponse"
                | "ElevationRequirement"
                | "RepairBehavior"
                | "InstallerIdentifier"
                | "ProductId"
                | "StoreProductId"
        )
}

fn merge_documents(id: &str, documents: Vec<ManifestDocument>) -> Result<PackageManifest> {
    let mut singleton: Option<Value> = None;
    let mut version: Option<Value> = None;
    let mut default_locale: Option<Value> = None;
    let mut locales: Vec<(String, Value)> = Vec::new();
    let mut installer: Option<Value> = None;
    let mut seen_locale = BTreeSet::new();
    for document in documents {
        if !document.id.eq_ignore_ascii_case(id) {
            return Err(PublisherError::Invalid(format!(
                "inconsistent PackageIdentifier {} in {}",
                document.id,
                document.path.display()
            )));
        }
        match document.kind.as_str() {
            "singleton" => replace_once(&mut singleton, document.document, "singleton manifest")?,
            "version" => replace_once(&mut version, document.document, "version manifest")?,
            "installer" => replace_once(&mut installer, document.document, "installer manifest")?,
            "defaultlocale" => replace_once(&mut default_locale, document.document, "default-locale manifest")?,
            "locale" => {
                let locale = document.locale.ok_or_else(|| {
                    PublisherError::Invalid(format!(
                        "locale manifest lacks PackageLocale: {}",
                        document.path.display()
                    ))
                })?;
                if !seen_locale.insert(locale.to_ascii_lowercase()) {
                    return Err(PublisherError::Invalid(format!("duplicate locale {locale} for {id}")));
                }
                locales.push((locale, document.document));
            }
            _ => return Err(PublisherError::Invalid("unsupported manifest type".to_owned())),
        }
    }
    if let Some(singleton) = singleton {
        if version.is_some() || default_locale.is_some() || installer.is_some() || !locales.is_empty() {
            return Err(PublisherError::Invalid(format!(
                "singleton and multi-file manifests are mixed for {id}"
            )));
        }
        let mapping = as_mapping(&singleton)?;
        let locale = required_string(mapping, "PackageLocale", Path::new("<singleton>"))?;
        validate_locale(&locale)?;
        if map_get(mapping, "Installers")
            .and_then(Value::as_sequence)
            .is_none_or(Vec::is_empty)
        {
            return Err(PublisherError::Invalid(format!("no installers found for {id}")));
        }
        return Ok(PackageManifest {
            id: required_string(mapping, "PackageIdentifier", Path::new("<singleton>"))?,
            name: required_string(mapping, "PackageName", Path::new("<singleton>"))?,
            version: required_string(mapping, "PackageVersion", Path::new("<singleton>"))?,
            moniker: optional_string(mapping, "Moniker"),
            locale_docs: vec![LocaleDocument {
                locale,
                document: canonicalize_value(singleton),
            }],
        });
    }
    let version = version.ok_or_else(|| PublisherError::Invalid(format!("missing version manifest for {id}")))?;
    let version_map = as_mapping(&version)?.clone();
    let default_locale =
        default_locale.ok_or_else(|| PublisherError::Invalid(format!("missing defaultLocale manifest for {id}")))?;
    let default_map = as_mapping(&default_locale)?.clone();
    let version_id = required_string(&version_map, "PackageIdentifier", Path::new("<version>"))?;
    let version_number = required_string(&version_map, "PackageVersion", Path::new("<version>"))?;
    if !version_id.eq_ignore_ascii_case(id) {
        return Err(PublisherError::Invalid(format!(
            "inconsistent PackageIdentifier in {id}"
        )));
    }
    let installer_map = installer.as_ref().map(as_mapping).transpose()?;
    let installer_map = installer_map
        .ok_or_else(|| PublisherError::Invalid(format!("missing installer manifest for {id} {version_number}")))?;
    let doc_id = required_string(installer_map, "PackageIdentifier", Path::new("<installer>"))?;
    let doc_version = required_string(installer_map, "PackageVersion", Path::new("<installer>"))?;
    if !doc_id.eq_ignore_ascii_case(id) || doc_version != version_number {
        return Err(PublisherError::Invalid(format!(
            "inconsistent installer manifest for {id} {version_number}"
        )));
    }
    let installer_entries = map_get(installer_map, "Installers")
        .and_then(Value::as_sequence)
        .cloned()
        .unwrap_or_default();
    if installer_entries.is_empty() {
        return Err(PublisherError::Invalid(format!(
            "no installers found for {id} {version_number}"
        )));
    }
    let default_locale_name = required_string(&default_map, "PackageLocale", Path::new("<defaultLocale>"))?;
    validate_locale(&default_locale_name)?;
    let has_default_locale = locales
        .iter()
        .any(|(locale, _)| locale.eq_ignore_ascii_case(&default_locale_name));
    if has_default_locale {
        return Err(PublisherError::Invalid(format!(
            "duplicate default locale {default_locale_name} for {id}"
        )));
    }
    let declared_default = required_string(&version_map, "DefaultLocale", Path::new("<version>"))?;
    if !declared_default.eq_ignore_ascii_case(&default_locale_name) {
        return Err(PublisherError::Invalid(format!(
            "DefaultLocale {declared_default} does not match {default_locale_name} for {id}"
        )));
    }
    locales.sort_by(|left, right| compare_utf16(&left.0, &right.0));
    let mut localizations = Vec::with_capacity(locales.len());
    for (locale, locale_value) in locales {
        validate_locale(&locale)?;
        let mut locale_map = as_mapping(&locale_value)?.clone();
        for key in ["ManifestType", "ManifestVersion", "PackageIdentifier", "PackageVersion"] {
            locale_map.remove(Value::String(key.to_owned()));
        }
        localizations.push(Value::Mapping(locale_map));
    }
    let mut merged = version_map.clone();
    merge_yaml_mapping(&mut merged, installer_map);
    merge_yaml_mapping(&mut merged, &default_map);
    merged.insert(
        Value::String("Installers".to_owned()),
        Value::Sequence(installer_entries),
    );
    if !localizations.is_empty() {
        merged.insert(Value::String("Localization".to_owned()), Value::Sequence(localizations));
    }
    set_string(&mut merged, "ManifestType", "singleton");
    let locale_docs = vec![LocaleDocument {
        locale: default_locale_name,
        document: canonicalize_value(Value::Mapping(merged)),
    }];
    let name = required_string(&default_map, "PackageName", Path::new("<defaultLocale>"))?;
    let moniker = optional_string(&default_map, "Moniker").or_else(|| optional_string(&version_map, "Moniker"));
    Ok(PackageManifest {
        id: version_id,
        name,
        version: version_number,
        moniker,
        locale_docs,
    })
}

fn serialize_merged(document: &Value) -> Result<Vec<u8>> {
    let mut bytes = serde_json::to_vec(&CanonicalYaml(document))?;
    bytes.push(b'\n');
    Ok(bytes)
}

type CatalogFields = (
    Vec<String>,
    Vec<String>,
    Vec<String>,
    Vec<String>,
    Vec<String>,
    Vec<String>,
    Vec<String>,
    Option<String>,
    Option<String>,
);

fn catalog_fields_all(packages: &[PackageManifest]) -> Result<CatalogFields> {
    let mut tags = BTreeSet::new();
    let mut commands = BTreeSet::new();
    let mut pfns = BTreeSet::new();
    let mut product_codes = BTreeSet::new();
    let mut upgrade_codes = BTreeSet::new();
    let mut normalized_names = BTreeSet::new();
    let mut normalized_publishers = BTreeSet::new();
    let mut arp_versions = BTreeSet::new();
    for document in packages.iter().flat_map(|package| &package.locale_docs) {
        let map = as_mapping(&document.document)?;
        extend_strings(&mut tags, map, "Tags");
        extend_strings(&mut commands, map, "Commands");
        let mut scopes = vec![map];
        for key in ["Localization", "Installers"] {
            if let Some(values) = map_get(map, key).and_then(Value::as_sequence) {
                scopes.extend(values.iter().filter_map(Value::as_mapping));
            }
        }
        for nested in scopes {
            extend_strings(&mut tags, nested, "Tags");
            extend_strings(&mut commands, nested, "Commands");
            extend_strings(&mut pfns, nested, "PackageFamilyName");
            extend_strings(&mut product_codes, nested, "ProductCode");
            extend_strings(&mut upgrade_codes, nested, "UpgradeCode");
            if let Some(value) = optional_string(nested, "PackageName") {
                let normalized = pinget_core::normalize_catalog_name(&value);
                if !normalized.name.is_empty() {
                    normalized_names.insert(normalized.name);
                }
            }
            if let Some(value) = optional_string(nested, "Publisher") {
                let normalized = pinget_core::normalize_catalog_publisher(&value);
                if !normalized.is_empty() {
                    normalized_publishers.insert(normalized);
                }
            }
            if let Some(entries) = map_get(nested, "AppsAndFeaturesEntries").and_then(Value::as_sequence) {
                for entry in entries {
                    if let Some(entry_map) = entry.as_mapping() {
                        if let Some(value) = optional_string(entry_map, "DisplayName") {
                            let normalized = pinget_core::normalize_catalog_name(&value);
                            if !normalized.name.is_empty() {
                                normalized_names.insert(normalized.name);
                            }
                        }
                        if let Some(value) = optional_string(entry_map, "Publisher") {
                            let normalized = pinget_core::normalize_catalog_publisher(&value);
                            if !normalized.is_empty() {
                                normalized_publishers.insert(normalized);
                            }
                        }
                        if let Some(value) =
                            optional_string(entry_map, "DisplayVersion").filter(|value| !value.trim().is_empty())
                        {
                            arp_versions.insert(value);
                        }
                        extend_strings(&mut product_codes, entry_map, "ProductCode");
                        extend_strings(&mut upgrade_codes, entry_map, "UpgradeCode");
                    }
                }
            }
            if let Some(installers) = map_get(nested, "Installers").and_then(Value::as_sequence) {
                for installer in installers {
                    if let Some(installer_map) = installer.as_mapping() {
                        extend_strings(&mut pfns, installer_map, "PackageFamilyName");
                        extend_strings(&mut product_codes, installer_map, "ProductCode");
                        extend_strings(&mut upgrade_codes, installer_map, "UpgradeCode");
                    }
                }
            }
        }
    }
    let mut versions: Vec<String> = arp_versions.into_iter().collect();
    versions.sort_by(|left, right| compare_winget_versions(left, right));
    let min = versions.first().cloned();
    let max = versions.last().cloned();
    Ok((
        canonical_terms(tags),
        canonical_terms(commands),
        canonical_terms(pfns),
        canonical_terms(product_codes),
        canonical_terms(upgrade_codes),
        canonical_terms(normalized_names),
        canonical_terms(normalized_publishers),
        min,
        max,
    ))
}

fn canonical_terms(terms: BTreeSet<String>) -> Vec<String> {
    let mut unique: BTreeMap<String, String> = BTreeMap::new();
    for term in terms.into_iter().filter(|term| !term.trim().is_empty()) {
        let key = term.to_ascii_lowercase();
        let current = unique.entry(key).or_insert_with(|| term.clone());
        if compare_utf16(&term, current).is_lt() {
            *current = term;
        }
    }
    let mut terms: Vec<_> = unique.into_values().collect();
    terms.sort_by(|left, right| compare_utf16(left, right));
    terms
}

fn insert_terms(
    connection: &Connection,
    table: &str,
    value_column: &str,
    map_table: &str,
    map_column: &str,
    package_row: i64,
    terms: &[String],
) -> Result<()> {
    for term in terms {
        let query = format!(
            "SELECT rowid,{value_column} FROM {table} WHERE {value_column}=?1 COLLATE NOCASE ORDER BY rowid LIMIT 1"
        );
        let existing: Option<(i64, String)> = connection
            .query_row(&query, [term], |row| Ok((row.get(0)?, row.get(1)?)))
            .optional()?;
        let value_row = if let Some((value_row, spelling)) = existing {
            if compare_utf16(term, &spelling).is_lt() {
                connection.execute(
                    &format!("UPDATE {table} SET {value_column}=?1 WHERE rowid=?2"),
                    params![term, value_row],
                )?;
            }
            value_row
        } else {
            connection.execute(&format!("INSERT INTO {table}({value_column}) VALUES(?1)"), [term])?;
            connection.last_insert_rowid()
        };
        let map_sql = format!("INSERT OR IGNORE INTO {map_table}({map_column},package) VALUES(?1,?2)");
        connection.execute(&map_sql, params![value_row, package_row])?;
    }
    Ok(())
}

fn insert_correlations(
    connection: &Connection,
    table: &str,
    value_column: &str,
    package_row: i64,
    values: &[String],
) -> Result<()> {
    let sql = format!("INSERT OR IGNORE INTO {table}({value_column},package) VALUES(?1,?2)");
    for value in values {
        connection.execute(&sql, params![value, package_row])?;
    }
    Ok(())
}

fn snapshot_metadata(staging: &Path, inventory: &Inventory, build_id: &str) -> Result<SnapshotMetadata> {
    let artifacts = hash_tree(&staging.join("feed"))?;
    let index_path = staging.join("Public").join("index.db");
    let index_hash = sha256_hex(&fs::read(&index_path)?);
    let db = Connection::open_with_flags(index_path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    let package_count: i64 = db.query_row("SELECT COUNT(*) FROM packages", [], |row| row.get(0))?;
    let manifest_count = artifacts.keys().filter(|path| path.starts_with("manifests/")).count();
    Ok(SnapshotMetadata {
        schema_version: 1,
        artifact_format_version: PUBLISHER_FORMAT_VERSION,
        build_id: build_id.to_owned(),
        created_utc: Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true),
        manifest_count,
        package_count: usize::try_from(package_count)
            .map_err(|_| PublisherError::Invalid("negative package count in generated index".to_owned()))?,
        index_hash,
        inventory: inventory.files.clone(),
        artifacts,
    })
}

fn verify_snapshot_path(snapshot: &Path) -> Result<()> {
    reject_reparse_components(snapshot)?;
    require_directory(snapshot)?;
    let snapshot_file = snapshot.join("snapshot.json");
    require_regular_file(&snapshot_file)?;
    let metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot_file)?)?;
    validate_format_version(metadata.artifact_format_version, "snapshot")?;
    if metadata.schema_version != 1
        || metadata.build_id != snapshot.file_name().and_then(|v| v.to_str()).unwrap_or_default()
    {
        return Err(PublisherError::Invalid(
            "snapshot identity or schema version is invalid".to_owned(),
        ));
    }
    validate_build_id(&metadata.build_id)?;
    if build_identifier(&Inventory {
        files: metadata.inventory.clone(),
        manifests: Vec::new(),
    })? != metadata.build_id
        || metadata.inventory.values().any(|hash| !is_lower_hex_hash(hash))
    {
        return Err(PublisherError::Invalid(
            "snapshot input inventory does not match its build identifier".to_owned(),
        ));
    }
    let feed = snapshot.join("feed");
    require_directory(&feed)?;
    for (relative, expected_hash) in &metadata.artifacts {
        let path = safe_join(&feed, relative)?;
        let metadata = fs::symlink_metadata(&path)?;
        if is_reparse_point(&metadata) || !metadata.is_file() {
            return Err(PublisherError::Invalid(format!(
                "snapshot contains an unsafe path: {relative}"
            )));
        }
        let bytes = fs::read(&path)?;
        let found = sha256_hex(&bytes);
        if &found != expected_hash {
            return Err(PublisherError::Invalid(format!(
                "snapshot file hash mismatch: {relative}"
            )));
        }
    }
    let found_files = hash_tree(&feed)?;
    if found_files != metadata.artifacts {
        return Err(PublisherError::Invalid(
            "snapshot contains missing or untracked files".to_owned(),
        ));
    }
    if metadata.artifacts.values().any(|hash| !is_lower_hex_hash(hash)) {
        return Err(PublisherError::Invalid(
            "snapshot artifact inventory contains an invalid hash".to_owned(),
        ));
    }
    let manifest_count = metadata
        .artifacts
        .keys()
        .filter(|path| path.starts_with("manifests/"))
        .count();
    if manifest_count != metadata.manifest_count {
        return Err(PublisherError::Invalid(
            "snapshot manifest count does not match its artifact inventory".to_owned(),
        ));
    }
    let index = snapshot.join("Public").join("index.db");
    let public = index
        .parent()
        .ok_or_else(|| PublisherError::Invalid("snapshot index path has no parent".to_owned()))?;
    require_directory(public)?;
    require_regular_file(&index)?;
    if !is_lower_hex_hash(&metadata.index_hash) || sha256_hex(&fs::read(&index)?) != metadata.index_hash {
        return Err(PublisherError::Invalid(
            "snapshot v2 index hash does not match snapshot metadata".to_owned(),
        ));
    }
    let db = Connection::open_with_flags(index, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    let integrity: String = db.query_row("PRAGMA integrity_check", [], |row| row.get(0))?;
    if integrity != "ok" {
        return Err(PublisherError::Invalid(format!(
            "SQLite integrity check failed: {integrity}"
        )));
    }
    let (major, minor): (String, String) = db.query_row(
        "SELECT (SELECT value FROM metadata WHERE name='majorVersion'), (SELECT value FROM metadata WHERE name='minorVersion')",
        [],
        |row| Ok((row.get(0)?, row.get(1)?)),
    )?;
    if major != "2" || minor != "0" {
        return Err(PublisherError::Invalid(format!(
            "snapshot index has unsupported schema version {major}.{minor}"
        )));
    }
    let package_count: i64 = db.query_row("SELECT COUNT(*) FROM packages", [], |row| row.get(0))?;
    if usize::try_from(package_count).ok() != Some(metadata.package_count) {
        return Err(PublisherError::Invalid(
            "snapshot package count does not match its index".to_owned(),
        ));
    }
    let mut statement = db.prepare("SELECT id,hash FROM packages")?;
    let mut rows = statement.query([])?;
    while let Some(row) = rows.next()? {
        let id: String = row.get(0)?;
        let hash: Vec<u8> = row.get(1)?;
        if hash.len() != 32 {
            return Err(PublisherError::Invalid(format!(
                "package {id} has an invalid sidecar hash length"
            )));
        }
        let hash_hex = hash.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
        let relative = format!("packages/{id}/{}/versionData.mszyml", &hash_hex[..8]);
        let sidecar = safe_join(&feed, &relative)?;
        let bytes = fs::read(sidecar).map_err(|error| {
            PublisherError::Invalid(format!("package {id} version-data sidecar is missing: {error}"))
        })?;
        if sha256_hex(&bytes) != hash_hex {
            return Err(PublisherError::Invalid(format!(
                "package {id} version-data hash mismatch"
            )));
        }
        let version_data = decompress_mszip(&bytes)?;
        let value: Value = serde_yaml::from_slice(&version_data)?;
        let version_data_map = value
            .as_mapping()
            .ok_or_else(|| PublisherError::Invalid(format!("package {id} version data is not a mapping")))?;
        if required_string(version_data_map, "sV", Path::new("<versionData>"))? != "1.0" {
            return Err(PublisherError::Invalid(format!(
                "package {id} has an unsupported version-data schema"
            )));
        }
        let versions = map_get(version_data_map, "vD")
            .and_then(Value::as_sequence)
            .ok_or_else(|| PublisherError::Invalid(format!("package {id} version data has no vD entries")))?;
        let mut seen_versions = Vec::new();
        for entry in versions {
            let map = entry
                .as_mapping()
                .ok_or_else(|| PublisherError::Invalid(format!("invalid version entry for {id}")))?;
            let version = required_string(map, "v", Path::new("<versionData>"))?;
            if seen_versions
                .iter()
                .any(|previous: &String| compare_winget_versions(previous, &version).is_eq())
            {
                return Err(PublisherError::Invalid(format!(
                    "duplicate version {version} in version data for {id}"
                )));
            }
            seen_versions.push(version.clone());
            let path = required_string(map, "rP", Path::new("<versionData>"))?;
            let expected = required_string(map, "s256H", Path::new("<versionData>"))?;
            if !is_lower_hex_hash(&expected) {
                return Err(PublisherError::Invalid(format!(
                    "package {id} version data contains an invalid manifest hash"
                )));
            }
            let manifest = safe_join(&feed, &path)?;
            let manifest_bytes = fs::read(manifest)?;
            if sha256_hex(&manifest_bytes) != expected {
                return Err(PublisherError::Invalid(format!(
                    "package {id} manifest hash mismatch: {path}"
                )));
            }
            let value: Value = serde_yaml::from_slice(&manifest_bytes)?;
            let manifest_map = as_mapping(&value)?;
            if required_string(manifest_map, "PackageIdentifier", Path::new("<merged manifest>"))? != id
                || required_string(manifest_map, "PackageVersion", Path::new("<merged manifest>"))? != version
            {
                return Err(PublisherError::Invalid(format!(
                    "version-data manifest identity mismatch for {id} {version}"
                )));
            }
        }
    }
    Ok(())
}

/// Builds the report for a verified snapshot; `changes` is `(changed_files, removed_files)`.
fn report_from_snapshot(snapshot: &Path, build_id: &str, changes: (usize, usize)) -> Result<BuildReport> {
    let metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
    Ok(BuildReport {
        build_id: build_id.to_owned(),
        snapshot_directory: display_path(snapshot),
        changed_files: changes.0,
        removed_files: changes.1,
        packages: metadata.package_count,
        manifests: metadata.manifest_count,
        files: metadata.artifacts.len() + 1,
    })
}

fn json_count(value: &serde_json::Value, property: &str) -> Result<usize> {
    let count = value[property]
        .as_u64()
        .ok_or_else(|| PublisherError::Invalid(format!("publisher summary is missing {property}")))?;
    usize::try_from(count).map_err(|_| PublisherError::Invalid(format!("publisher summary {property} is too large")))
}

fn record_build(state: &Path, build_id: &str, snapshot: &SnapshotMetadata) -> Result<()> {
    let mut connection = open_state(state, true)?;
    let transaction = connection.transaction()?;
    let summary = serde_json::json!({
        "manifestCount": snapshot.manifest_count,
        "packageCount": snapshot.package_count,
        "createdUtc": snapshot.created_utc,
        "snapshot": format!("builds/{build_id}"),
    });
    transaction.execute(
        "INSERT OR REPLACE INTO builds(id,inventory,summary) VALUES(?1,?2,?3)",
        params![
            build_id,
            canonical_inventory_json(&snapshot.inventory)?,
            serde_json::to_string(&summary)?
        ],
    )?;
    transaction.execute(
        "INSERT INTO metadata(name,value) VALUES('lastBuildId',?1) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
        [build_id],
    )?;
    transaction.commit()?;
    Ok(())
}

fn open_state(state: &Path, create: bool) -> Result<Connection> {
    let path = state.join("publisher.db");
    let existed = path.exists();
    match fs::symlink_metadata(&path) {
        Ok(_) => require_regular_file(&path)?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.into()),
    }
    if !create && !path.is_file() {
        return Err(PublisherError::Invalid(format!(
            "publisher state does not exist: {}",
            path.display()
        )));
    }
    if create && !existed {
        drop(OpenOptions::new().write(true).create_new(true).open(&path)?);
    } else if create {
        // Validate existing files through a read-only connection before opening a writer.
        drop(open_state(state, false)?);
    }
    let connection = if create {
        Connection::open(&path)?
    } else {
        Connection::open_with_flags(&path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?
    };
    if create && !existed {
        connection.execute_batch(STATE_SCHEMA)?;
        connection.execute(
            "INSERT OR IGNORE INTO metadata(name,value) VALUES('schemaVersion','1')",
            [],
        )?;
        connection.execute(
            "INSERT INTO metadata(name,value) VALUES('artifactFormatVersion',?1)",
            [PUBLISHER_FORMAT_VERSION.to_string()],
        )?;
    }
    let version: i64 = connection.query_row("PRAGMA user_version", [], |row| row.get(0))?;
    if version != 1 {
        return Err(PublisherError::Invalid(format!(
            "unsupported publisher state schema version: {version}"
        )));
    }
    let schema_version: Option<String> = connection
        .query_row("SELECT value FROM metadata WHERE name='schemaVersion'", [], |row| {
            row.get(0)
        })
        .optional()?;
    if schema_version.as_deref() != Some("1") {
        return Err(PublisherError::Invalid(format!(
            "unsupported or missing publisher state schemaVersion: {}",
            schema_version.as_deref().unwrap_or("<missing>")
        )));
    }
    let format_version: Option<String> = connection
        .query_row(
            "SELECT value FROM metadata WHERE name='artifactFormatVersion'",
            [],
            |row| row.get(0),
        )
        .optional()?;
    let format_version = format_version
        .as_deref()
        .unwrap_or("1")
        .parse::<u32>()
        .map_err(|_| PublisherError::Invalid("publisher state artifactFormatVersion is invalid".to_owned()))?;
    validate_format_version(format_version, "publisher state")?;
    let tables: Vec<String> = {
        let mut statement = connection
            .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")?;
        statement
            .query_map([], |row| row.get(0))?
            .collect::<std::result::Result<_, _>>()?
    };
    if tables != ["builds", "metadata"] {
        return Err(PublisherError::Invalid(
            "publisher state has an unexpected schema".to_owned(),
        ));
    }
    connection.prepare("SELECT name,value FROM metadata LIMIT 0")?;
    connection.prepare("SELECT id,inventory,summary FROM builds LIMIT 0")?;
    Ok(connection)
}

fn validate_recorded_snapshot(connection: &Connection, snapshot: &SnapshotMetadata) -> Result<()> {
    validate_format_version(snapshot.artifact_format_version, "snapshot")?;
    let record: Option<(String, String)> = connection
        .query_row(
            "SELECT inventory,summary FROM builds WHERE id=?1",
            [&snapshot.build_id],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .optional()?;
    let (inventory, summary) = record.ok_or_else(|| {
        PublisherError::Invalid(format!(
            "snapshot {} is not recorded in publisher state",
            snapshot.build_id
        ))
    })?;
    let inventory: BTreeMap<String, String> = serde_json::from_str(&inventory)?;
    let summary: serde_json::Value = serde_json::from_str(&summary)?;
    if inventory != snapshot.inventory
        || json_count(&summary, "manifestCount")? != snapshot.manifest_count
        || json_count(&summary, "packageCount")? != snapshot.package_count
        || summary["createdUtc"].as_str() != Some(snapshot.created_utc.as_str())
    {
        return Err(PublisherError::Invalid(
            "snapshot metadata does not match its recorded build".to_owned(),
        ));
    }
    Ok(())
}

fn validate_format_version(version: u32, subject: &str) -> Result<()> {
    if version != PUBLISHER_FORMAT_VERSION {
        return Err(PublisherError::Invalid(format!(
            "{subject} uses unsupported artifact format version {}; use a new --state directory; existing state and snapshots are preserved",
            if version == 0 { 1 } else { version }
        )));
    }
    Ok(())
}

fn remove_owned_staging(path: &Path) -> Result<()> {
    reject_reparse_components(path)?;
    require_directory(path)?;
    let mut files = Vec::new();
    gather_files(path, path, &mut files)?;
    fs::remove_dir_all(path)?;
    Ok(())
}

fn acquire_destination_lock(destination: &Path) -> Result<WriterLock> {
    let parent = destination
        .parent()
        .ok_or_else(|| PublisherError::Invalid("destination must not be a volume root".to_owned()))?;
    let name = destination
        .file_name()
        .and_then(|value| value.to_str())
        .ok_or_else(|| PublisherError::Invalid("destination name must be Unicode".to_owned()))?;
    reject_reparse_components(parent)?;
    fs::create_dir_all(parent)?;
    let lock = parent.join(format!("{name}.pinget-publisher.lock"));
    let result = (|| {
        reject_reparse_components(&lock)?;
        if lock.exists() {
            require_regular_file(&lock)?;
        }
        Ok(WriterLock {
            _file: lock_file(&lock)?,
        })
    })();
    result.map_err(|error: PublisherError| {
        PublisherError::Invalid(format!(
            "destination writer lock unavailable at {}: {error}",
            lock.display()
        ))
    })
}

fn is_published(connection: &Connection, build_id: &str) -> Result<bool> {
    let published: Option<String> = connection
        .query_row(
            "SELECT value FROM metadata WHERE name='lastPublishedBuildId'",
            [],
            |row| row.get(0),
        )
        .optional()?;
    Ok(published.as_deref() == Some(build_id))
}

fn acquire_lock(state: &Path) -> Result<WriterLock> {
    let lock_path = state.join(".publisher.lock");
    match fs::symlink_metadata(&lock_path) {
        Ok(_) => require_regular_file(&lock_path)?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.into()),
    }
    let file = lock_file(&lock_path)?;
    Ok(WriterLock { _file: file })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct OwnerMarker {
    schema_version: u32,
    owner: String,
    package_name: String,
    publisher: String,
    package_version: String,
    build_id: String,
    index_hash: String,
    unsigned_development: bool,
    published_utc: String,
    artifacts: BTreeMap<String, String>,
}

#[derive(Default)]
struct PublicationRollback {
    files: Vec<PathBuf>,
    directories: Vec<PathBuf>,
    committed: bool,
}

impl PublicationRollback {
    fn create_directories(&mut self, root: &Path, directory: &Path) -> Result<()> {
        let relative = directory
            .strip_prefix(root)
            .map_err(|_| PublisherError::Invalid("publication directory escaped root".to_owned()))?;
        let mut current = root.to_path_buf();
        for component in relative.components() {
            current.push(component);
            reject_reparse_components(&current)?;
            if !current.exists() {
                fs::create_dir(&current)?;
                self.directories.push(current.clone());
            }
        }
        Ok(())
    }
}

impl Drop for PublicationRollback {
    fn drop(&mut self) {
        if !self.committed {
            for file in self.files.iter().rev() {
                let _ = fs::remove_file(file);
            }
            for directory in self.directories.iter().rev() {
                let _ = fs::remove_dir(directory);
            }
        }
    }
}

fn package_and_publish(
    snapshot: &Path,
    temporary: &Path,
    destination: &Path,
    options: &PublishOptions,
    build_id: &str,
    prior: Option<OwnerMarker>,
    after_copy: impl FnOnce(&Path) -> Result<()>,
) -> Result<PublishReport> {
    let unsigned_development = matches!(options.signing, Signing::UnsignedDevelopment);
    let metadata: SnapshotMetadata = serde_json::from_slice(&fs::read(snapshot.join("snapshot.json"))?)?;
    let package_root = temporary.join("package");
    fs::create_dir_all(package_root.join("Public"))?;
    fs::copy(
        snapshot.join("Public").join("index.db"),
        package_root.join("Public").join("index.db"),
    )?;
    create_appx_assets(&package_root, &options.name, &options.publisher, &options.version)?;
    let msix = temporary.join("source2.msix");
    let makeappx = find_tool("makeappx.exe", options.makeappx.as_deref()).ok_or_else(|| {
        PublisherError::Tool("makeappx.exe was not found; pass --makeappx or add it to PATH".to_owned())
    })?;
    run_tool(
        &makeappx,
        [
            "pack",
            "/d",
            &package_root.to_string_lossy(),
            "/p",
            &msix.to_string_lossy(),
            "/o",
        ],
    )?;
    match &options.signing {
        Signing::Pfx { path, password_env } => {
            if !path.is_file() {
                return Err(PublisherError::Invalid(format!(
                    "PFX does not exist: {}",
                    path.display()
                )));
            }
            if std::env::var_os(password_env).is_none() {
                return Err(PublisherError::Invalid(format!(
                    "password environment variable {password_env} is not set"
                )));
            }
            validate_certificate_publisher(path, password_env, &options.publisher)?;
            let password = std::env::var(password_env).map_err(|_| {
                PublisherError::Invalid(format!("password environment variable {password_env} is not set"))
            })?;
            let signtool = find_tool("signtool.exe", options.signtool.as_deref()).ok_or_else(|| {
                PublisherError::Tool("signtool.exe was not found; pass --signtool or add it to PATH".to_owned())
            })?;
            let pfx_path = path.to_string_lossy();
            let msix_path = msix.to_string_lossy();
            let mut arguments = vec!["sign", "/fd", "SHA256", "/f", &pfx_path, "/p", &password];
            if let Some(url) = &options.timestamp_url {
                arguments.extend(["/tr", url, "/td", "SHA256"]);
            }
            arguments.push(&msix_path);
            run_sign_tool(&signtool, &arguments, Some(&password))?;
            run_tool(&signtool, ["verify", "/pa", "/v", &msix.to_string_lossy()])?;
        }
        Signing::UnsignedDevelopment => {}
    }
    let mut marker = prior.unwrap_or_else(|| OwnerMarker {
        schema_version: 1,
        owner: "Pinget.Publisher".to_owned(),
        package_name: options.name.clone(),
        publisher: options.publisher.clone(),
        package_version: options.version.clone(),
        build_id: build_id.to_owned(),
        index_hash: metadata.index_hash.clone(),
        unsigned_development,
        published_utc: String::new(),
        artifacts: BTreeMap::new(),
    });
    marker.package_version = options.version.clone();
    marker.build_id = build_id.to_owned();
    marker.index_hash = metadata.index_hash.clone();
    marker.unsigned_development = unsigned_development;
    verify_msix_contents(&msix, &marker)?;
    let package_hash = sha256_hex(&fs::read(&msix)?);
    let mut rollback = PublicationRollback::default();
    for relative in metadata.artifacts.keys() {
        let target = safe_join(destination, relative)?;
        if target.exists() {
            let hash = sha256_hex(&fs::read(&target)?);
            if metadata.artifacts.get(relative) != Some(&hash) {
                return Err(PublisherError::Invalid(format!(
                    "immutable publication path conflicts: {relative}"
                )));
            }
        }
    }
    for relative in metadata.artifacts.keys() {
        let target = safe_join(destination, relative)?;
        if !target.exists() {
            let source = safe_join(&snapshot.join("feed"), relative)?;
            let parent = target
                .parent()
                .ok_or_else(|| PublisherError::Invalid(format!("invalid publication path {relative}")))?;
            rollback.create_directories(destination, parent)?;
            let mut output = OpenOptions::new().write(true).create_new(true).open(&target)?;
            rollback.files.push(target.clone());
            let mut input = File::open(source)?;
            std::io::copy(&mut input, &mut output)?;
            output.sync_all()?;
            drop(output);
            if metadata.artifacts.get(relative) != Some(&sha256_hex(&fs::read(&target)?)) {
                return Err(PublisherError::Invalid(format!(
                    "copied publication hash mismatch: {relative}"
                )));
            }
        }
    }
    after_copy(destination)?;
    let target_msix = destination.join("source2.msix");
    let staged_package = stage_package(&msix, destination, build_id)?;
    rollback.files.push(staged_package.clone());

    marker.owner = "Pinget.Publisher".to_owned();
    marker.package_name = options.name.clone();
    marker.publisher = options.publisher.clone();
    marker.package_version = options.version.clone();
    marker.build_id = build_id.to_owned();
    marker.index_hash = metadata.index_hash;
    marker.unsigned_development = unsigned_development;
    marker.published_utc = Utc::now().to_rfc3339();
    marker.artifacts = published_inventory_excluding(destination, &[temporary, staged_package.as_path()])?;
    marker.artifacts.insert("source2.msix".to_owned(), package_hash);
    let old_marker_path = destination.join(".pinget-publisher.json");
    let marker_backup = temporary.join("prior-marker.json");
    let has_old_marker = old_marker_path.exists();
    if has_old_marker {
        fs::copy(&old_marker_path, &marker_backup)?;
        OpenOptions::new().write(true).open(&marker_backup)?.sync_all()?;
    }
    let marker_temp = destination.join(".pinget-publisher.json.new");
    write_json(&marker_temp, &marker)?;
    rollback.files.push(marker_temp.clone());
    if let Err(error) = atomic_replace(&marker_temp, &destination.join(".pinget-publisher.json"))
        .and_then(|()| atomic_replace(&staged_package, &target_msix))
    {
        if has_old_marker {
            let _ = atomic_replace(&marker_backup, &old_marker_path);
        } else {
            let _ = fs::remove_file(destination.join(".pinget-publisher.json"));
        }
        let _ = fs::remove_file(marker_temp);
        let _ = fs::remove_file(staged_package);
        return Err(error);
    }
    rollback.committed = true;

    // The marker is the publication commit record. Persist a matching state row.
    let state = options.state.canonicalize()?;
    mark_published(&state, build_id)?;
    fs::remove_dir_all(temporary)?;
    let verified = verify_destination_with_tool(destination, options.signtool.as_deref())?;
    Ok(PublishReport {
        valid: true,
        subject: verified.subject,
        artifact_count: verified.artifact_count,
        build_id: build_id.to_owned(),
        destination: display_path(destination),
        unsigned_development,
        reused: false,
    })
}

fn stage_package(source: &Path, destination: &Path, build_id: &str) -> Result<PathBuf> {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| PublisherError::Invalid(format!("system clock is before Unix epoch: {error}")))?
        .as_nanos();
    let staged = destination.join(format!(".source2.{build_id}.{}.{}.new", std::process::id(), timestamp));
    let mut output = OpenOptions::new().write(true).create_new(true).open(&staged)?;
    let result = (|| -> Result<()> {
        let mut input = File::open(source)?;
        std::io::copy(&mut input, &mut output)?;
        output.sync_all()?;
        Ok(())
    })();
    if let Err(error) = result {
        drop(output);
        let _ = fs::remove_file(&staged);
        return Err(error);
    }
    Ok(staged)
}

/// Removes the verbatim \\?\ prefix added by canonicalization when the plain form is equivalent.
fn display_path(path: &Path) -> PathBuf {
    let text = path.to_string_lossy();
    if let Some(rest) = text.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = text.strip_prefix(r"\\?\")
        && rest.as_bytes().get(1) == Some(&b':')
    {
        return PathBuf::from(rest);
    }
    path.to_path_buf()
}

fn mark_published(state: &Path, build_id: &str) -> Result<()> {
    let mut connection = open_state(state, true)?;
    let transaction = connection.transaction()?;
    transaction.execute(
        "INSERT INTO metadata(name,value) VALUES('lastPublishedBuildId',?1) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
        [build_id],
    )?;
    transaction.commit()?;
    Ok(())
}

#[cfg(windows)]
fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    const MOVEFILE_REPLACE_EXISTING: u32 = 0x1;
    const MOVEFILE_WRITE_THROUGH: u32 = 0x8;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn MoveFileExW(existing: *const u16, new: *const u16, flags: u32) -> i32;
    }
    let source_wide: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination_wide: Vec<u16> = destination.as_os_str().encode_wide().chain(Some(0)).collect();
    // SAFETY: both paths are NUL-terminated UTF-16 buffers that remain live for the call.
    if unsafe {
        MoveFileExW(
            source_wide.as_ptr(),
            destination_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        return Err(std::io::Error::last_os_error().into());
    }
    Ok(())
}

#[cfg(not(windows))]
fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    fs::rename(source, destination)?;
    Ok(())
}

#[cfg(windows)]
fn lock_file(path: &Path) -> Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .share_mode(0)
        .open(path)
        .map_err(|error| {
            if error.kind() == std::io::ErrorKind::PermissionDenied {
                PublisherError::Invalid(format!("another publisher is active: {}", path.display()))
            } else {
                error.into()
            }
        })
}

#[cfg(not(windows))]
fn lock_file(path: &Path) -> Result<File> {
    use fs2::FileExt;
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(path)?;
    file.try_lock_exclusive()
        .map_err(|_| PublisherError::Invalid(format!("another publisher is active: {}", path.display())))?;
    Ok(file)
}

fn verify_msix_contents(package_path: &Path, marker: &OwnerMarker) -> Result<()> {
    let file = File::open(package_path)?;
    let mut archive = ZipArchive::new(file)
        .map_err(|error| PublisherError::Invalid(format!("could not read source MSIX: {error}")))?;
    let index_hash = {
        let mut index = archive.by_name("Public/index.db").map_err(|error| {
            PublisherError::Invalid(format!("source MSIX does not contain Public/index.db: {error}"))
        })?;
        let mut bytes = Vec::new();
        index.read_to_end(&mut bytes)?;
        sha256_hex(&bytes)
    };
    if index_hash != marker.index_hash {
        return Err(PublisherError::Invalid(
            "source MSIX contains a v2 index different from the published build".to_owned(),
        ));
    }
    let manifest = {
        let mut entry = archive.by_name("AppxManifest.xml").map_err(|error| {
            PublisherError::Invalid(format!("source MSIX does not contain AppxManifest.xml: {error}"))
        })?;
        let mut bytes = Vec::new();
        entry.read_to_end(&mut bytes)?;
        bytes
    };
    let mut reader = Reader::from_reader(manifest.as_slice());
    reader.config_mut().trim_text(true);
    let mut identity = None;
    let mut extension = None;
    loop {
        match reader.read_event() {
            Ok(Event::Start(element) | Event::Empty(element)) => {
                let local_name = element.local_name();
                if local_name.as_ref() == b"Identity" {
                    if identity.replace(xml_attributes(&reader, &element)?).is_some() {
                        return Err(PublisherError::Invalid(
                            "source MSIX has multiple package identities".to_owned(),
                        ));
                    }
                } else if local_name.as_ref() == b"AppExtension" {
                    let attributes = xml_attributes(&reader, &element)?;
                    match (attributes.get("Name").map(String::as_str), extension.is_some()) {
                        (Some("com.microsoft.winget.source"), true) => {
                            return Err(PublisherError::Invalid(
                                "source MSIX has multiple WinGet source extensions".to_owned(),
                            ));
                        }
                        (Some("com.microsoft.winget.source"), false) => {
                            extension = Some(attributes);
                        }
                        _ => {}
                    }
                }
            }
            Ok(Event::Eof) => break,
            Ok(_) => {}
            Err(error) => {
                return Err(PublisherError::Invalid(format!(
                    "could not parse source MSIX manifest: {error}"
                )));
            }
        }
    }
    let identity =
        identity.ok_or_else(|| PublisherError::Invalid("source MSIX manifest has no Identity element".to_owned()))?;
    if identity.get("Name") != Some(&marker.package_name)
        || identity.get("Publisher") != Some(&marker.publisher)
        || identity.get("Version") != Some(&marker.package_version)
    {
        return Err(PublisherError::Invalid(
            "source MSIX identity does not match the publisher ownership marker".to_owned(),
        ));
    }
    let extension = extension
        .ok_or_else(|| PublisherError::Invalid("source MSIX has no WinGet preindexed source extension".to_owned()))?;
    if extension.get("PublicFolder").is_none_or(|value| value != "Public") {
        return Err(PublisherError::Invalid(
            "source MSIX extension does not expose the Public folder".to_owned(),
        ));
    }
    Ok(())
}

fn xml_attributes<R: std::io::BufRead>(
    reader: &Reader<R>,
    element: &quick_xml::events::BytesStart<'_>,
) -> Result<BTreeMap<String, String>> {
    let mut attributes = BTreeMap::new();
    for attribute in element.attributes() {
        let attribute =
            attribute.map_err(|error| PublisherError::Invalid(format!("invalid AppxManifest attribute: {error}")))?;
        let name = String::from_utf8_lossy(attribute.key.local_name().as_ref()).into_owned();
        let value = attribute
            .decode_and_unescape_value(reader.decoder())
            .map_err(|error| PublisherError::Invalid(format!("invalid AppxManifest attribute: {error}")))?
            .into_owned();
        attributes.insert(name, value);
    }
    Ok(attributes)
}

fn verify_destination(destination: &Path) -> Result<VerifyReport> {
    verify_destination_with_tool(destination, None)
}

fn verify_destination_with_tool(destination: &Path, signtool_path: Option<&Path>) -> Result<VerifyReport> {
    reject_reparse_components(destination)?;
    let destination = fs::canonicalize(destination)?;
    let marker = read_owner_marker(&destination.join(".pinget-publisher.json"))?
        .ok_or_else(|| PublisherError::Invalid("destination has no Pinget ownership marker".to_owned()))?;
    for (relative, expected) in &marker.artifacts {
        let path = safe_join(&destination, relative)?;
        let bytes = fs::read(&path)?;
        let found = sha256_hex(&bytes);
        if &found != expected {
            return Err(PublisherError::Invalid(format!(
                "published file hash mismatch: {relative}"
            )));
        }
    }
    let current = published_inventory(&destination)?;
    if current != marker.artifacts {
        return Err(PublisherError::Invalid(
            "published directory has missing or untracked files".to_owned(),
        ));
    }
    if !is_lower_hex_hash(&marker.index_hash) {
        return Err(PublisherError::Invalid(
            "publisher marker contains an invalid index hash".to_owned(),
        ));
    }
    verify_msix_contents(&destination.join("source2.msix"), &marker)?;
    if !marker.unsigned_development {
        if !cfg!(windows) {
            return Err(PublisherError::Tool(
                "MSIX signature verification requires Windows signtool on this platform".to_owned(),
            ));
        }
        let signtool = find_tool("signtool.exe", signtool_path)
            .ok_or_else(|| PublisherError::Tool("signtool.exe was not found on PATH".to_owned()))?;
        let package_path = destination.join("source2.msix");
        run_tool(&signtool, ["verify", "/pa", "/v", &package_path.to_string_lossy()])?;
    }
    Ok(VerifyReport {
        valid: true,
        subject: "published WinGet v2 static source".to_owned(),
        artifact_count: marker.artifacts.len(),
        build_id: Some(marker.build_id),
    })
}

fn ensure_owned_destination(destination: &Path) -> Result<()> {
    let marker = destination.join(".pinget-publisher.json");
    let entries: Vec<_> = fs::read_dir(destination)?.collect::<std::io::Result<Vec<_>>>()?;
    if entries.is_empty() || marker.is_file() {
        return Ok(());
    }
    Err(PublisherError::Invalid(format!(
        "refusing to publish into non-empty unowned destination: {}",
        destination.display()
    )))
}

fn read_owner_marker(path: &Path) -> Result<Option<OwnerMarker>> {
    match fs::symlink_metadata(path) {
        Ok(_) => require_regular_file(path)?,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error.into()),
    }
    let bytes = fs::read(path)?;
    if bytes.len() > 8 * 1024 * 1024 {
        return Err(PublisherError::Invalid(
            "publication ownership marker exceeds the 8 MiB limit".to_owned(),
        ));
    }
    let marker: OwnerMarker = serde_json::from_slice(&bytes)?;
    if marker.schema_version != 1 {
        return Err(PublisherError::Invalid(format!(
            "unsupported publication inventory schema version {}",
            marker.schema_version
        )));
    }
    if marker.owner != "Pinget.Publisher"
        || marker.package_name.is_empty()
        || marker.publisher.is_empty()
        || !is_lower_hex_hash(&marker.index_hash)
    {
        return Err(PublisherError::Invalid(
            "publisher ownership marker identity is invalid".to_owned(),
        ));
    }
    validate_build_id(&marker.build_id)?;
    validate_msix_version(&marker.package_version)?;
    for (relative, hash) in &marker.artifacts {
        validate_relative_path(Path::new(relative))?;
        if relative == ".pinget-publisher.json" || !is_lower_hex_hash(hash) {
            return Err(PublisherError::Invalid(
                "publication ownership marker contains an invalid inventory entry".to_owned(),
            ));
        }
    }
    if !marker.artifacts.contains_key("source2.msix") {
        return Err(PublisherError::Invalid(
            "publisher ownership marker does not list source2.msix".to_owned(),
        ));
    }
    Ok(Some(marker))
}

fn published_inventory(root: &Path) -> Result<BTreeMap<String, String>> {
    published_inventory_excluding(root, &[])
}

fn published_inventory_excluding(root: &Path, excluded: &[&Path]) -> Result<BTreeMap<String, String>> {
    let mut files = Vec::new();
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        if excluded.iter().any(|path| *path == entry.path()) {
            continue;
        }
        if entry.file_name() == ".pinget-publisher.json" || entry.file_name() == ".pinget-publisher.json.new" {
            continue;
        }
        gather_files(root, &entry.path(), &mut files)?;
    }
    files.sort();
    let mut inventory = BTreeMap::new();
    for path in files {
        let relative = path
            .strip_prefix(root)
            .map_err(|_| PublisherError::Invalid("published path escaped destination".to_owned()))?;
        let key = relative.to_string_lossy().replace('\\', "/");
        inventory.insert(key, sha256_hex(&fs::read(path)?));
    }
    Ok(inventory)
}

fn create_appx_assets(root: &Path, name: &str, publisher: &str, version: &str) -> Result<()> {
    fs::create_dir_all(root.join("Assets"))?;
    let escaped_publisher = escape_xml(publisher);
    let manifest = format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" xmlns:uap=\"http://schemas.microsoft.com/appx/manifest/uap/windows10\" xmlns:uap3=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/3\" IgnorableNamespaces=\"uap uap3\"><Identity Name=\"{name}\" Publisher=\"{escaped_publisher}\" Version=\"{version}\" ProcessorArchitecture=\"neutral\"/><Properties><DisplayName>Pinget preindexed source</DisplayName><PublisherDisplayName>{escaped_publisher}</PublisherDisplayName><Logo>Assets/StoreLogo.png</Logo></Properties><Dependencies><TargetDeviceFamily Name=\"Windows.Universal\" MinVersion=\"10.0.17763.0\" MaxVersionTested=\"10.0.22621.0\"/></Dependencies><Applications><Application Id=\"SourceData\"><uap:VisualElements DisplayName=\"Pinget preindexed source\" Description=\"Pinget static package source\" BackgroundColor=\"#0078d7\" Square150x150Logo=\"Assets/Logo150.png\" Square44x44Logo=\"Assets/Logo44.png\" AppListEntry=\"none\"/><Extensions><uap3:Extension Category=\"windows.appExtension\"><uap3:AppExtension Name=\"com.microsoft.winget.source\" Id=\"IndexDB\" DisplayName=\"Pinget preindexed source\" Description=\"Pinget static package source\" PublicFolder=\"Public\"/></uap3:Extension></Extensions></Application></Applications><Resources><Resource Language=\"und\"/></Resources></Package>\n"
    );
    fs::write(root.join("AppxManifest.xml"), manifest)?;
    write_png_logo(&root.join("Assets").join("StoreLogo.png"), 50)?;
    write_png_logo(&root.join("Assets").join("Logo150.png"), 150)?;
    write_png_logo(&root.join("Assets").join("Logo44.png"), 44)?;
    Ok(())
}

fn escape_xml(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

fn write_png_logo(path: &Path, size: usize) -> Result<()> {
    use flate2::Compression;
    use flate2::write::ZlibEncoder;

    let mut row = Vec::with_capacity(size * 4 + 1);
    row.push(0);
    for _ in 0..size {
        row.extend_from_slice(&[0x00, 0x78, 0xd7, 0xff]);
    }
    let mut scanlines = Vec::with_capacity(size * (size * 4 + 1));
    for _ in 0..size {
        scanlines.extend_from_slice(&row);
    }
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&scanlines)?;
    let compressed = encoder.finish()?;

    let mut png = b"\x89PNG\r\n\x1a\n".to_vec();
    let mut header = Vec::with_capacity(13);
    let size = u32::try_from(size)
        .map_err(|_| PublisherError::Invalid("generated PNG dimensions are too large".to_owned()))?;
    header.extend_from_slice(&size.to_be_bytes());
    header.extend_from_slice(&size.to_be_bytes());
    header.extend_from_slice(&[8, 6, 0, 0, 0]);
    append_png_chunk(&mut png, b"IHDR", &header)?;
    append_png_chunk(&mut png, b"IDAT", &compressed)?;
    append_png_chunk(&mut png, b"IEND", &[])?;
    fs::write(path, png)?;
    Ok(())
}

fn append_png_chunk(output: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) -> Result<()> {
    let length = u32::try_from(data.len())
        .map_err(|_| PublisherError::Invalid("generated PNG chunk is too large".to_owned()))?;
    output.extend_from_slice(&length.to_be_bytes());
    output.extend_from_slice(kind);
    output.extend_from_slice(data);
    let mut checksum = kind.to_vec();
    checksum.extend_from_slice(data);
    output.extend_from_slice(&crc32(&checksum).to_be_bytes());
    Ok(())
}

fn crc32(bytes: &[u8]) -> u32 {
    let mut crc = !0u32;
    for byte in bytes {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = (crc >> 1) ^ (0xedb8_8320 & (0u32.wrapping_sub(crc & 1)));
        }
    }
    !crc
}

fn run_tool<const N: usize>(tool: &Path, args: [&str; N]) -> Result<()> {
    let output = Command::new(tool).args(args).output()?;
    if output.status.success() {
        return Ok(());
    }
    Err(PublisherError::Tool(format!(
        "{} failed ({}): {}",
        tool.display(),
        output.status,
        String::from_utf8_lossy(&output.stderr).trim()
    )))
}

fn run_sign_tool(tool: &Path, args: &[&str], secret: Option<&str>) -> Result<()> {
    let output = Command::new(tool).args(args).output()?;
    if output.status.success() {
        return Ok(());
    }
    let stderr = String::from_utf8_lossy(&output.stderr);
    let stderr = secret.map_or_else(|| stderr.to_string(), |secret| stderr.replace(secret, "<redacted>"));
    Err(PublisherError::Tool(format!(
        "{} failed ({}): {}",
        tool.display(),
        output.status,
        stderr.trim()
    )))
}

fn validate_certificate_publisher(path: &Path, password_env: &str, publisher: &str) -> Result<()> {
    let powershell = find_tool("powershell.exe", None).ok_or_else(|| {
        PublisherError::Tool("Windows PowerShell is required to inspect the signing certificate".to_owned())
    })?;
    let script = "$ErrorActionPreference='Stop'; try { $p=[Environment]::GetEnvironmentVariable($env:PINGET_PUBLISHER_PASSWORD_NAME); $c=[System.Security.Cryptography.X509Certificates.X509Certificate2]::new($env:PINGET_PUBLISHER_PFX_PATH,$p,[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet); $now=[DateTime]::UtcNow; $eku=$c.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }; $codeSigning=$false; if ($eku) { $codeSigning=@($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0 }; $result=@{ subject=$c.Subject; hasPrivateKey=$c.HasPrivateKey; validNow=($now -ge $c.NotBefore.ToUniversalTime() -and $now -le $c.NotAfter.ToUniversalTime()); codeSigning=$codeSigning }; [Console]::Out.Write((ConvertTo-Json -InputObject $result -Compress)) } catch { [Console]::Error.WriteLine('Unable to inspect the signing certificate.'); exit 1 }";
    let output = Command::new(powershell)
        .args(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
        .env("PINGET_PUBLISHER_PFX_PATH", path)
        .env("PINGET_PUBLISHER_PASSWORD_NAME", password_env)
        .output()?;
    if !output.status.success() {
        return Err(PublisherError::Invalid(
            "unable to inspect the PFX signing certificate; confirm its password and certificate contents".to_owned(),
        ));
    }
    let certificate: serde_json::Value = serde_json::from_slice(&output.stdout)
        .map_err(|_| PublisherError::Invalid("unable to parse signing certificate details".to_owned()))?;
    let subject = certificate["subject"].as_str().unwrap_or_default();
    if !subject.eq_ignore_ascii_case(publisher) {
        return Err(PublisherError::Invalid(format!(
            "PFX certificate subject does not match --publisher (expected {publisher:?}, found {subject:?})"
        )));
    }
    if certificate["hasPrivateKey"] != true {
        return Err(PublisherError::Invalid(
            "PFX certificate does not contain a private key".to_owned(),
        ));
    }
    if certificate["validNow"] != true {
        return Err(PublisherError::Invalid(
            "PFX certificate is not currently valid".to_owned(),
        ));
    }
    if certificate["codeSigning"] != true {
        return Err(PublisherError::Invalid(
            "PFX certificate must include the Code Signing enhanced key usage".to_owned(),
        ));
    }
    Ok(())
}

fn find_tool(name: &str, explicit: Option<&Path>) -> Option<PathBuf> {
    if let Some(path) = explicit {
        return path.is_file().then(|| path.to_path_buf());
    }
    if let Some(path_env) = std::env::var_os("PATH")
        && let Some(path) = std::env::split_paths(&path_env)
            .map(|directory| directory.join(name))
            .find(|path| path.is_file())
    {
        return Some(path);
    }
    if !cfg!(windows) || !["makeappx.exe", "signtool.exe"].contains(&name) {
        return None;
    }
    let root = PathBuf::from(std::env::var_os("ProgramFiles(x86)")?)
        .join("Windows Kits")
        .join("10")
        .join("bin");
    let host_arch = std::env::var("PROCESSOR_ARCHITEW6432")
        .or_else(|_| std::env::var("PROCESSOR_ARCHITECTURE"))
        .unwrap_or_default();
    let architectures: &[&str] = if host_arch.eq_ignore_ascii_case("ARM64") || cfg!(target_arch = "aarch64") {
        &["arm64", "x64", "x86"]
    } else {
        &["x64", "x86"]
    };
    find_sdk_tool(&root, name, architectures)
}

fn find_sdk_tool(root: &Path, name: &str, architectures: &[&str]) -> Option<PathBuf> {
    let mut versions: Vec<_> = fs::read_dir(root)
        .ok()?
        .filter_map(std::result::Result::ok)
        .filter_map(|entry| {
            let version: Vec<u32> = entry
                .file_name()
                .to_str()?
                .split('.')
                .map(str::parse)
                .collect::<std::result::Result<_, _>>()
                .ok()?;
            (version.len() == 4).then_some((version, entry.path()))
        })
        .collect();
    versions.sort_by(|left, right| right.0.cmp(&left.0));
    versions
        .into_iter()
        .flat_map(|(_, directory)| architectures.iter().map(move |arch| directory.join(arch).join(name)))
        .find(|path| path.is_file())
}

fn write_json(path: &Path, value: &impl Serialize) -> Result<()> {
    let bytes = serde_json::to_vec_pretty(value)?;
    let mut file = OpenOptions::new().write(true).create_new(true).open(path)?;
    let result = (|| {
        file.write_all(&bytes)?;
        file.write_all(b"\n")?;
        file.sync_all()?;
        Ok(())
    })();
    if result.is_err() {
        drop(file);
        let _ = fs::remove_file(path);
    }
    result
}

fn write_relative(root: &Path, relative: &str, bytes: &[u8]) -> Result<()> {
    let path = safe_join(root, relative)?;
    if path.exists() {
        let old = fs::read(&path)?;
        if old != bytes {
            return Err(PublisherError::Invalid(format!(
                "content-addressed path already contains different bytes: {relative}"
            )));
        }
        return Ok(());
    }
    let parent = path
        .parent()
        .ok_or_else(|| PublisherError::Invalid(format!("invalid output path {relative}")))?;
    fs::create_dir_all(parent)?;
    let mut file = OpenOptions::new().write(true).create_new(true).open(path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    Ok(())
}

fn safe_join(root: &Path, relative: &str) -> Result<PathBuf> {
    let path = Path::new(relative);
    validate_relative_path(path)?;
    let target = root.join(path);
    if target.starts_with(root) {
        reject_reparse_components(&target)?;
        Ok(target)
    } else {
        Err(PublisherError::Invalid(format!("path escapes root: {relative}")))
    }
}

fn validate_relative_path(path: &Path) -> Result<()> {
    if path.as_os_str().is_empty()
        || path
            .components()
            .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(PublisherError::Invalid(format!(
            "unsafe relative path: {}",
            path.display()
        )));
    }
    Ok(())
}

fn safe_component(value: &str) -> Result<String> {
    if value.is_empty()
        || value == "."
        || value == ".."
        || value
            .chars()
            .any(|character| character == '/' || character == '\\' || character == ':' || character.is_control())
    {
        return Err(PublisherError::Invalid(format!("unsafe path component: {value:?}")));
    }
    Ok(value.to_owned())
}

fn validate_package_identifier(id: &str) -> Result<()> {
    safe_component(id)?;
    let mut chars = id.chars();
    if id.len() > 128
        || !chars.next().is_some_and(|ch| ch.is_ascii_alphanumeric())
        || !chars.all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '.' | '-'))
        || id.trim() != id
    {
        return Err(PublisherError::Invalid(format!("PackageIdentifier is invalid: {id:?}")));
    }
    Ok(())
}

fn validate_package_version(version: &str) -> Result<()> {
    let mut chars = version.chars();
    if version.len() > 128
        || !chars.next().is_some_and(|ch| ch.is_ascii_alphanumeric())
        || !chars.all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '.' | '_' | '+' | '-'))
    {
        return Err(PublisherError::Invalid(format!(
            "PackageVersion is invalid: {version:?}"
        )));
    }
    safe_component(version)?;
    Ok(())
}

fn validate_locale(locale: &str) -> Result<()> {
    let mut components = locale.split('-');
    let language = components.next().unwrap_or_default();
    let valid_language = (2..=3).contains(&language.len()) && language.bytes().all(|byte| byte.is_ascii_alphabetic());
    let valid_subtags =
        components.all(|part| (2..=8).contains(&part.len()) && part.bytes().all(|byte| byte.is_ascii_alphanumeric()));
    if !valid_language || !valid_subtags || locale.len() > 35 {
        return Err(PublisherError::Invalid(format!("PackageLocale is invalid: {locale:?}")));
    }
    safe_component(locale)?;
    Ok(())
}

fn validate_yaml_value(value: &Value, depth: usize) -> Result<()> {
    if depth > 100 {
        return Err(PublisherError::Invalid(
            "YAML nesting depth exceeds the 100-level limit".to_owned(),
        ));
    }
    match value {
        Value::Mapping(mapping) => {
            for (key, child) in mapping {
                if key.as_str().is_none_or(str::is_empty) {
                    return Err(PublisherError::Invalid(
                        "manifest mapping keys must be non-empty strings".to_owned(),
                    ));
                }
                validate_yaml_value(child, depth + 1)?;
            }
        }
        Value::Sequence(sequence) => {
            for child in sequence {
                validate_yaml_value(child, depth + 1)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn validate_package_identity(name: &str, publisher: &str) -> Result<()> {
    let segments: Vec<_> = name.split('.').collect();
    if name.len() > 50
        || segments.is_empty()
        || segments
            .iter()
            .any(|segment| segment.is_empty() || !segment.bytes().all(|byte| byte.is_ascii_alphanumeric()))
    {
        return Err(PublisherError::Invalid("invalid MSIX package identity name".to_owned()));
    }
    if publisher.is_empty() || publisher.contains(['<', '>']) || publisher.chars().any(char::is_control) {
        return Err(PublisherError::Invalid("invalid MSIX package publisher".to_owned()));
    }
    Ok(())
}

fn validate_msix_version(version: &str) -> Result<()> {
    let parts: Vec<_> = version.split('.').collect();
    if parts.len() != 4
        || parts.iter().any(|part| {
            part.is_empty() || !part.bytes().all(|byte| byte.is_ascii_digit()) || part.parse::<u16>().is_err()
        })
    {
        return Err(PublisherError::Invalid(format!(
            "MSIX package version must contain four numeric components: {version}"
        )));
    }
    Ok(())
}

fn compare_winget_versions(left: &str, right: &str) -> std::cmp::Ordering {
    let left = ParsedVersion::parse(left);
    let right = ParsedVersion::parse(right);
    if left.latest != right.latest {
        return left.latest.cmp(&right.latest);
    }
    if left.unknown != right.unknown {
        return right.unknown.cmp(&left.unknown);
    }
    for index in 0..left.parts.len().max(right.parts.len()) {
        let part = compare_version_parts(
            left.parts.get(index).unwrap_or(&VersionPart::ZERO),
            right.parts.get(index).unwrap_or(&VersionPart::ZERO),
        );
        if part != std::cmp::Ordering::Equal {
            return part;
        }
    }
    left.approximation.cmp(&right.approximation)
}

#[derive(Debug)]
struct ParsedVersion {
    latest: bool,
    unknown: bool,
    approximation: i32,
    parts: Vec<VersionPart>,
}

impl ParsedVersion {
    fn parse(source: &str) -> Self {
        let mut value = source.trim();
        let mut approximation = 0;
        if let Some(rest) = value.strip_prefix("< ") {
            approximation = -1;
            value = rest;
        } else if let Some(rest) = value.strip_prefix("> ") {
            approximation = 1;
            value = rest;
        }
        let first_digit = value.find(|character: char| character.is_ascii_digit());
        let first_separator = value.find('.');
        if let Some(first_digit) = first_digit
            && first_separator.is_none_or(|first_separator| first_digit < first_separator)
        {
            value = &value[first_digit..];
        }
        let mut parts: Vec<_> = value.split('.').map(VersionPart::parse).collect();
        while parts.last().is_some_and(|part| *part == VersionPart::ZERO) {
            parts.pop();
        }
        let latest = parts.len() == 1 && parts[0].integer == 0 && parts[0].other.eq_ignore_ascii_case("latest");
        let unknown = parts.len() == 1 && parts[0].integer == 0 && parts[0].other.eq_ignore_ascii_case("unknown");
        Self {
            latest,
            unknown,
            approximation,
            parts,
        }
    }
}

#[derive(Debug, Default, PartialEq, Eq)]
struct VersionPart {
    integer: u64,
    other: String,
}

impl VersionPart {
    const ZERO: Self = Self {
        integer: 0,
        other: String::new(),
    };

    fn parse(raw: &str) -> Self {
        let part = raw.trim();
        let digits = part.bytes().take_while(u8::is_ascii_digit).count();
        let Some(integer) = part.get(..digits).and_then(|value| value.parse::<u64>().ok()) else {
            return Self {
                integer: 0,
                other: part.to_lowercase(),
            };
        };
        Self {
            integer,
            other: part[digits..].to_lowercase(),
        }
    }
}

fn compare_version_parts(left: &VersionPart, right: &VersionPart) -> std::cmp::Ordering {
    let integer = left.integer.cmp(&right.integer);
    if integer != std::cmp::Ordering::Equal {
        return integer;
    }
    match (left.other.is_empty(), right.other.is_empty()) {
        (true, true) => std::cmp::Ordering::Equal,
        (true, false) => std::cmp::Ordering::Greater,
        (false, true) => std::cmp::Ordering::Less,
        (false, false) => left.other.encode_utf16().cmp(right.other.encode_utf16()),
    }
}

fn compare_quad_version(left: &str, right: &str) -> Result<std::cmp::Ordering> {
    validate_msix_version(left)?;
    validate_msix_version(right)?;
    let left: Vec<u16> = left
        .split('.')
        .map(|part| part.parse::<u16>())
        .collect::<std::result::Result<_, _>>()
        .map_err(|_| PublisherError::Invalid("invalid package version".to_owned()))?;
    let right: Vec<u16> = right
        .split('.')
        .map(|part| part.parse::<u16>())
        .collect::<std::result::Result<_, _>>()
        .map_err(|_| PublisherError::Invalid("invalid package version".to_owned()))?;
    Ok(left.cmp(&right))
}

fn required_string(mapping: &Mapping, key: &str, path: &Path) -> Result<String> {
    optional_string(mapping, key)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| PublisherError::Invalid(format!("required field {key} is missing in {}", path.display())))
}

fn optional_string(mapping: &Mapping, key: &str) -> Option<String> {
    map_get(mapping, key).and_then(|value| value.as_str().map(str::to_owned))
}

fn map_get<'a>(mapping: &'a Mapping, key: &str) -> Option<&'a Value> {
    mapping.get(Value::String(key.to_owned()))
}

fn as_mapping(value: &Value) -> Result<&Mapping> {
    value
        .as_mapping()
        .ok_or_else(|| PublisherError::Invalid("manifest document must be a mapping".to_owned()))
}

fn replace_once(slot: &mut Option<Value>, value: Value, label: &str) -> Result<()> {
    if slot.replace(value).is_some() {
        return Err(PublisherError::Invalid(format!("duplicate {label}")));
    }
    Ok(())
}

fn merge_yaml_mapping(target: &mut Mapping, source: &Mapping) {
    for (key, value) in source {
        let is_identity = matches!(
            key.as_str(),
            Some("ManifestType" | "ManifestVersion" | "PackageIdentifier" | "PackageVersion")
        );
        if is_identity && target.contains_key(key) {
            continue;
        }
        match (target.get_mut(key), value) {
            (Some(Value::Mapping(existing)), Value::Mapping(incoming)) => {
                merge_yaml_mapping(existing, incoming);
            }
            _ => {
                target.insert(key.clone(), value.clone());
            }
        }
    }
}

fn set_string(mapping: &mut Mapping, key: &str, value: &str) {
    mapping.insert(Value::String(key.to_owned()), Value::String(value.to_owned()));
}

fn json_quote(value: &str) -> Result<String> {
    Ok(serde_json::to_string(value)?)
}

fn extend_strings(target: &mut BTreeSet<String>, mapping: &Mapping, key: &str) {
    match map_get(mapping, key) {
        Some(Value::String(value)) => {
            target.insert(value.clone());
        }
        Some(Value::Sequence(values)) => {
            target.extend(values.iter().filter_map(Value::as_str).map(str::to_owned));
        }
        _ => {}
    }
}

fn canonicalize_value(value: Value) -> Value {
    match value {
        Value::Mapping(mapping) => {
            let mut entries: Vec<_> = mapping.into_iter().collect();
            entries.sort_by(|left, right| left.0.as_str().unwrap_or("").cmp(right.0.as_str().unwrap_or("")));
            let mut sorted = Mapping::new();
            for (key, value) in entries {
                sorted.insert(key, canonicalize_value(value));
            }
            Value::Mapping(sorted)
        }
        Value::Sequence(sequence) => Value::Sequence(sequence.into_iter().map(canonicalize_value).collect()),
        other => other,
    }
}

fn gather_files(root: &Path, path: &Path, output: &mut Vec<PathBuf>) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if is_reparse_point(&metadata) {
        return Err(PublisherError::Invalid(format!(
            "symlink or reparse point is not allowed: {}",
            path.display()
        )));
    }
    if metadata.is_file() {
        output.push(path.to_path_buf());
    } else if metadata.is_dir() {
        let mut entries = fs::read_dir(path)?.collect::<std::io::Result<Vec<_>>>()?;
        entries.sort_by_key(|entry| entry.file_name());
        for entry in entries {
            gather_files(root, &entry.path(), output)?;
        }
    }
    let _ = root;
    Ok(())
}

fn hash_tree(root: &Path) -> Result<BTreeMap<String, String>> {
    let mut files = Vec::new();
    gather_files(root, root, &mut files)?;
    files.sort();
    let mut inventory = BTreeMap::new();
    for path in files {
        let relative = path
            .strip_prefix(root)
            .map_err(|_| PublisherError::Invalid(format!("file escaped inventory root: {}", path.display())))?;
        let key = relative.to_string_lossy().replace('\\', "/");
        inventory.insert(key, sha256_hex(&fs::read(path)?));
    }
    Ok(inventory)
}

fn reject_reparse_components(path: &Path) -> Result<()> {
    let absolute = absolute_normalized(path)?;
    let mut current = PathBuf::new();
    for component in absolute.components() {
        current.push(component.as_os_str());
        // A bare prefix such as `\\?\D:` names the volume device, not a directory.
        if matches!(component, Component::Prefix(_)) {
            continue;
        }
        match fs::symlink_metadata(&current) {
            Ok(metadata) if is_reparse_point(&metadata) => {
                return Err(PublisherError::Invalid(format!(
                    "symbolic links and reparse points are not allowed in publisher paths: {}",
                    current.display()
                )));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => break,
            Err(error) => return Err(error.into()),
        }
    }
    Ok(())
}

fn require_directory(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if is_reparse_point(&metadata) || !metadata.is_dir() {
        return Err(PublisherError::Invalid(format!(
            "expected a regular directory: {}",
            path.display()
        )));
    }
    Ok(())
}

fn require_regular_file(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if is_reparse_point(&metadata) || !metadata.is_file() {
        return Err(PublisherError::Invalid(format!(
            "expected a regular file: {}",
            path.display()
        )));
    }
    Ok(())
}

fn is_reparse_point(metadata: &fs::Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
    }
    #[cfg(not(windows))]
    {
        false
    }
}

fn validate_build_id(build_id: &str) -> Result<()> {
    if !is_lower_hex_hash(build_id) {
        return Err(PublisherError::Invalid(format!(
            "build identifier must be a lowercase SHA-256 digest: {build_id:?}"
        )));
    }
    Ok(())
}

fn is_lower_hex_hash(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn build_identifier(inventory: &Inventory) -> Result<String> {
    let inventory_json = canonical_inventory_json(&inventory.files)?;
    let input = format!("pinget-publisher-state-v{PUBLISHER_FORMAT_VERSION}\n{inventory_json}");
    Ok(sha256_hex(input.as_bytes()))
}

fn canonical_inventory_json(inventory: &BTreeMap<String, String>) -> Result<String> {
    Ok(serde_json::to_string(&CanonicalInventory(inventory))?)
}

fn serialize_inventory<S>(inventory: &BTreeMap<String, String>, serializer: S) -> std::result::Result<S::Ok, S::Error>
where
    S: Serializer,
{
    CanonicalInventory(inventory).serialize(serializer)
}

fn compare_utf16(left: &str, right: &str) -> std::cmp::Ordering {
    left.encode_utf16().cmp(right.encode_utf16())
}

fn absolute_normalized(path: &Path) -> Result<PathBuf> {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut normalized = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            other => normalized.push(other.as_os_str()),
        }
    }
    Ok(normalized)
}

fn canonicalize_future_path(path: &Path) -> Result<PathBuf> {
    reject_reparse_components(path)?;
    let absolute = absolute_normalized(path)?;
    if absolute.exists() {
        return Ok(fs::canonicalize(absolute)?);
    }

    let mut existing = absolute.as_path();
    let mut suffix = Vec::new();
    while !existing.exists() {
        let name = existing
            .file_name()
            .ok_or_else(|| PublisherError::Invalid(format!("no existing parent directory for {}", path.display())))?;
        suffix.push(name.to_os_string());
        existing = existing
            .parent()
            .ok_or_else(|| PublisherError::Invalid(format!("no existing parent directory for {}", path.display())))?;
    }
    let mut resolved = fs::canonicalize(existing)?;
    for component in suffix.into_iter().rev() {
        resolved.push(component);
    }
    Ok(resolved)
}

fn ensure_disjoint_roots(left: &Path, right: &Path) -> Result<()> {
    #[cfg(windows)]
    let overlaps = {
        let left = windows_path_key(left);
        let right = windows_path_key(right);
        left == right || left.starts_with(&format!("{right}\\")) || right.starts_with(&format!("{left}\\"))
    };
    #[cfg(not(windows))]
    let overlaps = left.starts_with(right) || right.starts_with(left);
    if overlaps {
        return Err(PublisherError::Invalid(format!(
            "publisher roots must not overlap: {} and {}",
            left.display(),
            right.display()
        )));
    }
    Ok(())
}

#[cfg(windows)]
fn windows_path_key(path: &Path) -> String {
    let mut value = path.to_string_lossy().replace('/', "\\");
    if let Some(rest) = value.strip_prefix(r"\\?\UNC\") {
        value = format!(r"\\{rest}");
    } else if let Some(rest) = value.strip_prefix(r"\\?\") {
        value = rest.to_owned();
    }
    value.trim_end_matches('\\').to_lowercase()
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn uuid_string(build_id: &str) -> String {
    let digest = Sha256::digest(build_id.as_bytes());
    let mut bytes = [0u8; 16];
    bytes.copy_from_slice(&digest[..16]);
    bytes[6] = (bytes[6] & 0x0f) | 0x50;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    let hex = bytes.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    format!(
        "{}-{}-{}-{}-{}",
        &hex[..8],
        &hex[8..12],
        &hex[12..16],
        &hex[16..20],
        &hex[20..32]
    )
    .to_ascii_uppercase()
}

#[cfg(windows)]
fn win32_last_error() -> u32 {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetLastError() -> u32;
    }

    // SAFETY: GetLastError reads the calling thread's last-error value and has no pointer arguments.
    unsafe { GetLastError() }
}

#[cfg(windows)]
fn compress_mszip(input: &[u8]) -> Result<Vec<u8>> {
    if input.len() > MAX_MSZIP_SIZE {
        return Err(PublisherError::Invalid(
            "MSZIP payload exceeds the 64 MiB limit".to_owned(),
        ));
    }
    use std::ffi::c_void;
    type CompressorHandle = *mut c_void;

    #[link(name = "cabinet")]
    unsafe extern "system" {
        fn CreateCompressor(algorithm: u32, options: *const c_void, handle: *mut CompressorHandle) -> i32;
        fn Compress(
            handle: CompressorHandle,
            input: *const c_void,
            input_size: usize,
            output: *mut c_void,
            output_size: usize,
            bytes_written: *mut usize,
        ) -> i32;
        fn CloseCompressor(handle: CompressorHandle) -> i32;
    }

    const COMPRESS_ALGORITHM_MSZIP: u32 = 2;
    const ERROR_INSUFFICIENT_BUFFER: u32 = 122;
    let mut handle = std::ptr::null_mut();
    // SAFETY: API receives a valid output pointer and null optional options.
    if unsafe { CreateCompressor(COMPRESS_ALGORITHM_MSZIP, std::ptr::null(), &mut handle) } == 0 {
        return Err(PublisherError::Tool(format!(
            "CreateCompressor failed: {}",
            win32_last_error()
        )));
    }
    let result = (|| {
        let mut required = 0usize;
        // SAFETY: query mode uses null output and a valid input slice.
        let success = unsafe {
            Compress(
                handle,
                input.as_ptr().cast(),
                input.len(),
                std::ptr::null_mut(),
                0,
                &mut required,
            )
        };
        if success == 0 && win32_last_error() != ERROR_INSUFFICIENT_BUFFER {
            return Err(PublisherError::Tool(format!(
                "MSZIP size query failed: {}",
                win32_last_error()
            )));
        }
        let mut output = vec![0u8; required];
        let mut written = 0usize;
        // SAFETY: output has the API-reported capacity and both slices remain alive.
        if unsafe {
            Compress(
                handle,
                input.as_ptr().cast(),
                input.len(),
                output.as_mut_ptr().cast(),
                output.len(),
                &mut written,
            )
        } == 0
        {
            return Err(PublisherError::Tool(format!(
                "MSZIP compression failed: {}",
                win32_last_error()
            )));
        }
        output.truncate(written);
        Ok(output)
    })();
    // SAFETY: handle was successfully created and is closed exactly once.
    unsafe {
        CloseCompressor(handle);
    }
    result
}

#[cfg(windows)]
fn decompress_mszip(input: &[u8]) -> Result<Vec<u8>> {
    validate_mszip_size(input)?;
    use std::ffi::c_void;
    type DecompressorHandle = *mut c_void;

    #[link(name = "cabinet")]
    unsafe extern "system" {
        fn CreateDecompressor(algorithm: u32, options: *const c_void, handle: *mut DecompressorHandle) -> i32;
        fn Decompress(
            handle: DecompressorHandle,
            input: *const c_void,
            input_size: usize,
            output: *mut c_void,
            output_size: usize,
            bytes_written: *mut usize,
        ) -> i32;
        fn CloseDecompressor(handle: DecompressorHandle) -> i32;
    }

    const COMPRESS_ALGORITHM_MSZIP: u32 = 2;
    const ERROR_INSUFFICIENT_BUFFER: u32 = 122;
    let mut handle = std::ptr::null_mut();
    // SAFETY: API receives a valid output pointer and null optional options.
    if unsafe { CreateDecompressor(COMPRESS_ALGORITHM_MSZIP, std::ptr::null(), &mut handle) } == 0 {
        return Err(PublisherError::Tool(format!(
            "CreateDecompressor failed: {}",
            win32_last_error()
        )));
    }
    let result = (|| {
        let mut required = 0usize;
        // SAFETY: query mode uses null output and a valid input slice.
        let success = unsafe {
            Decompress(
                handle,
                input.as_ptr().cast(),
                input.len(),
                std::ptr::null_mut(),
                0,
                &mut required,
            )
        };
        if success == 0 && win32_last_error() != ERROR_INSUFFICIENT_BUFFER {
            return Err(PublisherError::Tool(format!(
                "MSZIP size query failed: {}",
                win32_last_error()
            )));
        }
        if required > MAX_MSZIP_SIZE {
            return Err(PublisherError::Invalid(
                "MSZIP decoded payload exceeds the 64 MiB limit".to_owned(),
            ));
        }
        let mut output = vec![0u8; required];
        let mut written = 0usize;
        // SAFETY: output has the API-reported capacity and both slices remain alive.
        if unsafe {
            Decompress(
                handle,
                input.as_ptr().cast(),
                input.len(),
                output.as_mut_ptr().cast(),
                output.len(),
                &mut written,
            )
        } == 0
        {
            return Err(PublisherError::Tool(format!(
                "MSZIP decompression failed: {}",
                win32_last_error()
            )));
        }
        output.truncate(written);
        Ok(output)
    })();
    // SAFETY: handle was successfully created and is closed exactly once.
    unsafe {
        CloseDecompressor(handle);
    }
    result
}

fn validate_mszip_size(input: &[u8]) -> Result<()> {
    let size = input
        .get(8..16)
        .and_then(|bytes| <[u8; 8]>::try_from(bytes).ok())
        .map(u64::from_le_bytes)
        .ok_or_else(|| PublisherError::Invalid("truncated MSZIP header".to_owned()))?;
    if size > MAX_MSZIP_SIZE as u64 {
        return Err(PublisherError::Invalid(
            "MSZIP decoded payload exceeds the 64 MiB limit".to_owned(),
        ));
    }
    Ok(())
}

#[cfg(not(windows))]
fn compress_mszip(_input: &[u8]) -> Result<Vec<u8>> {
    Err(PublisherError::Tool(
        "native WinGet MSZIP compression is currently supported only on Windows".to_owned(),
    ))
}

#[cfg(not(windows))]
fn decompress_mszip(input: &[u8]) -> Result<Vec<u8>> {
    validate_mszip_size(input)?;
    Err(PublisherError::Tool(
        "native WinGet MSZIP decompression is currently supported only on Windows".to_owned(),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_comparer_orders_numeric_release_and_special_versions() {
        assert_eq!(compare_winget_versions("1.10.0", "1.9.0"), std::cmp::Ordering::Greater);
        assert_eq!(compare_winget_versions("1.2.0", "1.2"), std::cmp::Ordering::Equal);
        assert_eq!(compare_winget_versions("1.2-rc1", "1.2"), std::cmp::Ordering::Less);
        assert_eq!(compare_winget_versions("< 1.2", "1.2"), std::cmp::Ordering::Less);
        assert_eq!(compare_winget_versions("> 1.2", "1.2"), std::cmp::Ordering::Greater);
        assert_eq!(compare_winget_versions("unknown", "0.1"), std::cmp::Ordering::Less);
        assert_eq!(compare_winget_versions("latest", "99.0"), std::cmp::Ordering::Greater);
    }

    #[test]
    fn schema_scalars_preserve_raw_spelling_in_block_and_flow_yaml() {
        let hash = "1".repeat(64);
        let input = format!(
            "PackageVersion: 1.0\nUnknown: 2024\nFlag: true\nTags: [1.0, 2024, true]\nInstallers:\n- InstallerSha256: {hash}\n  SignatureSha256: {hash}\n  Details: {{MinimumVersion: 2024, DisplayVersion: 1.0}}\nDescription: |\n  InstallerSha256: 123\n"
        );
        let output = serde::de::DeserializeSeed::deserialize(ManifestValue, serde_yaml::Deserializer::from_str(&input))
            .expect("schema-aware YAML");
        let map = output.as_mapping().expect("mapping");
        assert_eq!(optional_string(map, "PackageVersion").as_deref(), Some("1.0"));
        assert_eq!(map_get(map, "Unknown").and_then(Value::as_u64), Some(2024));
        assert_eq!(map_get(map, "Flag").and_then(Value::as_bool), Some(true));
        assert_eq!(
            map_get(map, "Tags").and_then(Value::as_sequence).expect("tags"),
            &["1.0", "2024", "true"].map(|value| Value::String(value.to_owned()))
        );
        let installer = map_get(map, "Installers")
            .and_then(Value::as_sequence)
            .expect("installers")[0]
            .as_mapping()
            .expect("installer");
        assert_eq!(
            optional_string(installer, "InstallerSha256").as_deref(),
            Some(hash.as_str())
        );
        assert_eq!(
            optional_string(installer, "SignatureSha256").as_deref(),
            Some(hash.as_str())
        );
        let details = map_get(installer, "Details")
            .and_then(Value::as_mapping)
            .expect("details");
        assert_eq!(optional_string(details, "MinimumVersion").as_deref(), Some("2024"));
        assert_eq!(optional_string(details, "DisplayVersion").as_deref(), Some("1.0"));
        assert_eq!(
            optional_string(map, "Description").as_deref(),
            Some("InstallerSha256: 123\n")
        );
    }

    #[test]
    fn catalog_terms_use_ascii_nocase_with_ordinal_spelling() {
        let terms = ["tool", "TOOL", "Tool", "", "  ", "é", "É"]
            .map(str::to_owned)
            .into_iter()
            .collect();
        assert_eq!(canonical_terms(terms), vec!["TOOL", "É", "é"]);
    }

    #[test]
    fn unknown_numeric_scalars_keep_number_types_and_canonical_text() {
        let yaml = "[1.0, 0.5, 1e-5, 1e-6, 1e15, 1e16, 1e20, -0.0, 18446744073709551615]";
        let value = serde::de::DeserializeSeed::deserialize(ManifestValue, serde_yaml::Deserializer::from_str(yaml))
            .expect("numbers");
        let json = serde_json::to_string(&CanonicalYaml(&value)).expect("canonical numbers");
        assert_eq!(
            json,
            "[1.0,0.5,0.00001,1e-6,1000000000000000.0,1e+16,1e+20,-0.0,18446744073709551615]"
        );
    }

    #[test]
    fn mszip_declared_size_limit_is_checked_before_allocation() {
        let mut header = [0u8; 24];
        header[8..16].copy_from_slice(&(MAX_MSZIP_SIZE as u64).to_le_bytes());
        assert!(validate_mszip_size(&header).is_ok(), "64 MiB boundary is supported");
        header[8..16].copy_from_slice(&((MAX_MSZIP_SIZE as u64) + 1).to_le_bytes());
        assert!(
            validate_mszip_size(&header).is_err(),
            "over limit must fail without decompression"
        );
        assert!(
            validate_mszip_size(&header[..12]).is_err(),
            "truncated header must fail"
        );
    }

    fn unit_directory(label: &str) -> PathBuf {
        let name = format!(
            ".publisher-unit-{label}-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
        );
        let directory = std::env::current_dir().expect("cwd").join(name);
        fs::create_dir(&directory).expect("create unit directory");
        directory
    }

    #[test]
    fn future_or_unrelated_state_is_never_initialized_or_mutated() {
        for future in [false, true] {
            let directory = unit_directory("state");
            let path = directory.join("publisher.db");
            let connection = Connection::open(&path).expect("create database");
            connection
                .execute_batch("CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('keep');")
                .expect("schema");
            if future {
                connection
                    .execute_batch("PRAGMA user_version=42")
                    .expect("future version");
            }

            drop(connection);
            let original = fs::read(&path).expect("database");
            assert!(open_state(&directory, true).is_err(), "foreign state must be rejected");
            assert_eq!(
                fs::read(&path).expect("database after"),
                original,
                "database bytes must remain unchanged"
            );
            fs::remove_dir_all(directory).expect("cleanup");
        }
    }

    #[test]
    fn legacy_artifact_format_requires_fresh_state_without_migration() {
        let root = unit_directory("legacy");
        let connection = open_state(&root, true).expect("current state");
        connection
            .execute("DELETE FROM metadata WHERE name='artifactFormatVersion'", [])
            .expect("legacy metadata");
        drop(connection);
        let database = fs::read(root.join("publisher.db")).expect("legacy state");
        fs::write(root.join("old-snapshot"), b"preserve").expect("prior artifact");
        let error = open_state(&root, true).expect_err("legacy format must be refused");
        assert!(
            error.to_string().contains("new --state"),
            "actionable fresh-state guidance"
        );
        assert_eq!(
            fs::read(root.join("publisher.db")).expect("unchanged database"),
            database
        );
        assert_eq!(
            fs::read(root.join("old-snapshot")).expect("prior artifact"),
            b"preserve"
        );
        let inventory = Inventory {
            files: BTreeMap::new(),
            manifests: Vec::new(),
        };
        assert_eq!(
            build_identifier(&inventory).expect("identifier"),
            sha256_hex(b"pinget-publisher-state-v2\n{}")
        );
        assert_ne!(
            build_identifier(&inventory).expect("identifier"),
            sha256_hex(b"pinget-publisher-state-v1\n{}")
        );
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn owned_staging_recovery_leaves_sibling_artifacts_untouched() {
        let root = unit_directory("staging");
        let staging = root.join(format!(".staging-{}", "1".repeat(64)));
        fs::create_dir_all(staging.join("feed")).expect("stale staging");
        fs::write(staging.join("feed").join("partial"), b"partial").expect("partial artifact");
        fs::write(root.join("keep"), b"immutable").expect("sibling");
        remove_owned_staging(&staging).expect("recover owned staging");
        assert!(!staging.exists());
        assert_eq!(fs::read(root.join("keep")).expect("sibling"), b"immutable");
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn failed_publication_removes_only_new_files_and_empty_directories() {
        let root = unit_directory("rollback");
        let existing = root.join("existing");
        fs::create_dir(&existing).expect("existing directory");
        fs::write(existing.join("keep"), b"old").expect("existing artifact");
        {
            let mut rollback = PublicationRollback::default();
            let directory = root.join("new").join("nested");
            rollback
                .create_directories(&root, &directory)
                .expect("created directories");
            let path = directory.join("artifact");
            rollback.files.push(path.clone());
            fs::write(path, b"new").expect("new artifact");
        }
        assert!(!root.join("new").exists(), "new directory tree must be removed");
        assert_eq!(fs::read(existing.join("keep")).expect("existing artifact"), b"old");
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[cfg(windows)]
    #[test]
    fn initial_publish_post_copy_failure_cleans_destination_and_allows_retry() {
        let makeappx = doctor().makeappx_path.expect("Windows SDK MakeAppx is required");
        let root = unit_directory("initial-publish");
        let input = root.join("input");
        let state = root.join("state");
        let destination = root.join("destination");
        fs::create_dir(&input).expect("manifest input");
        fs::write(
            input.join("package.yaml"),
            concat!(
                "PackageIdentifier: Pinget.Test.InitialRollback\n",
                "PackageVersion: 1.0.0\nPackageLocale: en-US\n",
                "Publisher: Pinget Test\nPackageName: Initial Rollback\n",
                "License: MIT\nShortDescription: Initial publication rollback regression\n",
                "Installers:\n  - Architecture: x64\n    InstallerType: exe\n",
                "    InstallerUrl: https://example.invalid/unused.exe\n",
                "    InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111\n",
                "ManifestType: singleton\nManifestVersion: 1.9.0\n",
            ),
        )
        .expect("manifest");
        let build_report = build(&BuildOptions {
            input,
            state: state.clone(),
        })
        .expect("build snapshot");
        let metadata: SnapshotMetadata = serde_json::from_slice(
            &fs::read(build_report.snapshot_directory.join("snapshot.json")).expect("snapshot metadata"),
        )
        .expect("snapshot");
        assert!(
            metadata.artifacts.len() >= 2,
            "manifest and version sidecar must be copied"
        );
        let options = PublishOptions {
            state: state.clone(),
            destination: destination.clone(),
            build_id: Some(build_report.build_id.clone()),
            name: "Pinget.Test.InitialRollback".to_owned(),
            publisher: "CN=Pinget Initial Rollback Test".to_owned(),
            version: "1.0.0.0".to_owned(),
            signing: Signing::UnsignedDevelopment,
            makeappx: Some(makeappx),
            signtool: None,
            timestamp_url: None,
        };
        let mut reached_after_copy = false;
        let error = publish_after_copy(&options, |feed| {
            reached_after_copy = true;
            for (relative, expected) in &metadata.artifacts {
                let path = safe_join(feed, relative).expect("copied artifact path");
                assert!(
                    path.is_file(),
                    "artifact must exist before injected failure: {relative}"
                );
                assert_eq!(sha256_hex(&fs::read(path).expect("copied artifact")), *expected);
            }
            assert!(
                !feed.join(".pinget-publisher.json").exists(),
                "no ownership marker before promotion"
            );
            assert!(!feed.join("source2.msix").exists(), "no root package before promotion");
            Err(PublisherError::Invalid("injected initial post-copy failure".to_owned()))
        })
        .expect_err("injected post-copy failure");
        assert!(reached_after_copy, "failure must occur after actual verified copies");
        assert!(error.to_string().contains("injected initial post-copy failure"));
        assert_eq!(
            fs::read_dir(&destination).expect("destination").count(),
            0,
            "initial failure must leave no files, directories, marker, package or staging"
        );
        let failed_status = status(&state).expect("state after failure");
        assert_eq!(failed_status.last_published_build_id, None);
        assert!(!failed_status.last_build_published);
        verify(&state, Some(&build_report.build_id), None).expect("snapshot preserved after failure");

        let published = publish(&options).expect("normal initial retry");
        assert!(published.valid);
        assert!(
            !published.reused,
            "retry must perform publication rather than reuse a false commit"
        );
        assert_eq!(published.build_id, build_report.build_id);
        let verified = verify_published_destination(&destination, None).expect("destination verification");
        assert!(verified.valid);
        assert_eq!(verified.build_id.as_deref(), Some(build_report.build_id.as_str()));
        assert_eq!(
            verified.artifact_count,
            metadata.artifacts.len() + 1,
            "feed artifacts plus root package"
        );
        let published_status = status(&state).expect("published state");
        assert_eq!(
            published_status.last_published_build_id.as_deref(),
            Some(build_report.build_id.as_str())
        );
        assert!(published_status.last_build_published);
        verify(&state, Some(&build_report.build_id), None).expect("snapshot preserved after retry");
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn destination_lock_serializes_independent_publisher_states() {
        let root = unit_directory("lock");
        let destination = root.join("feed");
        let first = acquire_destination_lock(&destination).expect("first lock");
        assert!(
            acquire_destination_lock(&destination).is_err(),
            "same destination must be exclusive"
        );
        assert!(!destination.exists(), "locking must not create a feed");
        drop(first);
        drop(acquire_destination_lock(&destination).expect("released lock"));
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn sdk_discovery_prefers_numeric_newest_version_and_host_architecture() {
        let root = unit_directory("sdk");
        for (version, architecture) in [("10.0.9.0", "x64"), ("10.0.10.0", "x86"), ("10.0.10.0", "x64")] {
            let directory = root.join(version).join(architecture);
            fs::create_dir_all(&directory).expect("SDK directory");
            fs::write(directory.join("makeappx.exe"), b"fixture").expect("SDK tool");
        }
        assert_eq!(
            find_sdk_tool(&root, "makeappx.exe", &["x64", "x86"]),
            Some(root.join("10.0.10.0").join("x64").join("makeappx.exe"))
        );
        fs::remove_file(root.join("10.0.10.0").join("x64").join("makeappx.exe")).expect("remove native tool");
        assert_eq!(
            find_sdk_tool(&root, "makeappx.exe", &["x64", "x86"]),
            Some(root.join("10.0.10.0").join("x86").join("makeappx.exe"))
        );
        assert!(find_sdk_tool(&root, "signtool.exe", &["x64"]).is_none());
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn recorded_snapshot_requires_matching_input_inventory_and_summary() {
        let connection = Connection::open_in_memory().expect("state");
        connection.execute_batch(STATE_SCHEMA).expect("schema");
        let mut snapshot = SnapshotMetadata {
            schema_version: 1,
            artifact_format_version: PUBLISHER_FORMAT_VERSION,
            build_id: "1".repeat(64),
            created_utc: "now".to_owned(),
            manifest_count: 1,
            package_count: 1,
            index_hash: "2".repeat(64),
            inventory: [("input.yaml".to_owned(), "3".repeat(64))].into_iter().collect(),
            artifacts: BTreeMap::new(),
        };
        assert!(
            validate_recorded_snapshot(&connection, &snapshot).is_err(),
            "orphan snapshot rejected"
        );
        connection
            .execute(
                "INSERT INTO builds(id,inventory,summary) VALUES(?1,?2,?3)",
                params![
                    snapshot.build_id,
                    canonical_inventory_json(&snapshot.inventory).expect("inventory"),
                    r#"{"createdUtc":"now","manifestCount":1,"packageCount":1}"#
                ],
            )
            .expect("record");
        validate_recorded_snapshot(&connection, &snapshot).expect("matching snapshot");
        snapshot.inventory.insert("other.yaml".to_owned(), "4".repeat(64));
        assert!(
            validate_recorded_snapshot(&connection, &snapshot).is_err(),
            "different inventory rejected"
        );
        snapshot.inventory.remove("other.yaml");
        snapshot.package_count = 2;
        assert!(
            validate_recorded_snapshot(&connection, &snapshot).is_err(),
            "different summary rejected"
        );
    }

    #[test]
    fn shared_tag_rows_use_nocase_and_minimum_spelling_across_packages() {
        let connection = Connection::open_in_memory().expect("index");
        connection.execute_batch(INDEX_SCHEMA).expect("schema");
        insert_terms(&connection, "tags2", "tag", "tags2_map", "tag", 1, &["tool".to_owned()]).expect("first tag");
        insert_terms(&connection, "tags2", "tag", "tags2_map", "tag", 2, &["TOOL".to_owned()]).expect("second tag");
        let tags: Vec<String> = connection
            .prepare("SELECT tag FROM tags2")
            .expect("query")
            .query_map([], |row| row.get(0))
            .expect("rows")
            .collect::<std::result::Result<_, _>>()
            .expect("tags");
        assert_eq!(tags, vec!["TOOL"]);
        let count: i64 = connection
            .query_row("SELECT COUNT(*) FROM tags2_map", [], |row| row.get(0))
            .expect("maps");
        assert_eq!(count, 2, "both packages retain the same shared row");
    }
}
