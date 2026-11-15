//! Wave 19 (portable installs and archives) regression tests.

#[cfg_attr(not(windows), allow(unused_imports))]
use std::io::Write;
#[cfg(windows)]
use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

#[cfg(windows)]
static COUNTER: AtomicU64 = AtomicU64::new(0);

#[cfg(windows)]
fn unique_name(label: &str) -> String {
    format!(
        "PingetWave19{label}_{}_{}_{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst),
        Utc::now().timestamp_nanos_opt().unwrap_or_default()
    )
}

#[cfg(windows)]
struct TempDir {
    path: PathBuf,
}

#[cfg(windows)]
impl TempDir {
    fn new(label: &str) -> Self {
        let path = std::env::temp_dir().join(unique_name(label));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("create temp dir");
        Self { path }
    }
}

#[cfg(windows)]
impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

fn nested_file(path: &str) -> NestedInstallerFile {
    NestedInstallerFile {
        relative_file_path: path.to_owned(),
        portable_command_alias: None,
    }
}

fn zip_installer(nested_type: &str, files: &[&str]) -> Installer {
    Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some(nested_type.to_owned()),
        nested_installer_files: files.iter().map(|path| nested_file(path)).collect(),
        ..Installer::default()
    }
}

// ── W19-1: an archive portable without NestedInstallerFiles is rejected ──

#[test]
fn archive_portable_without_nested_files_is_rejected() {
    let installer = zip_installer("portable", &[]);
    let error = determine_portable_links(&installer, true, None, None)
        .expect_err("a portable archive must name its NestedInstallerFiles");
    assert!(error.to_string().contains("NestedInstallerFiles"), "{error:#}");

    let installer = zip_installer("portable", &["bin/tool.exe"]);
    let links = determine_portable_links(&installer, true, None, None).expect("links");
    assert_eq!(links.len(), 1);
}

// ── W19-2: a non-portable nested installer must be a single file ──

#[test]
fn nested_installer_target_rejects_multiple_files_for_single_installer_types() {
    let installer = zip_installer("exe", &["setup.exe", "other.exe"]);
    assert!(
        nested_archive_installer_target(&installer).is_err(),
        "an exe archive naming two nested installers must be rejected, not run the first"
    );

    let installer = zip_installer("msi", &["setup.msi"]);
    assert_eq!(
        nested_archive_installer_target(&installer).expect("single nested installer"),
        ("msi".to_owned(), "setup.msi".to_owned())
    );
}

// ── W19-3: superscript COM/LPT device names are reserved ──

#[test]
fn superscript_device_names_are_rejected_in_portable_paths() {
    for path in ["COM\u{b9}.exe", "bin/lpt\u{b2}", "Com\u{b3}.txt", "LPT\u{b9}"] {
        assert!(
            normalize_portable_relative_path(path).is_err(),
            "{path} names a reserved device"
        );
    }
    for path in ["COM1.exe", "lpt9"] {
        assert!(normalize_portable_relative_path(path).is_err(), "{path}");
    }
    for path in ["COM10.exe", "COM0", "LPT", "computer.exe", "com\u{b9}x.exe"] {
        assert!(normalize_portable_relative_path(path).is_ok(), "{path} is not reserved");
    }
}

// ── W19-8: a local manifest portable is attributed to *DefaultSource ──

#[cfg(windows)]
#[test]
fn local_manifest_portable_uses_default_source_identifier() {
    let mut request = InstallRequest::new(PackageQuery::default());
    request.manifest_path = Some(PathBuf::from(r"C:\manifests\contoso.tool.yaml"));
    let source = portable_install_source_identifier(&request);
    assert_eq!(source, "*DefaultSource");
    // MakeSuitablePathPart turns the '*' into '_', as WinGet's product code does.
    let subkey = portable_subkey_name("Contoso.Tool", &source);
    assert_eq!(subkey, "Contoso.Tool__DefaultSource");
    assert_eq!(normalize_portable_relative_path(&subkey).expect("valid subkey"), subkey);

    let mut request = InstallRequest::new(PackageQuery {
        source: Some("winget".to_owned()),
        ..PackageQuery::default()
    });
    request.manifest_path = None;
    assert_eq!(
        portable_install_source_identifier(&request),
        "Microsoft.Winget.Source_8wekyb3d8bbwe"
    );
}

