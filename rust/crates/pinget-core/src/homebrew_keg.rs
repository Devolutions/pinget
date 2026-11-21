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

/// Durable write-ahead journal shared with the .NET runtime. Its presence means a mutation of the
/// prefix may be incomplete; `Prepared` is rolled back and `Committed` is finished on recovery.
const JOURNAL: &str = "transaction.json";
/// Test-only seam: `<formula>:<step>` terminates the process at that step to simulate a crash.
const INTERRUPT_VARIABLE: &str = "PINGET_HOMEBREW_TEST_INTERRUPT";
const PREPARED: &str = "Prepared";
const COMMITTED: &str = "Committed";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct LinkChange {
    before: Option<String>,
    after: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
struct Transaction {
    schema_version: u32,
    operation: String,
    state: String,
    formula: String,
    keg_version: String,
    stage: Option<String>,
    backup: Option<String>,
    trash: Option<String>,
    obsolete_keg_version: Option<String>,
    links: BTreeMap<String, LinkChange>,
    previous_receipt: Option<Receipt>,
    receipt: Option<Receipt>,
}

#[derive(Debug)]
struct Interrupted(String);

impl std::fmt::Display for Interrupted {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "simulated interruption at {}", self.0)
    }
}

impl std::error::Error for Interrupted {}

#[cfg(test)]
thread_local! {
    static INTERRUPT_AT: std::cell::RefCell<Option<String>> = const { std::cell::RefCell::new(None) };
}

fn checkpoint(formula: &str, step: &str) -> Result<()> {
    let point = format!("{formula}:{step}");
    #[cfg(test)]
    if INTERRUPT_AT.with(|value| value.borrow().as_deref() == Some(point.as_str())) {
        return Err(Interrupted(point).into());
    }
    if std::env::var_os(INTERRUPT_VARIABLE).is_some_and(|value| value == point.as_str()) {
        std::process::exit(86);
    }
    Ok(())
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
    if fs::symlink_metadata(root.join(JOURNAL)).is_ok() {
        // An active operation owns the journal; otherwise recover before reporting state.
        if let Some(_lock) = try_lock(&root)? {
            recover(&root, &mut Vec::new())?;
        }
    }
    read_receipts(&root)
}

fn read_receipts(root: &Path) -> Result<Vec<Receipt>> {
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
        validate_receipt(&receipt)
            .with_context(|| format!("Unsupported or invalid Homebrew receipt: {}", path.display()))?;
        if path.file_name().and_then(|name| name.to_str()) != Some(&format!("{}.json", receipt.package.formula)) {
            bail!("Unsupported or invalid Homebrew receipt: {}", path.display());
        }
        receipts.push(receipt);
    }
    Ok(receipts)
}

fn validate_receipt(receipt: &Receipt) -> Result<()> {
    validate(&receipt.package)?;
    if receipt.schema_version != 1 || receipt.links.keys().any(|name| !is_command_link(name)) {
        bail!("Unsupported or invalid Homebrew receipt");
    }
    Ok(())
}

