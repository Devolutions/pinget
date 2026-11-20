//! Offline, single-target Homebrew formula import. No dependency resolution or downloads.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Component, Path, PathBuf};

use serde::Serialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use crate::{PublisherError, Result, sha256_hex};

/// Explicit local inputs for the bounded importer.
pub struct ImportOptions {
    pub formula_json: PathBuf,
    pub bottle_archive: PathBuf,
    pub manifest_output: PathBuf,
    pub bottle_mirror: PathBuf,
    pub tag: String,
    pub target_os: String,
    pub architecture: String,
    pub base_url: String,
}

/// Deterministic audit record; contains no local paths or upstream URLs.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportReport {
    pub schema_version: u32,
    pub package_identifier: String,
    pub package_version: String,
    pub formula_json_sha256: String,
    pub bottle_sha256: String,
    pub bottle_tag: String,
    pub bottle_file: String,
    pub manifest_file: String,
    pub dependency_policy: String,
}

fn invalid(message: &str) -> PublisherError {
    PublisherError::Invalid(message.to_owned())
}

fn text<'a>(value: &'a Value, field: &str) -> Result<&'a str> {
    value[field]
        .as_str()
        .filter(|s| !s.is_empty() && s.len() <= 256 && !s.chars().any(char::is_control))
        .ok_or_else(|| invalid(&format!("formula JSON requires a nonempty string: {field}")))
}

fn optional_text<'a>(value: &'a Value, field: &str, max_len: usize) -> Result<Option<&'a str>> {
    match value.get(field) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(text)) if text.is_empty() => Ok(None),
        Some(Value::String(text)) if text.len() <= max_len && !text.chars().any(char::is_control) => Ok(Some(text)),
        _ => Err(invalid(&format!("formula JSON field must be a string of at most {max_len} bytes: {field}"))),
    }
}

fn integer(value: &Value, field: &str) -> Result<u32> {
    value[field]
        .as_u64()
        .and_then(|n| u32::try_from(n).ok())
        .ok_or_else(|| invalid(&format!("formula JSON requires an unsigned 32-bit integer: {field}")))
}

fn token(value: &str, extra: &[u8]) -> bool {
    !value.is_empty()
        && value.bytes().any(|b| b.is_ascii_alphanumeric())
        && value.bytes().all(|b| b.is_ascii_alphanumeric() || extra.contains(&b))
}

// Deliberately excludes userinfo, escapes, query strings and fragments, including signed URLs.
fn public_url(value: &str) -> bool {
    let Some(rest) = value.strip_prefix("https://") else {
        return false;
    };
    let (authority, path) = rest.split_once('/').unwrap_or((rest, ""));
    let mut parts = authority.split(':');
    let host = parts.next().unwrap_or("");
    let port = parts.next();
    !host.is_empty()
        && host
            .split('.')
            .all(|label| token(label, b"-") && !label.starts_with('-') && !label.ends_with('-'))
        && port.is_none_or(|p| !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()) && p.parse::<u16>().is_ok_and(|n| n != 0))
        && parts.next().is_none()
        && path.bytes().all(|b| b.is_ascii_alphanumeric() || b"-._~/".contains(&b))
        && !path.split('/').any(|p| matches!(p, "." | ".."))
}

// Reject symlinks in every existing component instead of trusting lexical containment.
fn safe_path(path: &Path) -> Result<PathBuf> {
    let absolute = std::path::absolute(path)?;
    let mut resolved = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::ParentDir => return Err(invalid("input/output paths must not contain '..'")),
            Component::CurDir => {}
            _ => resolved.push(component),
        }
        match fs::symlink_metadata(&resolved) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(invalid("input/output paths must not contain symbolic links"));
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
    }
    Ok(resolved)
}