// ── W19-6: removing an install directory from PATH ──

#[cfg(windows)]
#[test]
fn remove_portable_path_entry_drops_only_the_matching_directory() {
    let dir = r"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Contoso.Tool_src";
    let existing = format!(r"C:\Windows;{dir}\;%LOCALAPPDATA%\Other;{}", dir.to_uppercase());
    let updated = remove_portable_path_entry(&existing, dir, |value| value.to_owned()).expect("removed");
    assert_eq!(updated, r"C:\Windows;%LOCALAPPDATA%\Other");

    // A sibling with the same prefix is not the same directory.
    assert_eq!(
        remove_portable_path_entry(&format!(r"{dir}-2;C:\Windows"), dir, |value| value.to_owned()),
        None
    );
    // An expandable reference to the directory is recognized.
    let expanded = remove_portable_path_entry(r"%PKG%;C:\Windows", dir, |value| value.replace("%PKG%", dir));
    assert_eq!(expanded.as_deref(), Some(r"C:\Windows"));
}

#[cfg(windows)]
#[test]
fn prepare_portable_path_removal_keeps_value_type_and_raw_variables() {
    let dir = r"C:\Pkg\Tool";
    let mut raw = format!(r"%SystemRoot%;{dir};%USERPROFILE%\bin").to_reg_value();
    raw.vtype = REG_EXPAND_SZ;
    let update = prepare_portable_path_removal(Some(raw), dir, |value| value.to_owned())
        .expect("prepare")
        .expect("dir removed");
    assert_eq!(update.vtype, REG_EXPAND_SZ);
    let value = OsString::from_reg_value(&update)
        .expect("decode")
        .into_string()
        .expect("utf-16");
    assert_eq!(value, r"%SystemRoot%;%USERPROFILE%\bin");

    assert!(
        prepare_portable_path_removal(None, dir, |value| value.to_owned())
            .expect("missing PATH")
            .is_none()
    );
}

// ── Registry-backed tests (test-specific HKCU uninstall subkeys, always removed) ──

#[cfg(windows)]
struct TestArpKey {
    name: String,
    path: String,
}

#[cfg(windows)]
impl TestArpKey {
    fn new(label: &str) -> Self {
        let name = unique_name(label);
        let path = format!(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}");
        Self { name, path }
    }

    fn create(&self) -> RegKey {
        RegKey::predef(HKEY_CURRENT_USER)
            .create_subkey(&self.path)
            .expect("create test ARP key")
            .0
    }

    fn open(&self) -> Option<RegKey> {
        RegKey::predef(HKEY_CURRENT_USER).open_subkey(&self.path).ok()
    }

    fn list_match(&self, install_location: Option<&Path>) -> ListMatch {
        ListMatch {
            name: self.name.clone(),
            id: self.name.clone(),
            local_id: format!(r"ARP\User\X64\{}", self.name),
            installed_version: "1.0.0".to_owned(),
            available_version: None,
            source_name: None,
            publisher: None,
            scope: Some("user".to_owned()),
            installer_category: Some("portable".to_owned()),
            install_location: install_location.map(|path| path.to_string_lossy().to_string()),
            package_family_names: Vec::new(),
            product_codes: Vec::new(),
            upgrade_codes: Vec::new(),
        }
    }
}

#[cfg(windows)]
impl Drop for TestArpKey {
    fn drop(&mut self) {
        let _ = RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(&self.path);
    }
}