pub(crate) fn install(app_root: &Path, archive: &Path, package: KegSpec, warnings: &mut Vec<String>) -> Result<()> {
    if !cfg!(any(target_os = "linux", target_os = "macos")) {
        bail!("Native Homebrew bottles require Linux or macOS");
    }
    validate(&package)?;
    let root = prepare(app_root)?;
    with_lock(&root, warnings, |warnings| {
        let receipts = read_receipts(&root)?;
        let previous = receipts
            .iter()
            .find(|receipt| receipt.package.formula == package.formula)
            .cloned();
        if previous.as_ref().is_some_and(|receipt| {
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
        let stage_name = unique();
        let stage = root.join(".staging").join(&stage_name);
        let prepared = (|| -> Result<Transaction> {
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
            let mut old_links = previous
                .as_ref()
                .map(|receipt| receipt.links.clone())
                .unwrap_or_default();
            if let Some(previous) = &previous {
                old_links.insert(
                    format!("opt/{}", package.formula),
                    format!("../Cellar/{}/{}", package.formula, previous.package.keg_version),
                );
            }
            let mut changes = BTreeMap::new();
            for relative in links.keys().chain(old_links.keys()) {
                let path = root.join(relative);
                let target = link_target(&path)?;
                let exists = fs::symlink_metadata(&path).is_ok();
                if exists && (target.is_none() || target.as_ref() != old_links.get(relative)) {
                    bail!("Refusing to overwrite an unowned Homebrew link: {}", path.display());
                }
                changes.insert(
                    relative.clone(),
                    LinkChange {
                        before: target,
                        after: links.get(relative).cloned(),
                    },
                );
            }
            let backup = if fs::symlink_metadata(&keg).is_ok() {
                if previous
                    .as_ref()
                    .is_none_or(|receipt| receipt.package.keg_version != package.keg_version)
                    || fs::symlink_metadata(&keg)?.file_type().is_symlink()
                {
                    bail!("Refusing to replace an unowned Homebrew keg: {}", keg.display());
                }
                Some(format!("{stage_name}.previous"))
            } else {
                None
            };
            Ok(Transaction {
                schema_version: 1,
                operation: "install".to_owned(),
                state: PREPARED.to_owned(),
                formula: package.formula.clone(),
                keg_version: package.keg_version.clone(),
                stage: Some(stage_name.clone()),
                backup,
                trash: None,
                obsolete_keg_version: previous
                    .as_ref()
                    .map(|receipt| receipt.package.keg_version.clone())
                    .filter(|version| *version != package.keg_version),
                receipt: Some(Receipt {
                    schema_version: 1,
                    package: package.clone(),
                    links: links.into_iter().filter(|(name, _)| is_command_link(name)).collect(),
                }),
                links: changes,
                previous_receipt: previous.clone(),
            })
        })();
        let transaction = match prepared {
            Ok(transaction) => transaction,
            Err(failure) => {
                cleanup(&stage, warnings);
                return Err(failure);
            }
        };
        run_transaction(&root, transaction, warnings, apply_install)?;
        if let Some(previous) = &previous {
            report_dependency_changes(&previous.package, &package, warnings);
        }
        Ok(())
    })
}

fn report_dependency_changes(previous: &KegSpec, current: &KegSpec, warnings: &mut Vec<String>) {
    let added = current
        .dependencies
        .iter()
        .filter(|dependency| !previous.dependencies.contains(dependency))
        .cloned()
        .collect::<Vec<_>>();
    let removed = previous
        .dependencies
        .iter()
        .filter(|dependency| !current.dependencies.contains(dependency))
        .cloned()
        .collect::<Vec<_>>();
    if !added.is_empty() {
        warnings.push(format!(
            "{} now depends on: {}",
            current.package_identifier,
            added.join(", ")
        ));
    }
    if !removed.is_empty() {
        warnings.push(format!(
            "{} no longer depends on: {}; those packages remain installed",
            current.package_identifier,
            removed.join(", ")
        ));
    }
}

fn apply_install(root: &Path, transaction: &Transaction) -> Result<()> {
    let staging = root.join(".staging");
    let formula_directory = root.join("Cellar").join(&transaction.formula);
    let keg = formula_directory.join(&transaction.keg_version);
    if let Some(backup) = &transaction.backup {
        fs::rename(&keg, staging.join(backup))?;
        sync_directory(&formula_directory)?;
        sync_directory(&staging)?;
        checkpoint(&transaction.formula, "install-backup")?;
    }
    let stage = transaction.stage.as_deref().context("Install journal has no stage")?;
    fs::rename(staging.join(stage), &keg)?;
    sync_directory(&formula_directory)?;
    sync_directory(&staging)?;
    checkpoint(&transaction.formula, "install-keg")?;
    apply_links(root, transaction, "install-link")?;
    save_receipt(
        root,
        transaction.receipt.as_ref().context("Install journal has no receipt")?,
    )?;
    checkpoint(&transaction.formula, "install-receipt")
}

fn apply_uninstall(root: &Path, transaction: &Transaction) -> Result<()> {
    let staging = root.join(".staging");
    let formula_directory = root.join("Cellar").join(&transaction.formula);
    let trash = transaction.trash.as_deref().context("Uninstall journal has no trash")?;
    fs::rename(formula_directory.join(&transaction.keg_version), staging.join(trash))?;
    sync_directory(&formula_directory)?;
    sync_directory(&staging)?;
    checkpoint(&transaction.formula, "uninstall-trash")?;
    apply_links(root, transaction, "uninstall-link")?;
    fs::remove_file(root.join("receipts").join(format!("{}.json", transaction.formula)))?;
    sync_directory(&root.join("receipts"))?;
    checkpoint(&transaction.formula, "uninstall-receipt")
}

fn apply_links(root: &Path, transaction: &Transaction, step: &str) -> Result<()> {
    for (index, (relative, change)) in transaction.links.iter().enumerate() {
        let path = root.join(relative);
        match &change.after {
            Some(target) => set_link(root, &path, target)?,
            None => remove_link_if_present(&path)?,
        }
        if index == 0 {
            checkpoint(&transaction.formula, step)?;
        }
    }
    for directory in ["opt", "bin", "sbin"] {
        sync_directory(&root.join(directory))?;
    }
    Ok(())
}

fn run_transaction(
    root: &Path,
    mut transaction: Transaction,
    warnings: &mut Vec<String>,
    apply: fn(&Path, &Transaction) -> Result<()>,
) -> Result<()> {
    write_journal(root, &transaction)?;
    let operation = transaction.operation.clone();
    let result = checkpoint(&transaction.formula, &format!("{operation}-journal"))
        .and_then(|()| apply(root, &transaction))
        .and_then(|()| {
            let mut committed = transaction.clone();
            committed.state = COMMITTED.to_owned();
            write_journal(root, &committed)
        });
    if let Err(failure) = result {
        if failure.is::<Interrupted>() {
            return Err(failure);
        }
        if let Err(rollback) = rollback(root, &transaction) {
            bail!(
                "Homebrew {operation} of {} failed: {failure:#}; rollback requires attention and the recovery journal was kept at {}: {rollback:#}",
                transaction.formula,
                root.join(JOURNAL).display()
            );
        }
        return Err(failure);
    }
    transaction.state = COMMITTED.to_owned();
    checkpoint(&transaction.formula, &format!("{operation}-commit"))?;
    finish(root, &transaction, warnings)
}

fn recover(root: &Path, warnings: &mut Vec<String>) -> Result<()> {
    let path = root.join(JOURNAL);
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.into()),
    };
    let unrecoverable = || {
        format!(
            "Unrecoverable Homebrew transaction journal; inspect {} manually",
            path.display()
        )
    };
    if !metadata.is_file() {
        bail!("{}", unrecoverable());
    }
    let transaction: Transaction = serde_json::from_slice(&fs::read(&path)?).with_context(unrecoverable)?;
    validate_transaction(&transaction).with_context(unrecoverable)?;
    if transaction.state == PREPARED {
        rollback(root, &transaction).with_context(|| {
            format!(
                "Could not roll back the interrupted Homebrew {} of {}; the journal was kept at {} for manual recovery",
                transaction.operation,
                transaction.formula,
                path.display()
            )
        })?;
        warnings.push(format!(
            "Rolled back an interrupted Homebrew {} of {}",
            transaction.operation, transaction.formula
        ));
    } else {
        finish(root, &transaction, warnings)?;
        warnings.push(format!(
            "Completed an interrupted Homebrew {} of {}",
            transaction.operation, transaction.formula
        ));
    }
    Ok(())
}

