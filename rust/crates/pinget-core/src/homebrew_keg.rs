use std::collections::BTreeMap;
use std::fs;
use std::io::Write as _;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use anyhow::{Context, Result, anyhow, bail};
use serde::{Deserialize, Serialize};

use crate::homebrew_bottle;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct KegSpec {
    pub(crate) package_identifier: String,
    pub(crate) package_version: String,
    pub(crate) name: String,
    pub(crate) formula: String,
    pub(crate) keg_version: String,
    pub(crate) original_prefix: String,
    pub(crate) keg_only: bool,
    pub(crate) source_identifier: String,
    pub(crate) source_name: String,
    pub(crate) dependencies: Vec<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub(crate) struct Receipt {
    schema_version: u32,
    pub(crate) package: KegSpec,
    links: BTreeMap<String, String>,
}

pub(crate) fn prefix(app_root: &Path) -> PathBuf {
    app_root.join("homebrew")
}

pub(crate) fn installer_matches_host(installer: &crate::Installer) -> bool {
    if installer.installer_type.as_deref() != Some("homebrew-bottle") {
        return true;
    }
    let platform = if cfg!(target_os = "linux") {
        "Linux"
    } else if cfg!(target_os = "macos") {
        "macOS"
    } else {
        return false;
    };
    installer.architecture.as_deref() == Some(crate::current_architecture())
        && installer.platforms.iter().any(|value| value == platform)
}

pub(crate) fn spec(
    manifest: &crate::Manifest,
    installer: &crate::Installer,
    source_identifier: String,
    source_name: String,
) -> Result<KegSpec> {
    if !installer_matches_host(installer) {
        bail!("Homebrew bottle does not match the native platform and architecture");
    }
    let formula = manifest
        .homebrew
        .as_ref()
        .ok_or_else(|| anyhow!("Missing Homebrew metadata"))?;
    let bottle = formula
        .bottles
        .iter()
        .find(|bottle| {
            installer.url.as_deref() == Some(bottle.url.as_str())
                && installer
                    .sha256
                    .as_deref()
                    .is_some_and(|hash| bottle.sha256.eq_ignore_ascii_case(hash))
        })
        .ok_or_else(|| anyhow!("Selected installer has no matching Homebrew bottle metadata"))?;
    if !matches!(bottle.relocation.as_str(), "any" | "any_skip_relocation") || formula.version_scheme != 0 {
        bail!("Native bottles currently require relocatable archives and version scheme 0");
    }
    let original_prefix = match (bottle.target_os.as_str(), bottle.architecture.as_str()) {
        ("Linux", _) => {
            if bottle.loader.as_deref() != Some("glibc") {
                bail!("Native Linux bottles require an explicit glibc loader policy");
            }
            let actual = homebrew_bottle::run("getconf", &["GNU_LIBC_VERSION"])?;
            let actual = actual
                .strip_prefix("glibc ")
                .ok_or_else(|| anyhow!("Host is not glibc Linux"))?;
            let minimum = bottle
                .minimum_glibc
                .as_deref()
                .ok_or_else(|| anyhow!("Missing MinimumGlibc"))?;
            check_version(actual.trim(), minimum, false)?;
            "/home/linuxbrew/.linuxbrew"
        }
        ("macOS", architecture) => {
            let actual = homebrew_bottle::run("/usr/bin/sw_vers", &["-productVersion"])?;
            let minimum = bottle
                .minimum_os_version
                .as_deref()
                .ok_or_else(|| anyhow!("Missing MinimumOSVersion"))?;
            check_version(actual.trim(), minimum, false)?;
            if let Some(maximum) = &bottle.maximum_os_version {
                check_version(actual.trim(), maximum, true)?;
            }
            if architecture == "arm64" {
                "/opt/homebrew"
            } else {
                "/usr/local"
            }
        }
        _ => bail!("Unsupported native bottle platform"),
    };
    let keg_version = if formula.revision == 0 {
        formula.upstream_version.clone()
    } else {
        format!("{}_{}", formula.upstream_version, formula.revision)
    };
    let spec = KegSpec {
        package_identifier: manifest.id.clone(),
        package_version: manifest.version.clone(),
        name: manifest.name.clone(),
        formula: formula.formula.clone(),
        keg_version,
        original_prefix: original_prefix.to_owned(),
        keg_only: formula.keg_only,
        source_identifier,
        source_name,
        dependencies: crate::selected_installer_dependencies(manifest, installer)
            .into_iter()
            .map(|dependency| dependency.package_identifier)
            .collect(),
    };
    validate(&spec)?;
    Ok(spec)
}

fn check_version(actual: &str, boundary: &str, maximum: bool) -> Result<()> {
    let actual_parts =
        crate::parse_dotted_version(actual).ok_or_else(|| anyhow!("Invalid host ABI version: {actual}"))?;
    let boundary_parts =
        crate::parse_dotted_version(boundary).ok_or_else(|| anyhow!("Invalid bottle ABI version: {boundary}"))?;
    let comparison = crate::compare_dotted_versions(&actual_parts, &boundary_parts);
    if (!maximum && comparison.is_lt()) || (maximum && comparison.is_gt()) {
        bail!(
            "Host version {actual} does not satisfy bottle {} version {boundary}",
            if maximum { "maximum" } else { "minimum" }
        );
    }
    Ok(())
}

pub(crate) fn installed_packages(app_root: &Path, scope: Option<&str>) -> Result<Vec<crate::InstalledPackage>> {
    if scope.is_some_and(|value| value != "user") {
        return Ok(Vec::new());
    }
    read_all(app_root)?
        .into_iter()
        .map(|receipt| {
            let package = receipt.package;
            Ok(crate::InstalledPackage {
                name: package.name,
                local_id: package.package_identifier.clone(),
                installed_version: package.package_version,
                publisher: None,
                scope: Some("user".to_owned()),
                installer_category: Some("homebrew-bottle".to_owned()),
                install_location: Some(
                    prefix(app_root)
                        .join("Cellar")
                        .join(package.formula)
                        .join(package.keg_version)
                        .to_string_lossy()
                        .into_owned(),
                ),
                winget_package_identifier: Some(package.package_identifier),
                winget_source_identifier: Some(package.source_identifier),
                package_family_names: Vec::new(),
                product_codes: Vec::new(),
                upgrade_codes: Vec::new(),
                correlated: None,
                installed_version_canonical: true,
                correlated_requires_explicit_upgrade: false,
                correlated_lacks_compatible_installer: false,
            })
        })
        .collect()
}

pub(crate) fn read_all(app_root: &Path) -> Result<Vec<Receipt>> {
    let root = prefix(app_root);
    if link_target(&root)?.is_some() {
        bail!("Homebrew storage must not be a symbolic link: {}", root.display());
    }
    if !root.try_exists()? {
        return Ok(Vec::new());
    }
    check_directory(&root)?;
    let directory = root.join("receipts");
    if link_target(&directory)?.is_some() {
        bail!("Homebrew receipts must not be a symbolic link: {}", directory.display());
    }
    if !directory.try_exists()? {
        return Ok(Vec::new());
    }
    check_directory(&directory)?;
    let mut paths = fs::read_dir(&directory)?
        .map(|entry| entry.map(|entry| entry.path()))
        .collect::<std::io::Result<Vec<_>>>()?;
    paths.sort();
    let mut receipts = Vec::new();
    for path in paths {
        if path.extension().is_none_or(|value| value != "json") {
            continue;
        }
        if fs::symlink_metadata(&path)?.file_type().is_symlink() {
            bail!("Homebrew receipt must not be a symbolic link: {}", path.display());
        }
        let receipt: Receipt = serde_json::from_slice(&fs::read(&path)?)?;
        validate(&receipt.package)?;
        if receipt.schema_version != 1
            || path.file_name().and_then(|name| name.to_str()) != Some(&format!("{}.json", receipt.package.formula))
            || receipt.links.keys().any(|name| !is_command_link(name))
        {
            bail!("Unsupported or invalid Homebrew receipt: {}", path.display());
        }
        receipts.push(receipt);
    }
    Ok(receipts)
}

pub(crate) fn install(app_root: &Path, archive: &Path, package: KegSpec, warnings: &mut Vec<String>) -> Result<()> {
    if !cfg!(any(target_os = "linux", target_os = "macos")) {
        bail!("Native Homebrew bottles require Linux or macOS");
    }
    validate(&package)?;
    let root = prepare(app_root)?;
    with_lock(&root, || {
        let receipts = read_all(app_root)?;
        let previous = receipts
            .iter()
            .find(|receipt| receipt.package.formula == package.formula);
        if previous.is_some_and(|receipt| {
            receipt.package.package_identifier != package.package_identifier
                || receipt.package.source_identifier != package.source_identifier
        }) {
            bail!(
                "Formula {} is already owned by another package or source",
                package.formula
            );
        }
        for dependency in &package.dependencies {
            if !receipts
                .iter()
                .any(|receipt| receipt.package.package_identifier == *dependency)
            {
                bail!("Required bottle dependency is not installed: {dependency}");
            }
        }
        let formula_directory = root.join("Cellar").join(&package.formula);
        fs::create_dir_all(&formula_directory)?;
        check_directory(&formula_directory)?;
        let keg = formula_directory.join(&package.keg_version);
        let stage = root.join(".staging").join(unique());
        let backup = stage.with_extension("previous");
        let mut links_before = BTreeMap::new();
        let mut moved_keg = false;
        let result = (|| -> Result<()> {
            let files = homebrew_bottle::extract(archive, &stage, &package.formula, &package.keg_version)?;
            homebrew_bottle::relocate(&files, &root, &package.original_prefix)?;
            let mut links = BTreeMap::from([(
                format!("opt/{}", package.formula),
                format!("../Cellar/{}/{}", package.formula, package.keg_version),
            )]);
            if !package.keg_only {
                for directory in ["bin", "sbin"] {
                    let source = stage.join(directory);
                    if !source.is_dir() {
                        continue;
                    }
                    for entry in fs::read_dir(source)? {
                        let entry = entry?;
                        if entry.path().is_dir() {
                            bail!("Bottle command directories are unsupported: {}", entry.path().display());
                        }
                        let name = entry
                            .file_name()
                            .into_string()
                            .map_err(|_| anyhow!("Command name is not UTF-8"))?;
                        validate_component(&name)?;
                        links.insert(
                            format!("{directory}/{name}"),
                            format!("../opt/{}/{directory}/{name}", package.formula),
                        );
                    }
                }
            }
            let mut old_links = previous.map(|receipt| receipt.links.clone()).unwrap_or_default();
            if let Some(previous) = previous {
                old_links.insert(
                    format!("opt/{}", package.formula),
                    format!("../Cellar/{}/{}", package.formula, previous.package.keg_version),
                );
            }
            for relative in links.keys().chain(old_links.keys()) {
                let path = root.join(relative);
                let target = link_target(&path)?;
                let exists = fs::symlink_metadata(&path).is_ok();
                if exists && (target.is_none() || target.as_ref() != old_links.get(relative)) {
                    bail!("Refusing to overwrite an unowned Homebrew link: {}", path.display());
                }
                links_before.insert(relative.clone(), target);
            }
            if fs::symlink_metadata(&keg).is_ok() {
                if previous.is_none_or(|receipt| receipt.package.keg_version != package.keg_version)
                    || fs::symlink_metadata(&keg)?.file_type().is_symlink()
                {
                    bail!("Refusing to replace an unowned Homebrew keg: {}", keg.display());
                }
                fs::rename(&keg, &backup)?;
            }
            fs::rename(&stage, &keg)?;
            moved_keg = true;
            for relative in links_before.keys() {
                if let Some(target) = links.get(relative) {
                    set_link(&root.join(relative), target)?;
                } else {
                    fs::remove_file(root.join(relative))?;
                }
            }
            let receipt = Receipt {
                schema_version: 1,
                package: package.clone(),
                links: links.into_iter().filter(|(name, _)| is_command_link(name)).collect(),
            };
            save_receipt(&root, &receipt)?;
            Ok(())
        })();
        if let Err(failure) = result {
            let rollback = (|| -> Result<()> {
                for (relative, target) in &links_before {
                    if let Some(target) = target {
                        set_link(&root.join(relative), target)?;
                    } else {
                        remove_link_if_present(&root.join(relative))?;
                    }
                }
                if moved_keg {
                    fs::remove_dir_all(&keg)?;
                }
                if backup.is_dir() {
                    fs::rename(&backup, &keg)?;
                }
                Ok(())
            })();
            cleanup(&stage, warnings);
            if let Err(rollback) = rollback {
                bail!("Bottle installation failed: {failure:#}; rollback requires attention: {rollback:#}");
            }
            return Err(failure);
        }
        cleanup(&stage, warnings);
        cleanup(&backup, warnings);
        if let Some(previous) = previous
            && previous.package.keg_version != package.keg_version
        {
            cleanup(&formula_directory.join(&previous.package.keg_version), warnings);
        }
        Ok(())
    })
}

pub(crate) fn uninstall(app_root: &Path, package_id: &str, force: bool, warnings: &mut Vec<String>) -> Result<()> {
    if !cfg!(any(target_os = "linux", target_os = "macos")) {
        bail!("Native Homebrew bottles require Linux or macOS");
    }
    let root = prepare(app_root)?;
    with_lock(&root, || {
        let receipts = read_all(app_root)?;
        let receipt = receipts
            .iter()
            .find(|receipt| receipt.package.package_identifier == package_id)
            .context("No native bottle is installed for the requested package")?;
        if !force
            && let Some(dependent) = receipts.iter().find(|receipt| {
                receipt.package.package_identifier != package_id
                    && receipt
                        .package
                        .dependencies
                        .iter()
                        .any(|dependency| dependency == package_id)
            })
        {
            bail!(
                "Cannot uninstall {package_id}; {} depends on it",
                dependent.package.package_identifier
            );
        }
        let package = &receipt.package;
        let mut links = receipt.links.clone();
        links.insert(
            format!("opt/{}", package.formula),
            format!("../Cellar/{}/{}", package.formula, package.keg_version),
        );
        for (relative, target) in &links {
            if link_target(&root.join(relative))?.as_ref() != Some(target) {
                bail!("Homebrew link was modified; refusing to remove it: {relative}");
            }
        }
        let keg = root.join("Cellar").join(&package.formula).join(&package.keg_version);
        check_directory(keg.parent().context("Keg has no parent")?)?;
        check_directory(&keg)?;
        let trash = root.join(".staging").join(format!("{}.uninstalled", unique()));
        fs::rename(&keg, &trash)?;
        let result = (|| -> Result<()> {
            for relative in links.keys() {
                fs::remove_file(root.join(relative))?;
            }
            fs::remove_file(root.join("receipts").join(format!("{}.json", package.formula)))?;
            Ok(())
        })();
        if let Err(failure) = result {
            fs::rename(&trash, &keg).with_context(|| format!("Uninstall failed: {failure:#}; rollback failed"))?;
            for (relative, target) in &links {
                set_link(&root.join(relative), target)
                    .with_context(|| format!("Uninstall failed: {failure:#}; link rollback failed"))?;
            }
            return Err(failure);
        }
        cleanup(&trash, warnings);
        Ok(())
    })
}

fn validate(package: &KegSpec) -> Result<()> {
    validate_component(&package.formula)?;
    validate_component(&package.keg_version)?;
    if package.package_identifier.trim().is_empty()
        || package.package_version.trim().is_empty()
        || package.name.trim().is_empty()
        || package.source_identifier.trim().is_empty()
        || package.source_name.trim().is_empty()
        || !package.original_prefix.starts_with('/')
        || package
            .dependencies
            .iter()
            .any(|dependency| dependency.trim().is_empty())
    {
        bail!("Invalid native Homebrew package identity or relocation prefix");
    }
    Ok(())
}

fn validate_component(value: &str) -> Result<()> {
    if value.is_empty()
        || matches!(value, "." | "..")
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-' | b'+' | b'@'))
    {
        bail!("Unsafe Homebrew path component: {value}");
    }
    Ok(())
}

fn is_command_link(value: &str) -> bool {
    value
        .split_once('/')
        .is_some_and(|(directory, name)| matches!(directory, "bin" | "sbin") && validate_component(name).is_ok())
}

fn prepare(app_root: &Path) -> Result<PathBuf> {
    let root = prefix(app_root);
    fs::create_dir_all(&root)?;
    check_directory(&root)?;
    for child in ["Cellar", "opt", "bin", "sbin", "receipts", ".staging"] {
        let directory = root.join(child);
        fs::create_dir_all(&directory)?;
        check_directory(&directory)?;
    }
    Ok(root)
}

fn check_directory(path: &Path) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.is_dir() || metadata.file_type().is_symlink() {
        bail!(
            "Homebrew storage must be an existing real directory: {}",
            path.display()
        );
    }
    Ok(())
}