#[cfg(windows)]
fn test_manifest(id: &str) -> Manifest {
    parse_yaml_manifest(
        format!(
            "PackageIdentifier: {id}\nPackageVersion: 1.2.3\nPackageName: Contoso Tool\nPublisher: Contoso\n\
             PublisherSupportUrl: https://contoso.example/support\nManifestType: merged\nManifestVersion: 1.10.0\nInstallers: []\n"
        )
        .as_bytes(),
    )
    .expect("parse manifest")
}

#[cfg(windows)]
fn write_test_arp_entry(key: &TestArpKey, location: &Path, target: &Path, manifest: &Manifest, installer: &Installer) {
    let symlink = location.join("links").join("tool.exe");
    write_portable_arp_entry(
        &PortableArpEntry {
            subkey_name: &key.name,
            install_location: location,
            portable_target_full_path: target,
            portable_symlink_full_path: Some(&symlink),
            symlinks: std::slice::from_ref(&symlink),
            install_directory_created: true,
            install_directory_added_to_path: false,
            files: &["tool.exe".to_owned()],
            directories: &[],
            source_identifier: "Microsoft.Winget.Source_8wekyb3d8bbwe",
            registry_32: false,
            manifest,
            installer,
        },
        PortableScope::User,
    )
    .expect("write ARP entry");
}

// ── W19-4: pinget writes WinGet's portable ARP value names and the target hash ──

#[cfg(windows)]
#[test]
fn portable_arp_entry_uses_winget_value_names_and_target_hash() {
    let temp = TempDir::new("ArpNames");
    let target = temp.path.join("tool.exe");
    fs::write(&target, b"portable payload").expect("write target");
    let key = TestArpKey::new("ArpNames");
    {
        // A value an earlier pinget wrote under its own name must not linger.
        let legacy = key.create();
        legacy
            .set_value("PortableTargetFullPath", &r"C:\old\tool.exe".to_owned())
            .expect("legacy value");
    }
    let manifest = test_manifest(&key.name);
    write_test_arp_entry(&key, &temp.path, &target, &manifest, &Installer::default());

    let subkey = key.open().expect("ARP key written");
    assert_eq!(
        read_reg_string(&subkey, "TargetFullPath").as_deref(),
        Some(target.to_string_lossy().as_ref())
    );
    assert_eq!(
        read_reg_string(&subkey, "SymlinkFullPath").as_deref(),
        Some(temp.path.join("links").join("tool.exe").to_string_lossy().as_ref())
    );
    assert_eq!(
        read_reg_string(&subkey, "SHA256"),
        Some(sha256_hex(b"portable payload").to_ascii_lowercase()),
        "WinGet verifies the target hash before removing it"
    );
    assert!(read_reg_string(&subkey, "PortableTargetFullPath").is_none());
    assert!(read_reg_string(&subkey, "PortableSymlinkFullPath").is_none());
    assert_eq!(read_reg_dword(&subkey, "InstallDirectoryAddedToPath"), Some(0));
}

// ── W19-7: the ARP identity comes from AppsAndFeaturesEntries, like WinGet ──