fn validate_transaction(transaction: &Transaction) -> Result<()> {
    validate_component(&transaction.formula)?;
    validate_component(&transaction.keg_version)?;
    for name in [
        &transaction.stage,
        &transaction.backup,
        &transaction.trash,
        &transaction.obsolete_keg_version,
    ]
    .into_iter()
    .flatten()
    {
        validate_component(name)?;
    }
    let install = transaction.operation == "install";
    if transaction.schema_version != 1
        || !matches!(transaction.state.as_str(), PREPARED | COMMITTED)
        || (install && (transaction.stage.is_none() || transaction.receipt.is_none()))
        || (!install
            && (transaction.operation != "uninstall"
                || transaction.trash.is_none()
                || transaction.previous_receipt.is_none()))
    {
        bail!("Unsupported Homebrew transaction");
    }
    for receipt in [&transaction.previous_receipt, &transaction.receipt]
        .into_iter()
        .flatten()
    {
        validate_receipt(receipt)?;
        if receipt.package.formula != transaction.formula {
            bail!("Homebrew transaction receipt does not match its formula");
        }
    }
    let opt = format!("opt/{}", transaction.formula);
    if transaction
        .links
        .keys()
        .any(|name| *name != opt && !is_command_link(name))
    {
        bail!("Homebrew transaction contains an unsupported link");
    }
    Ok(())
}