fn hash_reader(reader: &mut File) -> Result<String> {
    let mut digest = Sha256::new();
    let mut buffer = [0; 65536];
    loop {
        let count = reader.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(digest.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

fn check_existing(path: &Path, expected_hash: &str) -> Result<bool> {
    safe_path(path)?;
    match File::open(path) {
        Ok(mut file) if file.metadata()?.is_file() => {
            if hash_reader(&mut file)? != expected_hash {
                return Err(invalid(
                    "destination already exists with different bytes; use a clean output directory",
                ));
            }
            Ok(true)
        }
        Ok(_) => Err(invalid("destination must be a regular file")),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(error) => Err(error.into()),
    }
}

fn write_new(path: &Path, bytes: &[u8]) -> Result<()> {
    let mut file = OpenOptions::new().write(true).create_new(true).open(path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    Ok(())
}

/// Import one dependency-free `arm64_sonoma` bottle from pinned local files.
pub fn import(options: &ImportOptions) -> Result<ImportReport> {
    if options.tag != "arm64_sonoma" || options.target_os != "macOS" || options.architecture != "arm64" {
        return Err(invalid(
            "unsupported target: this offline slice requires --tag arm64_sonoma --target-os macOS --architecture arm64",
        ));
    }
    if !public_url(&options.base_url) {
        return Err(invalid(
            "base URL must be a public HTTPS URL without credentials, escapes, query, fragment, or dot segments",
        ));
    }
    let formula_path = safe_path(&options.formula_json)?;
    let archive_path = safe_path(&options.bottle_archive)?;
    let output = safe_path(&options.manifest_output)?;
    let mirror = safe_path(&options.bottle_mirror)?;
    if output.starts_with(&mirror)
        || mirror.starts_with(&output)
        || [&formula_path, &archive_path]
            .iter()
            .any(|p| p.starts_with(&output) || p.starts_with(&mirror))
    {
        return Err(invalid(
            "formula JSON, bottle input, manifest output and bottle mirror must be disjoint; output directories must not overlap",
        ));
    }
    let mut formula_file = File::open(&formula_path)?;
    if !formula_file.metadata()?.is_file() || formula_file.metadata()?.len() > 8 * 1024 * 1024 {
        return Err(invalid("formula JSON must be a regular file no larger than 8 MiB"));
    }
    let mut bytes = Vec::new();
    (&mut formula_file).take(8 * 1024 * 1024 + 1).read_to_end(&mut bytes)?;
    if bytes.len() > 8 * 1024 * 1024 {
        return Err(invalid("formula JSON exceeds the 8 MiB limit"));
    }
    let formula: Value = serde_json::from_slice(&bytes)
        .map_err(|_| invalid("invalid formula JSON; provide one pinned formula object, not an API list or cask"))?;
    let name = text(&formula, "name")?;
    let tap = text(&formula, "tap")?;
    if !token(name, b"@+._-") || tap.split('/').count() != 2 || !tap.split('/').all(|p| token(p, b"._-")) {
        return Err(invalid("invalid formula name or qualified owner/tap identity"));
    }
    if formula.get("token").is_some() {
        return Err(invalid("casks are unsupported; provide a formula JSON object"));
    }
    let full_name = text(&formula, "full_name")?;
    let qualified = format!("{tap}/{name}");
    if full_name != qualified && !(tap == "homebrew/core" && full_name == name) {
        return Err(invalid("formula full_name does not match name and tap"));
    }
    for field in [
        "dependencies",
        "recommended_dependencies",
        "optional_dependencies",
        "uses_from_macos",
        "requirements",
        "conflicts_with",
    ] {
        match formula.get(field) {
            Some(Value::Array(values)) if values.is_empty() => {}
            None if field != "dependencies" => {}
            _ => {
                return Err(invalid(&format!(
                    "unsupported {field}: dependency closure and requirements are not implemented; provide a dependency-free formula with empty arrays"
                )));
            }
        }
    }
    if formula
        .get("variations")
        .is_some_and(|v| v.as_object().is_none_or(|m| !m.is_empty()))
    {
        return Err(invalid(
            "target-specific formula variations are unsupported; provide a pinned formula without variations",
        ));
    }
    for field in ["disabled", "deprecated"] {
        if formula.get(field).is_some_and(|v| v != false) {
            return Err(invalid("disabled or deprecated formulas are unsupported"));
        }
    }
    let upstream = text(&formula["versions"], "stable")?;
    if upstream.len() > 128 || !token(upstream, b".+_-") || upstream.starts_with('.') {
        return Err(invalid("stable version must be a safe nonempty version token"));
    }
    let revision = integer(&formula, "revision")?;
    let version_scheme = integer(&formula, "version_scheme")?;
    if version_scheme > 5 {
        return Err(invalid("unsupported formula version_scheme; v1 supports 0 through 5"));
    }
    let keg_only = formula["keg_only"]
        .as_bool()
        .ok_or_else(|| invalid("formula keg_only must be boolean"))?;
    let mut lists = Vec::new();
    for field in ["aliases", "oldnames"] {
        let values = formula[field]
            .as_array()
            .ok_or_else(|| invalid(&format!("formula {field} must be an array")))?;
        if values.iter().any(|v| v.as_str().is_none_or(|s| !token(s, b"@+._-"))) {
            return Err(invalid("formula aliases and oldnames must contain formula names"));
        }
        let mut values: Vec<&str> = values.iter().filter_map(Value::as_str).collect();
        values.sort_unstable();
        values.dedup();
        lists.push(values);
    }
    let bottle = &formula["bottle"]["stable"];
    let rebuild = integer(bottle, "rebuild")?;
    let selected = &bottle["files"][&options.tag];
    if !selected.is_object() {
        return Err(invalid("pinned formula has no arm64_sonoma bottle; provide JSON containing the selected target"));
    }
    let sha256 = text(selected, "sha256")?.to_ascii_lowercase();
    if sha256.len() != 64 || !sha256.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(invalid("selected bottle sha256 must have 64 hexadecimal digits"));
    }
    if !public_url(text(selected, "url")?) {
        return Err(invalid(
            "selected bottle URL must be public HTTPS without credentials or query/fragment",
        ));
    }
    let cellar = text(selected, "cellar")?;
    if !matches!(cellar, "any" | "any_skip_relocation") {
        return Err(invalid(
            "fixed-cellar bottles are unsupported; select any or any_skip_relocation",
        ));
    }
    let description = optional_text(&formula, "desc", 4096)?;
    let homepage = optional_text(&formula, "homepage", 2048)?;
    if homepage.is_some_and(|url| !public_url(url)) {
        return Err(invalid("formula homepage must be public HTTPS without credentials, escapes, query, fragment, or dot segments"));
    }
    let license = optional_text(&formula, "license", 256)?;
    if license.is_some_and(|license| {
        !license
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b" .-+()".contains(&b))
    }) {
        return Err(invalid("formula license must be an SPDX expression"));
    }
    let package_identifier = pinget_core::homebrew_package_identifier(tap, name)
        .map_err(|_| invalid("invalid Homebrew formula/tap identity"))?;
    let package_version = if revision == 0 {
        upstream.to_owned()
    } else {
        format!("{upstream}_{revision}")
    };
    let mut archive = File::open(archive_path)?;
    if !archive.metadata()?.is_file() || archive.metadata()?.len() == 0 {
        return Err(invalid("bottle archive must be a nonempty regular file"));
    }
    if hash_reader(&mut archive)? != sha256 {
        return Err(invalid(
            "bottle SHA-256 mismatch; provide the exact archive selected by the pinned formula",
        ));
    }
    let bottle_file = format!("{sha256}.tar.gz");
    let url = format!("{}/{bottle_file}", options.base_url.trim_end_matches('/'));
    let manifest_file = format!("{package_identifier}.{package_version}.yaml");
    let mut manifest = json!({
        "PackageIdentifier": package_identifier, "PackageVersion": package_version,
        "PackageName": name, "Publisher": "Unknown",
        "PackageLocale": "en-US", "ManifestType": "singleton", "ManifestVersion": "1.10.0",
        "Installers": [{"Architecture": "arm64", "Platform": ["macOS"], "InstallerType": "homebrew-bottle",
            "InstallerUrl": url, "InstallerSha256": sha256}],
        "Pinget": {"Homebrew": {"SchemaVersion": 1, "Formula": name, "Tap": tap,
            "UpstreamVersion": upstream, "Revision": revision, "VersionScheme": version_scheme,
            "KegOnly": keg_only, "Aliases": lists[0], "OldNames": lists[1],
            "Bottles": [{"Url": url, "Sha256": sha256, "TargetOS": "macOS", "Architecture": "arm64",
                "BottleTag": options.tag, "Rebuild": rebuild, "Relocation": cellar, "MinimumOSVersion": "14.0"}]}}
    });
    if let Some(description) = description {
        manifest["ShortDescription"] = json!(description);
    }
    if let Some(homepage) = homepage {
        manifest["PackageUrl"] = json!(homepage);
    }
    if let Some(license) = license {
        manifest["License"] = json!(license);
    }
    let manifest_bytes = serde_yaml::to_string(&manifest)?.into_bytes();
    let report = ImportReport {
        schema_version: 1,
        package_identifier,
        package_version,
        formula_json_sha256: sha256_hex(&bytes),
        bottle_sha256: sha256,
        bottle_tag: options.tag.clone(),
        bottle_file,
        manifest_file,
        dependency_policy: "rejected-runtime-dependencies".to_owned(),
    };
    let mut report_bytes = serde_json::to_vec_pretty(&report)?;
    report_bytes.push(b'\n');
    let manifest_path = output.join(&report.manifest_file);
    let report_path = output.join(format!("{}.import.json", report.manifest_file));
    let bottle_path = mirror.join(&report.bottle_file);
    let have_bottle = check_existing(&bottle_path, &report.bottle_sha256)?;
    let have_manifest = check_existing(&manifest_path, &sha256_hex(&manifest_bytes))?;
    let have_report = check_existing(&report_path, &sha256_hex(&report_bytes))?;
    fs::create_dir_all(&output)?;
    fs::create_dir_all(&mirror)?;
    if !have_bottle {
        archive.seek(SeekFrom::Start(0))?;
        let mut destination = OpenOptions::new().write(true).create_new(true).open(&bottle_path)?;
        std::io::copy(&mut archive, &mut destination)?;
        destination.sync_all()?;
        drop(destination);
        let copied_matches = {
            let mut copied = File::open(&bottle_path)?;
            hash_reader(&mut copied)? == report.bottle_sha256
        };
        if !copied_matches {
            fs::remove_file(&bottle_path)?;
            return Err(invalid("bottle changed during mirroring; no manifest was written"));
        }
    }
    if !have_manifest {
        write_new(&manifest_path, &manifest_bytes)?;
    }
    if !have_report {
        write_new(&report_path, &report_bytes)?;
    }
    Ok(report)
}