#[cfg(windows)]
#[test]
fn portable_arp_entry_prefers_apps_and_features_identity() {
    let temp = TempDir::new("ArpIdentity");
    let target = temp.path.join("tool.exe");
    fs::write(&target, b"tool").expect("write target");
    let key = TestArpKey::new("ArpIdentity");
    let manifest = test_manifest(&key.name);
    let installer = Installer {
        apps_and_features_entries: vec![AppsAndFeaturesEntry {
            display_name: Some("Contoso Tool (x64)".to_owned()),
            publisher: Some("Contoso Ltd.".to_owned()),
            display_version: Some("1.2.3.0".to_owned()),
            ..AppsAndFeaturesEntry::default()
        }],
        ..Installer::default()
    };
    write_test_arp_entry(&key, &temp.path, &target, &manifest, &installer);

    let subkey = key.open().expect("ARP key written");
    assert_eq!(
        read_reg_string(&subkey, "DisplayName").as_deref(),
        Some("Contoso Tool (x64)")
    );
    assert_eq!(read_reg_string(&subkey, "DisplayVersion").as_deref(), Some("1.2.3.0"));
    assert_eq!(read_reg_string(&subkey, "Publisher").as_deref(), Some("Contoso Ltd."));
    assert_eq!(
        read_reg_string(&subkey, "HelpLink").as_deref(),
        Some("https://contoso.example/support")
    );

    // Without AppsAndFeaturesEntries the manifest identity is used.
    write_test_arp_entry(&key, &temp.path, &target, &manifest, &Installer::default());
    let subkey = key.open().expect("ARP key written");
    assert_eq!(read_reg_string(&subkey, "DisplayName").as_deref(), Some("Contoso Tool"));
    assert_eq!(read_reg_string(&subkey, "DisplayVersion").as_deref(), Some("1.2.3"));
    assert_eq!(read_reg_string(&subkey, "Publisher").as_deref(), Some("Contoso"));
}

// ── W19-5: pinget reads WinGet's TargetFullPath when uninstalling ──

#[cfg(windows)]
#[test]
fn uninstall_of_winget_installed_portable_removes_its_target() {
    let temp = TempDir::new("WinGetTarget");
    let location = temp.path.join("Contoso.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe");
    fs::create_dir_all(&location).expect("create location");
    let target = location.join("tool.exe");
    fs::write(&target, b"binary").expect("write target");
    let key = TestArpKey::new("WinGetTarget");
    {
        // The values WinGet itself writes for a standalone portable.
        let subkey = key.create();
        subkey
            .set_value("WinGetInstallerType", &"portable".to_owned())
            .expect("type");
        subkey
            .set_value("InstallLocation", &location.to_string_lossy().to_string())
            .expect("location");
        subkey
            .set_value("TargetFullPath", &target.to_string_lossy().to_string())
            .expect("target");
        subkey.set_value("SHA256", &sha256_hex(b"binary")).expect("hash");
        subkey.set_value("InstallDirectoryCreated", &1u32).expect("created");
    }

    let installed = key.list_match(Some(&location));
    let record = read_portable_entry_for_installed(&installed)
        .expect("read entry")
        .expect("entry exists");
    assert_eq!(
        record.entry.portable_target_full_path.as_deref(),
        Some(target.as_path())
    );

    let mut warnings = Vec::new();
    let exit_code = uninstall_package(
        &installed,
        &UninstallRequest::new(PackageQuery::default()),
        &mut warnings,
    )
    .expect("uninstall");
    assert_eq!(exit_code, 0);
    assert!(
        !target.exists(),
        "the WinGet-recorded binary must be removed: {warnings:?}"
    );
    assert!(!location.exists(), "the now-empty created directory must be removed");
    assert!(key.open().is_none(), "the ARP entry must be removed");
}

// ── W19-6: the InstallDirectoryAddedToPath flag is read for uninstall ──

#[cfg(windows)]
#[test]
fn portable_entry_reports_install_directory_added_to_path() {
    let key = TestArpKey::new("AddedToPath");
    {
        let subkey = key.create();
        subkey
            .set_value("WinGetInstallerType", &"portable".to_owned())
            .expect("type");
        subkey.set_value("InstallDirectoryAddedToPath", &1u32).expect("flag");
    }
    let record = read_portable_entry_for_installed(&key.list_match(None))
        .expect("read entry")
        .expect("entry exists");
    assert!(record.install_directory_added_to_path);
}

// ── W19-10: an already-deleted install directory still uninstalls ──