/// Restores the pre-transaction state. Every step is idempotent so recovery can itself be interrupted.
fn rollback(root: &Path, transaction: &Transaction) -> Result<()> {
    let staging = root.join(".staging");
    for (relative, change) in &transaction.links {
        let path = root.join(relative);
        let current = entry_state(&path)?;
        if current == change.before.clone().map(Some) {
            continue;
        }
        if current != change.after.clone().map(Some) {
            bail!(
                "Homebrew link was changed outside Pinget during an interrupted operation: {}",
                path.display()
            );
        }
        match &change.before {
            Some(target) => set_link(root, &path, target)?,
            None => fs::remove_file(&path)?,
        }
    }
    for directory in ["opt", "bin", "sbin"] {
        sync_directory(&root.join(directory))?;
    }
    checkpoint(&transaction.formula, "rollback-links")?;
    let formula_directory = root.join("Cellar").join(&transaction.formula);
    let keg = formula_directory.join(&transaction.keg_version);
    if transaction.operation == "install" {
        match &transaction.backup {
            Some(backup) => {
                let backup = staging.join(backup);
                if fs::symlink_metadata(&backup).is_ok() {
                    remove_owned_directory(&keg)?;
                    fs::rename(&backup, &keg)?;
                }
            }
            None => remove_owned_directory(&keg)?,
        }
    } else if let Some(trash) = &transaction.trash {
        let trash = staging.join(trash);
        if fs::symlink_metadata(&trash).is_ok() {
            if fs::symlink_metadata(&keg).is_ok() {
                bail!(
                    "A Homebrew keg reappeared during an interrupted uninstall: {}",
                    keg.display()
                );
            }
            fs::rename(&trash, &keg)?;
        }
    }
    if formula_directory.is_dir() {
        if transaction.previous_receipt.is_none() && fs::read_dir(&formula_directory)?.next().is_none() {
            fs::remove_dir(&formula_directory)?;
            sync_directory(&root.join("Cellar"))?;
        } else {
            sync_directory(&formula_directory)?;
        }
    }
    match &transaction.previous_receipt {
        Some(receipt) => save_receipt(root, receipt)?,
        None => {
            remove_link_if_present(&root.join("receipts").join(format!("{}.json", transaction.formula)))?;
            sync_directory(&root.join("receipts"))?;
        }
    }
    if let Some(stage) = &transaction.stage {
        remove_tree_if_present(&staging.join(stage))?;
    }
    remove_journal(root)
}

/// Completes cleanup for a committed transaction; leftovers only produce warnings.
fn finish(root: &Path, transaction: &Transaction, warnings: &mut Vec<String>) -> Result<()> {
    let staging = root.join(".staging");
    for name in [&transaction.stage, &transaction.backup, &transaction.trash]
        .into_iter()
        .flatten()
    {
        cleanup(&staging.join(name), warnings);
    }
    if let Some(obsolete) = &transaction.obsolete_keg_version
        && *obsolete != transaction.keg_version
    {
        cleanup(&root.join("Cellar").join(&transaction.formula).join(obsolete), warnings);
    }
    remove_journal(root)
}

/// `None` = absent, `Some(None)` = non-link entry, `Some(Some(target))` = symbolic link.
fn entry_state(path: &Path) -> Result<Option<Option<String>>> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(Some(link_target(path)?)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error.into()),
    }
}