fn link_target(path: &Path) -> Result<Option<String>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.file_type().is_symlink() => Ok(Some(
            fs::read_link(path)?
                .into_os_string()
                .into_string()
                .map_err(|_| anyhow!("Link target is not UTF-8"))?,
        )),
        Ok(_) => Ok(None),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error.into()),
    }
}

fn set_link(path: &Path, target: &str) -> Result<()> {
    let temporary = path.with_extension(format!("{}.tmp", unique()));
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, &temporary)?;
    #[cfg(not(unix))]
    bail!("Native bottle installation requires Linux or macOS");
    let result = fs::rename(&temporary, path);
    remove_link_if_present(&temporary)?;
    result.map_err(Into::into)
}

fn remove_link_if_present(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn save_receipt(root: &Path, receipt: &Receipt) -> Result<()> {
    let temporary = root.join(".staging").join(format!("{}.json", unique()));
    let result = (|| -> Result<()> {
        let mut file = fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
        file.write_all(&serde_json::to_vec(receipt)?)?;
        file.sync_all()?;
        fs::rename(
            &temporary,
            root.join("receipts").join(format!("{}.json", receipt.package.formula)),
        )?;
        Ok(())
    })();
    remove_link_if_present(&temporary)?;
    result
}

fn cleanup(path: &Path, warnings: &mut Vec<String>) {
    if path.is_dir()
        && let Err(error) = fs::remove_dir_all(path)
    {
        warnings.push(format!(
            "Could not remove obsolete Homebrew staging data '{}': {error}",
            path.display()
        ));
    }
}

fn with_lock(root: &Path, action: impl FnOnce() -> Result<()>) -> Result<()> {
    let path = root.join("operations.lock");
    let file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .context(
            "Homebrew operation lock exists or could not be created; check for an active operation or stale lock",
        )?;
    let result = action();
    drop(file);
    fs::remove_file(path).with_context(|| {
        format!(
            "Could not release Homebrew operation lock; operation result: {}",
            result
                .as_ref()
                .err()
                .map_or_else(|| "success".to_owned(), ToString::to_string)
        )
    })?;
    result
}

fn unique() -> String {
    static NEXT_ID: AtomicU64 = AtomicU64::new(0);
    format!(
        "{}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        NEXT_ID.fetch_add(1, Ordering::Relaxed)
    )
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use crate::homebrew_bottle::tests::Fixture;

    fn package(formula: &str, version: &str) -> KegSpec {
        KegSpec {
            package_identifier: format!("Homebrew.{formula}"),
            package_version: version.to_owned(),
            name: formula.to_owned(),
            formula: formula.to_owned(),
            keg_version: version.to_owned(),
            original_prefix: "/old".to_owned(),
            keg_only: false,
            source_identifier: "test-source".to_owned(),
            source_name: "test-source".to_owned(),
            dependencies: Vec::new(),
        }
    }

    #[test]
    fn upgrade_collision_preserves_previous_keg_and_receipt() -> Result<()> {
        let fixture = Fixture::new()?;
        let archive = fixture.0.join("bottle.tar.gz");
        let mut warnings = Vec::new();
        fixture.write(&[("sample/1.0/bin/sample", None)])?;
        install(&fixture.0, &archive, package("sample", "1.0"), &mut warnings)?;
        install(&fixture.0, &archive, package("sample", "1.0"), &mut warnings)?;
        let root = prefix(&fixture.0);
        fs::write(root.join("bin/unowned"), "preserve")?;
        fixture.write(&[("sample/2.0/bin/unowned", None)])?;
        assert!(install(&fixture.0, &archive, package("sample", "2.0"), &mut warnings).is_err());
        let receipts = read_all(&fixture.0)?;
        assert_eq!(receipts.len(), 1);
        assert_eq!(receipts[0].package.package_version, "1.0");
        assert_eq!(
            fs::read_link(root.join("opt/sample"))?,
            Path::new("../Cellar/sample/1.0")
        );
        assert_eq!(fs::read_to_string(root.join("bin/unowned"))?, "preserve");
        assert!(!root.join("Cellar/sample/2.0").exists());
        uninstall(&fixture.0, "Homebrew.sample", false, &mut warnings)?;
        assert!(read_all(&fixture.0)?.is_empty());
        assert_eq!(fs::read_to_string(root.join("bin/unowned"))?, "preserve");
        assert!(warnings.is_empty());
        Ok(())
    }

    #[test]
    fn uninstall_refuses_dependents_and_modified_links() -> Result<()> {
        let fixture = Fixture::new()?;
        let archive = fixture.0.join("bottle.tar.gz");
        let mut warnings = Vec::new();
        fixture.write(&[("dependency/1.0/lib/data", None)])?;
        install(&fixture.0, &archive, package("dependency", "1.0"), &mut warnings)?;
        fixture.write(&[("sample/1.0/bin/sample", None)])?;
        let mut sample = package("sample", "1.0");
        sample.dependencies.push("Homebrew.dependency".to_owned());
        install(&fixture.0, &archive, sample, &mut warnings)?;
        assert!(uninstall(&fixture.0, "Homebrew.dependency", false, &mut warnings).is_err());
        let command = prefix(&fixture.0).join("bin/sample");
        fs::remove_file(&command)?;
        fs::write(&command, "user-owned")?;
        assert!(uninstall(&fixture.0, "Homebrew.sample", false, &mut warnings).is_err());
        assert_eq!(fs::read_to_string(command)?, "user-owned");
        assert_eq!(read_all(&fixture.0)?.len(), 2);
        Ok(())
    }
}