#[cfg(windows)]
#[test]
fn uninstall_with_missing_install_location_unregisters_without_force() {
    let temp = TempDir::new("MissingLocation");
    let location = temp.path.join("gone");
    let key = TestArpKey::new("MissingLocation");
    {
        let subkey = key.create();
        subkey
            .set_value("WinGetInstallerType", &"portable".to_owned())
            .expect("type");
        subkey
            .set_value("InstallLocation", &location.to_string_lossy().to_string())
            .expect("location");
        subkey.set_value("InstallDirectoryCreated", &1u32).expect("created");
    }

    let mut warnings = Vec::new();
    let result = uninstall_package(
        &key.list_match(Some(&location)),
        &UninstallRequest::new(PackageQuery::default()),
        &mut warnings,
    );
    assert_eq!(result.expect("uninstall succeeds without --force"), 0);
    assert!(key.open().is_none(), "the stale ARP entry must be removed");

    // No install location at all behaves the same way.
    let key = TestArpKey::new("NoLocation");
    {
        let subkey = key.create();
        subkey
            .set_value("WinGetInstallerType", &"portable".to_owned())
            .expect("type");
    }
    let result = uninstall_package(
        &key.list_match(None),
        &UninstallRequest::new(PackageQuery::default()),
        &mut warnings,
    );
    assert_eq!(result.expect("uninstall succeeds without --force"), 0);
    assert!(key.open().is_none());
}

// ── W19-9: a failed fresh portable install is rolled back ──

/// A zip whose `tool.exe` extracts fine but whose `data.bin` is stored with a corrupt CRC,
/// so extraction fails after `tool.exe` has been written.
#[cfg(windows)]
fn write_corrupt_zip(path: &Path) {
    let file = fs::File::create(path).expect("create zip");
    let mut zip = zip::ZipWriter::new(file);
    let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);
    zip.start_file("tool.exe", options).expect("start tool");
    zip.write_all(b"tool binary").expect("write tool");
    zip.start_file("data.bin", options).expect("start data");
    zip.write_all(b"PINGET-WAVE19-DATA").expect("write data");
    zip.finish().expect("finish zip");

    let mut bytes = fs::read(path).expect("read zip");
    let marker = b"PINGET-WAVE19-DATA";
    let offset = bytes
        .windows(marker.len())
        .position(|window| window == marker)
        .expect("stored data");
    bytes[offset] ^= 0xFF;
    fs::write(path, bytes).expect("write corrupt zip");
}

#[cfg(windows)]
fn failing_install(temp: &TempDir, target: &Path) -> Result<i32> {
    let installer_path = temp.path.join("tool.zip");
    write_corrupt_zip(&installer_path);
    let mut request = InstallRequest::new(PackageQuery::default());
    request.install_location = Some(target.to_string_lossy().to_string());
    let manifest = test_manifest(&unique_name("Rollback"));
    let installer = zip_installer("portable", &["tool.exe"]);
    let mut warnings = Vec::new();
    install_portable(
        &installer_path,
        &request,
        &manifest,
        &installer,
        PortableScope::User,
        &mut warnings,
    )
}

#[cfg(windows)]
#[test]
fn failed_fresh_portable_install_removes_the_directory_it_created() {
    let temp = TempDir::new("RollbackNew");
    let target = temp.path.join("install");
    assert!(
        failing_install(&temp, &target).is_err(),
        "the corrupt archive must fail"
    );
    assert!(
        !target.exists(),
        "a failed fresh install must not leave a partial payload behind"
    );
}

#[cfg(windows)]
#[test]
fn failed_fresh_portable_install_into_existing_directory_keeps_other_files() {
    let temp = TempDir::new("RollbackExisting");
    let target = temp.path.join("install");
    fs::create_dir_all(&target).expect("create target");
    fs::write(target.join("keep.txt"), b"user data").expect("write user file");
    assert!(
        failing_install(&temp, &target).is_err(),
        "the corrupt archive must fail"
    );
    assert!(!target.join("tool.exe").exists(), "the partial payload must be removed");
    assert!(
        target.join("keep.txt").exists(),
        "files that were already there must survive"
    );
}