fn remove_owned_directory(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.is_dir() => Ok(fs::remove_dir_all(path)?),
        Ok(_) => bail!(
            "Homebrew keg was replaced outside Pinget during an interrupted operation: {}",
            path.display()
        ),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

fn remove_tree_if_present(path: &Path) -> Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if metadata.is_dir() => Ok(fs::remove_dir_all(path)?),
        Ok(_) => Ok(fs::remove_file(path)?),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

pub(crate) fn uninstall(app_root: &Path, package_id: &str, force: bool, warnings: &mut Vec<String>) -> Result<()> {
    if !cfg!(any(target_os = "linux", target_os = "macos")) {
        bail!("Native Homebrew bottles require Linux or macOS");
    }
    let root = prepare(app_root)?;
    with_lock(&root, warnings, |warnings| {
        let receipts = read_receipts(&root)?;
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
        let transaction = Transaction {
            schema_version: 1,
            operation: "uninstall".to_owned(),
            state: PREPARED.to_owned(),
            formula: package.formula.clone(),
            keg_version: package.keg_version.clone(),
            stage: None,
            backup: None,
            trash: Some(format!("{}.uninstalled", unique())),
            obsolete_keg_version: None,
            links: links
                .into_iter()
                .map(|(relative, target)| {
                    (
                        relative,
                        LinkChange {
                            before: Some(target),
                            after: None,
                        },
                    )
                })
                .collect(),
            previous_receipt: Some(receipt.clone()),
            receipt: None,
        };
        run_transaction(&root, transaction, warnings, apply_uninstall)
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

fn set_link(root: &Path, path: &Path, target: &str) -> Result<()> {
    // Stage the link privately so an interruption never leaves temporary entries in bin/opt.
    let temporary = root.join(".staging").join(format!("{}.link", unique()));
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

fn write_durable(root: &Path, destination: &Path, bytes: &[u8]) -> Result<()> {
    let temporary = root.join(".staging").join(format!("{}.json", unique()));
    let result = (|| -> Result<()> {
        let mut file = fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        fs::rename(&temporary, destination)?;
        sync_directory(destination.parent().context("Durable file has no parent")?)
    })();
    remove_link_if_present(&temporary)?;
    result
}

fn save_receipt(root: &Path, receipt: &Receipt) -> Result<()> {
    write_durable(
        root,
        &root.join("receipts").join(format!("{}.json", receipt.package.formula)),
        &serde_json::to_vec(receipt)?,
    )
}

fn write_journal(root: &Path, transaction: &Transaction) -> Result<()> {
    write_durable(root, &root.join(JOURNAL), &serde_json::to_vec(transaction)?)
}

fn remove_journal(root: &Path) -> Result<()> {
    remove_link_if_present(&root.join(JOURNAL))?;
    sync_directory(root)
}

fn sync_directory(path: &Path) -> Result<()> {
    #[cfg(unix)]
    fs::File::open(path)?.sync_all()?;
    #[cfg(not(unix))]
    let _ = path;
    Ok(())
}

fn cleanup(path: &Path, warnings: &mut Vec<String>) {
    if let Err(error) = remove_tree_if_present(path) {
        warnings.push(format!(
            "Could not remove obsolete Homebrew staging data '{}': {error}",
            path.display()
        ));
    }
}

/// Acquires the advisory operation lock shared with the .NET runtime (`flock` on Unix). The OS
/// releases it when the process exits, so an interrupted operation never leaves a stale lock.
fn try_lock(root: &Path) -> Result<Option<fs::File>> {
    let file = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(root.join("operations.lock"))?;
    match file.try_lock() {
        Ok(()) => Ok(Some(file)),
        Err(fs::TryLockError::WouldBlock) => Ok(None),
        Err(fs::TryLockError::Error(error)) => Err(error.into()),
    }
}

fn with_lock<T>(
    root: &Path,
    warnings: &mut Vec<String>,
    action: impl FnOnce(&mut Vec<String>) -> Result<T>,
) -> Result<T> {
    let _lock = try_lock(root)?.context("Another Pinget Homebrew operation is in progress for this prefix")?;
    recover(root, warnings)?;
    // Without a journal, anything left in the private staging area is from an interrupted
    // extraction and is owned by Pinget.
    for entry in fs::read_dir(root.join(".staging"))? {
        cleanup(&entry?.path(), warnings);
    }
    action(warnings)
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

    fn bottle(fixture: &Fixture, formula: &str, version: &str, commands: &[&str]) -> Result<()> {
        let mut names = vec![format!("{formula}/{version}/lib/data")];
        names.extend(
            commands
                .iter()
                .map(|command| format!("{formula}/{version}/bin/{command}")),
        );
        let entries = names.iter().map(|name| (name.as_str(), None)).collect::<Vec<_>>();
        fixture.write(&entries)
    }

    fn install_bottle(
        fixture: &Fixture,
        formula: &str,
        version: &str,
        commands: &[&str],
        dependencies: &[&str],
    ) -> Result<()> {
        bottle(fixture, formula, version, commands)?;
        let mut spec = package(formula, version);
        spec.dependencies = dependencies.iter().map(|value| (*value).to_owned()).collect();
        install(&fixture.0, &fixture.0.join("bottle.tar.gz"), spec, &mut Vec::new())
    }

    fn snapshot(fixture: &Fixture) -> Result<Vec<(String, String)>> {
        fn walk(base: &Path, directory: &Path, entries: &mut Vec<(String, String)>) -> Result<()> {
            let mut children = fs::read_dir(directory)?.collect::<std::io::Result<Vec<_>>>()?;
            children.sort_by_key(fs::DirEntry::file_name);
            for child in children {
                let path = child.path();
                let relative = path.strip_prefix(base)?.to_string_lossy().into_owned();
                if matches!(relative.as_str(), ".staging" | "operations.lock") {
                    continue;
                }
                let metadata = fs::symlink_metadata(&path)?;
                let description = if metadata.file_type().is_symlink() {
                    format!("link:{}", fs::read_link(&path)?.display())
                } else if metadata.is_dir() {
                    walk(base, &path, entries)?;
                    "dir".to_owned()
                } else {
                    use std::os::unix::fs::PermissionsExt as _;
                    format!(
                        "file:{:o}:{}",
                        metadata.permissions().mode() & 0o777,
                        String::from_utf8_lossy(&fs::read(&path)?)
                    )
                };
                entries.push((relative, description));
            }
            Ok(())
        }
        let root = prefix(&fixture.0);
        let mut entries = Vec::new();
        walk(&root, &root, &mut entries)?;
        Ok(entries)
    }

    fn interrupted<T>(point: &str, action: impl FnOnce() -> Result<T>) -> Result<()> {
        INTERRUPT_AT.with(|value| *value.borrow_mut() = Some(point.to_owned()));
        let result = action();
        INTERRUPT_AT.with(|value| *value.borrow_mut() = None);
        let error = result.err().context("operation was not interrupted")?;
        assert!(error.is::<Interrupted>(), "{error:#}");
        Ok(())
    }

    type Step = fn(&Fixture) -> Result<()>;

    fn setup_dependency(fixture: &Fixture) -> Result<()> {
        install_bottle(fixture, "dependency", "1.0", &[], &[])?;
        fs::write(prefix(&fixture.0).join("bin/unrelated"), "user file")?;
        Ok(())
    }

    fn setup_sample(fixture: &Fixture) -> Result<()> {
        setup_dependency(fixture)?;
        install_bottle(fixture, "sample", "1.0", &["sample", "old"], &[])
    }

    fn fresh_install(fixture: &Fixture) -> Result<()> {
        install_bottle(fixture, "sample", "1.0", &["sample"], &["Homebrew.dependency"])
    }

    fn repair(fixture: &Fixture) -> Result<()> {
        install_bottle(fixture, "sample", "1.0", &["sample", "old"], &[])
    }

    fn upgrade(fixture: &Fixture) -> Result<()> {
        install_bottle(fixture, "sample", "2.0", &["sample", "new"], &["Homebrew.dependency"])
    }

    fn remove(fixture: &Fixture) -> Result<()> {
        uninstall(&fixture.0, "Homebrew.sample", false, &mut Vec::new())
    }

    #[test]
    fn interruption_at_every_step_recovers_exact_previous_or_committed_state() -> Result<()> {
        let scenarios: [(Step, Step, &[&str], &str); 4] = [
            (
                setup_dependency,
                fresh_install,
                &["journal", "keg", "link", "receipt"],
                "install",
            ),
            (
                setup_sample,
                repair,
                &["journal", "backup", "keg", "link", "receipt"],
                "install",
            ),
            (setup_sample, upgrade, &["journal", "keg", "link", "receipt"], "install"),
            (
                setup_sample,
                remove,
                &["journal", "trash", "link", "receipt"],
                "uninstall",
            ),
        ];
        for (setup, operation, steps, kind) in scenarios {
            let expected = Fixture::new()?;
            setup(&expected)?;
            operation(&expected)?;
            let committed = snapshot(&expected)?;
            for step in steps
                .iter()
                .map(|step| format!("{kind}-{step}"))
                .chain([format!("{kind}-commit")])
            {
                let fixture = Fixture::new()?;
                setup(&fixture)?;
                let before = snapshot(&fixture)?;
                interrupted(&format!("sample:{step}"), || operation(&fixture))?;
                let root = prefix(&fixture.0);
                assert!(root.join(JOURNAL).is_file(), "{step}");
                // Listing recovers, as would the next operation from either runtime.
                read_all(&fixture.0)?;
                assert!(!root.join(JOURNAL).exists(), "{step}");
                assert_eq!(fs::read_dir(root.join(".staging"))?.count(), 0, "{step}");
                let recovered = snapshot(&fixture)?;
                if step.ends_with("-commit") {
                    assert_eq!(recovered, committed, "{step}");
                } else {
                    assert_eq!(recovered, before, "{step}");
                    operation(&fixture)?;
                    assert_eq!(snapshot(&fixture)?, committed, "{step}");
                }
                assert_eq!(fs::read_to_string(root.join("bin/unrelated"))?, "user file");
            }
        }
        Ok(())
    }

    #[test]
    fn interrupted_recovery_resumes_and_outside_changes_are_unrecoverable() -> Result<()> {
        let fixture = Fixture::new()?;
        setup_sample(&fixture)?;
        let before = snapshot(&fixture)?;
        interrupted("sample:install-receipt", || upgrade(&fixture))?;
        interrupted("sample:rollback-links", || read_all(&fixture.0))?;
        let root = prefix(&fixture.0);
        assert!(root.join(JOURNAL).is_file());
        let mut warnings = Vec::new();
        recover(&root, &mut warnings)?;
        assert_eq!(warnings, ["Rolled back an interrupted Homebrew install of sample"]);
        assert_eq!(snapshot(&fixture)?, before);

        interrupted("sample:install-link", || upgrade(&fixture))?;
        let command = root.join("bin/new");
        assert_eq!(fs::read_link(&command)?, Path::new("../opt/sample/bin/new"));
        fs::remove_file(&command)?;
        fs::write(&command, "user replacement")?;
        let error = read_all(&fixture.0).expect_err("outside change must stop recovery");
        assert!(format!("{error:#}").contains("changed outside Pinget"), "{error:#}");
        assert!(root.join(JOURNAL).is_file());
        assert_eq!(fs::read_to_string(&command)?, "user replacement");
        assert!(upgrade(&fixture).is_err());

        fs::write(root.join(JOURNAL), "{")?;
        let error = read_all(&fixture.0).expect_err("corrupt journal must be explicit");
        assert!(
            format!("{error:#}").contains("Unrecoverable Homebrew transaction journal"),
            "{error:#}"
        );
        Ok(())
    }

    #[test]
    fn operation_lock_is_exclusive_and_released_by_process_exit_semantics() -> Result<()> {
        let fixture = Fixture::new()?;
        setup_dependency(&fixture)?;
        let root = prefix(&fixture.0);
        let held = try_lock(&root)?.context("lock should be free")?;
        let error = fresh_install(&fixture).expect_err("concurrent operation must be refused");
        assert!(format!("{error:#}").contains("in progress"), "{error:#}");
        drop(held);
        // A leftover lock file from an interrupted process does not block later operations.
        assert!(root.join("operations.lock").is_file());
        fresh_install(&fixture)
    }

    #[test]
    fn upgrade_reports_dependency_changes() -> Result<()> {
        let fixture = Fixture::new()?;
        setup_sample(&fixture)?;
        bottle(&fixture, "sample", "2.0", &["sample"])?;
        let mut spec = package("sample", "2.0");
        spec.dependencies = vec!["Homebrew.dependency".to_owned()];
        let mut warnings = Vec::new();
        install(&fixture.0, &fixture.0.join("bottle.tar.gz"), spec, &mut warnings)?;
        assert_eq!(warnings, ["Homebrew.sample now depends on: Homebrew.dependency"]);
        bottle(&fixture, "sample", "3.0", &["sample"])?;
        warnings.clear();
        install(
            &fixture.0,
            &fixture.0.join("bottle.tar.gz"),
            package("sample", "3.0"),
            &mut warnings,
        )?;
        assert_eq!(
            warnings,
            ["Homebrew.sample no longer depends on: Homebrew.dependency; those packages remain installed"]
        );
        Ok(())
    }
}
