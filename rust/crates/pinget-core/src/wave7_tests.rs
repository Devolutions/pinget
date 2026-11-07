//! Regression tests for the portable / archive / repair / process-launch defect fixes (wave 7).

#[cfg_attr(not(windows), allow(unused_imports))]
use std::io::Write;
use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

struct TempDir {
    path: PathBuf,
}

impl TempDir {
    fn new(label: &str) -> Self {
        static COUNTER: AtomicU64 = AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "pinget-wave7-{label}-{}-{}",
            std::process::id(),
            COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
        ));
        let _ = fs::remove_dir_all(&path);
        fs::create_dir_all(&path).expect("create temp dir");
        Self { path }
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

fn write_file(path: &Path, contents: &str) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("create parent");
    }
    fs::write(path, contents).expect("write file");
}

fn simple_manifest(id: &str, version: &str) -> Manifest {
    parse_yaml_manifest(
        format!(
            "PackageIdentifier: {id}\nPackageVersion: {version}\nPackageName: {id}\nPublisher: Contoso\nManifestType: merged\nManifestVersion: 1.10.0\nInstallers: []\n"
        )
        .as_bytes(),
    )
    .expect("parse manifest")
}

fn nested_file(path: &str, alias: Option<&str>) -> NestedInstallerFile {
    NestedInstallerFile {
        relative_file_path: path.to_owned(),
        portable_command_alias: alias.map(str::to_owned),
    }
}

fn list_match(installer_category: Option<&str>, product_codes: &[&str]) -> ListMatch {
    ListMatch {
        name: "Contoso App".to_owned(),
        id: "Contoso.App".to_owned(),
        local_id: r"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}".to_owned(),
        installed_version: "1.0.0".to_owned(),
        available_version: None,
        source_name: Some("winget".to_owned()),
        publisher: None,
        scope: Some("machine".to_owned()),
        installer_category: installer_category.map(str::to_owned),
        install_location: None,
        package_family_names: Vec::new(),
        product_codes: product_codes.iter().map(|code| (*code).to_owned()).collect(),
        upgrade_codes: Vec::new(),
    }
}

// ── R7-1: portable uninstall keeps user data unless --purge ──

#[cfg(windows)]
fn owned_portable_entry(
    location: &Path,
    files: Option<Vec<String>>,
    directories: Vec<String>,
) -> ExistingPortableEntry {
    ExistingPortableEntry {
        install_location: Some(location.to_path_buf()),
        install_directory_created: Some(1),
        portable_target_full_path: Some(location.join("tool.exe")),
        portable_symlink_full_path: None,
        files,
        directories: Some(directories),
        subkey_name: "Contoso.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe".to_owned(),
    }
}

#[cfg(windows)]
#[test]
fn portable_uninstall_without_purge_removes_only_tracked_files_and_keeps_user_data() {
    let temp = TempDir::new("uninstall-keep");
    let location = temp.path.join("Contoso.Tool");
    write_file(&location.join("tool.exe"), "binary");
    write_file(&location.join(r"lib\helper.dll"), "helper");
    write_file(&location.join("settings.ini"), "user settings");
    write_file(&location.join(r"data\profile.json"), "user data");
    let entry = owned_portable_entry(
        &location,
        Some(vec!["tool.exe".to_owned(), r"lib\helper.dll".to_owned()]),
        vec!["lib".to_owned()],
    );

    let mut warnings = Vec::new();
    remove_portable_directory(&location, Some(&entry), false, &mut warnings).expect("uninstall");

    assert!(!location.join("tool.exe").exists());
    assert!(
        !location.join("lib").exists(),
        "the tracked, now empty directory is removed"
    );
    assert_eq!(
        fs::read_to_string(location.join("settings.ini")).expect("user file survives"),
        "user settings"
    );
    assert!(location.join(r"data\profile.json").is_file());
    assert!(
        warnings.iter().any(|warning| warning.contains("--purge")),
        "remaining files are reported: {warnings:?}"
    );
}

#[cfg(windows)]
#[test]
fn portable_uninstall_removes_owned_directory_once_it_is_empty() {
    let temp = TempDir::new("uninstall-empty");
    let location = temp.path.join("Contoso.Tool");
    write_file(&location.join("tool.exe"), "binary");
    let entry = owned_portable_entry(&location, Some(vec!["tool.exe".to_owned()]), Vec::new());

    let mut warnings = Vec::new();
    remove_portable_directory(&location, Some(&entry), false, &mut warnings).expect("uninstall");

    assert!(!location.exists(), "an owned directory left empty is removed");
    assert!(warnings.is_empty(), "{warnings:?}");
}

#[cfg(windows)]
#[test]
fn portable_uninstall_with_purge_removes_the_owned_directory() {
    let temp = TempDir::new("uninstall-purge");
    let location = temp.path.join("Contoso.Tool");
    write_file(&location.join("tool.exe"), "binary");
    write_file(&location.join("settings.ini"), "user settings");
    let entry = owned_portable_entry(&location, Some(vec!["tool.exe".to_owned()]), Vec::new());

    let mut warnings = Vec::new();
    remove_portable_directory(&location, Some(&entry), true, &mut warnings).expect("purge");

    assert!(!location.exists());
}

#[cfg(windows)]
#[test]
fn portable_uninstall_of_untracked_owned_directory_removes_only_the_target() {
    let temp = TempDir::new("uninstall-legacy");
    let location = temp.path.join("Contoso.Tool");
    write_file(&location.join("tool.exe"), "binary");
    write_file(&location.join("settings.ini"), "user settings");
    let entry = owned_portable_entry(&location, None, Vec::new());

    let mut warnings = Vec::new();
    remove_portable_directory(&location, Some(&entry), false, &mut warnings).expect("uninstall");

    assert!(!location.join("tool.exe").exists());
    assert!(
        location.join("settings.ini").is_file(),
        "untracked files are not deleted"
    );
}

// ── R7-2: portable upgrade removes only the previous version's obsolete files ──

#[test]
fn portable_upgrade_removes_only_obsolete_tracked_files() {
    let temp = TempDir::new("upgrade");
    let target = temp.path.join("Contoso.Tool");
    write_file(&target.join("tool.exe"), "v2");
    write_file(&target.join(r"old\legacy.dll"), "v1 only");
    write_file(&target.join("config.json"), "user config");
    let payload = PortablePayload {
        files: vec!["tool.exe".to_owned()],
        directories: Vec::new(),
    };

    remove_obsolete_portable_files(
        &target,
        &payload,
        &["tool.exe".to_owned(), r"old\legacy.dll".to_owned()],
        &["old".to_owned()],
    )
    .expect("remove obsolete files");

    assert_eq!(fs::read_to_string(target.join("tool.exe")).expect("new binary"), "v2");
    assert!(
        !target.join("old").exists(),
        "obsolete tracked file and directory are removed"
    );
    assert_eq!(
        fs::read_to_string(target.join("config.json")).expect("user file survives the upgrade"),
        "user config"
    );
}

// ── R7-3: zip with a non-portable nested installer ──

#[test]
fn nested_archive_installer_target_resolves_type_and_path() {
    let installer = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("MSI".to_owned()),
        nested_installer_files: vec![nested_file("bin/setup.msi", None)],
        ..Installer::default()
    };
    let (nested_type, relative) = nested_archive_installer_target(&installer).expect("nested target");
    assert_eq!(nested_type, "msi");
    assert_eq!(relative, r"bin\setup.msi");
    assert_eq!(effective_installer_type(&installer, "zip"), "msi");
}

#[test]
fn nested_archive_installer_target_fails_without_nested_files_or_type() {
    let missing_files = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("exe".to_owned()),
        ..Installer::default()
    };
    let error = nested_archive_installer_target(&missing_files).expect_err("NestedInstallerFiles is required");
    assert!(error.to_string().contains("NestedInstallerFiles"), "{error}");

    let missing_type = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_files: vec![nested_file("setup.exe", None)],
        ..Installer::default()
    };
    let error = nested_archive_installer_target(&missing_type).expect_err("NestedInstallerType is required");
    assert!(error.to_string().contains("NestedInstallerType"), "{error}");

    let escaping = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("exe".to_owned()),
        nested_installer_files: vec![nested_file("../setup.exe", None)],
        ..Installer::default()
    };
    assert!(nested_archive_installer_target(&escaping).is_err());

    let nested_zip = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("zip".to_owned()),
        nested_installer_files: vec![nested_file("inner.zip", None)],
        ..Installer::default()
    };
    assert!(nested_archive_installer_target(&nested_zip).is_err());
}

#[test]
fn effective_installer_type_keeps_portable_zip_and_other_types() {
    let portable_zip = Installer {
        nested_installer_type: Some("portable".to_owned()),
        ..Installer::default()
    };
    assert_eq!(effective_installer_type(&portable_zip, "zip"), "zip");
    assert_eq!(effective_installer_type(&Installer::default(), "exe"), "exe");
}

#[cfg(windows)]
fn write_zip(path: &Path, entries: &[(&str, &[u8])]) {
    let file = fs::File::create(path).expect("create zip");
    let mut writer = zip::ZipWriter::new(file);
    for (name, contents) in entries {
        writer
            .start_file(*name, zip::write::SimpleFileOptions::default())
            .expect("start zip entry");
        writer.write_all(contents).expect("write zip entry");
    }
    writer.finish().expect("finish zip");
}

#[cfg(windows)]
#[test]
fn nested_archive_installer_fails_cleanly_when_the_nested_file_is_missing() {
    let temp = TempDir::new("nested-missing");
    let archive = temp.path.join("package.zip");
    write_zip(&archive, &[("readme.txt", b"no installer here")]);
    let installer = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("exe".to_owned()),
        nested_installer_files: vec![nested_file("setup.exe", None)],
        ..Installer::default()
    };
    let manifest = simple_manifest("Contoso.Zipped", "1.0.0");
    let request = InstallRequest::new(PackageQuery::default());
    let extract_root = std::env::temp_dir().join("pinget-install");
    let before: HashSet<PathBuf> = fs::read_dir(&extract_root)
        .map(|entries| {
            entries
                .filter_map(|entry| entry.ok().map(|entry| entry.path()))
                .collect()
        })
        .unwrap_or_default();

    let mut warnings = Vec::new();
    let error = install_nested_archive_installer(&archive, &request, &manifest, &installer, &mut warnings)
        .expect_err("a missing nested installer must not report success");

    assert!(error.to_string().contains("not present"), "{error:#}");
    let leaked: Vec<PathBuf> = fs::read_dir(&extract_root)
        .map(|entries| {
            entries
                .filter_map(|entry| entry.ok().map(|entry| entry.path()))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default()
        .into_iter()
        .filter(|path: &PathBuf| {
            !before.contains(path)
                && path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.starts_with(&format!("extracted-{}-", std::process::id())))
        })
        .collect();
    assert!(
        leaked.is_empty(),
        "the temporary extraction directory is removed: {leaked:?}"
    );
}

// ── R7-4: system tools are launched from System32 by absolute path ──

#[cfg(windows)]
#[test]
fn system_tools_resolve_to_absolute_system32_paths() {
    let system_root = PathBuf::from(std::env::var_os("SystemRoot").expect("SystemRoot is set"));
    for (tool, expected) in [
        (MSIEXEC_EXE, r"System32\msiexec.exe"),
        (CMD_EXE, r"System32\cmd.exe"),
        (
            WINDOWS_POWERSHELL_EXE,
            r"System32\WindowsPowerShell\v1.0\powershell.exe",
        ),
    ] {
        let path = system_tool_path(tool).expect("resolve system tool");
        assert!(path.is_absolute(), "{}", path.display());
        assert_eq!(
            path.to_string_lossy().to_lowercase(),
            system_root.join(expected).to_string_lossy().to_lowercase()
        );
        assert!(path.is_file(), "{} exists", path.display());
    }
}

// ── R7-6: MSI repair uses msiexec /f instead of a no-op reinstall ──

#[test]
fn msi_repair_uses_the_installed_product_code() {
    let installed = list_match(Some("msi"), &["Contoso.App", "{11111111-2222-3333-4444-555555555555}"]);
    assert_eq!(
        msi_repair_product_code(&installed).as_deref(),
        Some("{11111111-2222-3333-4444-555555555555}")
    );
    assert_eq!(
        msi_repair_product_code(&list_match(Some("exe"), &["{11111111-2222-3333-4444-555555555555}"])),
        None,
        "non-MSI entries keep the reinstall fallback"
    );
    assert_eq!(
        msi_repair_product_code(&list_match(Some("msi"), &["Contoso.App"])),
        None
    );
}

#[test]
fn msi_repair_arguments_match_winget() {
    let code = "{11111111-2222-3333-4444-555555555555}";
    assert_eq!(
        build_msi_repair_arguments(code, InstallerMode::Silent, None),
        vec!["/f", code, "/quiet", "/norestart"]
    );
    assert_eq!(
        build_msi_repair_arguments(code, InstallerMode::SilentWithProgress, None),
        vec!["/f", code, "/passive", "/norestart"]
    );
    assert_eq!(
        build_msi_repair_arguments(code, InstallerMode::Interactive, Some(Path::new(r"C:\logs\repair.log"))),
        vec!["/f", code, "/log", r"C:\logs\repair.log"]
    );
}

// ── R7-7: the install flow downloads the installer it already selected ──

#[test]
fn fetch_installer_uses_the_supplied_installer_without_resolving_again() {
    let temp = TempDir::new("fetch");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(temp.path.clone())).expect("open repository");
    let request = InstallRequest::new(PackageQuery {
        id: Some("Not.In.Any.Source".to_owned()),
        exact: true,
        ..PackageQuery::default()
    });
    let manifest = simple_manifest("Contoso.Selected", "2.0.0");
    let installer = Installer {
        installer_type: Some("exe".to_owned()),
        url: None,
        ..Installer::default()
    };

    let error = repository
        .fetch_installer_for_request(&request, &manifest, &installer, None, &temp.path.join("downloads"))
        .expect_err("an installer without a URL cannot be fetched");
    assert!(
        error.to_string().contains("Installer has no URL"),
        "the supplied installer is used, not a fresh lookup: {error:#}"
    );
}

// ── R7-8: every nested portable file gets a command link ──

#[test]
fn portable_links_cover_every_nested_file() {
    let installer = Installer {
        installer_type: Some("zip".to_owned()),
        nested_installer_type: Some("portable".to_owned()),
        nested_installer_files: vec![
            nested_file("bin/foo.exe", None),
            nested_file("bin/foo-cli.exe", Some("fcli")),
        ],
        ..Installer::default()
    };
    let links = determine_portable_links(&installer, true, None, None).expect("links");
    assert_eq!(
        links,
        vec![
            PortableCommandLink {
                alias: "foo".to_owned(),
                relative_path: Some(r"bin\foo.exe".to_owned()),
            },
            PortableCommandLink {
                alias: "fcli".to_owned(),
                relative_path: Some(r"bin\foo-cli.exe".to_owned()),
            },
        ]
    );

    let escaping = Installer {
        nested_installer_files: vec![nested_file("bin/foo.exe", None), nested_file("../evil.exe", None)],
        ..installer
    };
    assert!(
        determine_portable_links(&escaping, true, None, None).is_err(),
        "every nested path is validated, not just the first"
    );
}

// ── R7-9: standalone portable alias falls back to the file name; --rename names the alias ──

#[test]
fn standalone_portable_link_uses_rename_then_command_then_file_name() {
    let no_commands = Installer {
        installer_type: Some("portable".to_owned()),
        ..Installer::default()
    };
    let alias = |installer: &Installer, rename: Option<&str>| {
        determine_portable_links(installer, false, rename, Some("tool.exe"))
            .expect("links")
            .into_iter()
            .map(|link| (link.alias, link.relative_path))
            .collect::<Vec<_>>()
    };
    assert_eq!(alias(&no_commands, None), vec![("tool".to_owned(), None)]);
    assert_eq!(alias(&no_commands, Some("t2")), vec![("t2".to_owned(), None)]);
    assert_eq!(alias(&no_commands, Some("t2.EXE")), vec![("t2".to_owned(), None)]);

    let with_command = Installer {
        commands: vec!["toolctl".to_owned()],
        ..no_commands
    };
    assert_eq!(alias(&with_command, None), vec![("toolctl".to_owned(), None)]);
    assert_eq!(alias(&with_command, Some("t2")), vec![("t2".to_owned(), None)]);
}

#[test]
fn portable_install_downloads_keep_their_original_name() {
    let mut request = InstallRequest::new(PackageQuery::default());
    request.rename = Some("t2".to_owned());
    assert_eq!(install_download_rename(&request, "portable"), None);
    assert_eq!(install_download_rename(&request, "exe"), Some("t2"));
}

// ── R7-12: machine-scope x86 portables use the x86 root and registry view ──

#[test]
fn x86_machine_portables_use_the_x86_layout() {
    let x86 = Installer {
        architecture: Some("x86".to_owned()),
        ..Installer::default()
    };
    let x64 = Installer {
        architecture: Some("x64".to_owned()),
        ..Installer::default()
    };
    assert!(portable_uses_x86_machine_layout(PortableScope::Machine, &x86));
    assert!(!portable_uses_x86_machine_layout(PortableScope::User, &x86));
    assert!(!portable_uses_x86_machine_layout(PortableScope::Machine, &x64));
}

#[cfg(windows)]
#[test]
fn x86_machine_portable_root_is_under_program_files_x86() {
    let x86_root = default_portable_root(PortableScope::Machine, true).expect("x86 root");
    let x64_root = default_portable_root(PortableScope::Machine, false).expect("x64 root");
    assert_eq!(
        x86_root,
        program_files_x86_dir()
            .expect("Program Files (x86)")
            .join("WinGet")
            .join("Packages")
    );
    assert_eq!(
        x64_root,
        program_files_dir()
            .expect("Program Files")
            .join("WinGet")
            .join("Packages")
    );
    assert_ne!(x86_root, x64_root);
}

// ── R7-15: no guessed switches for generic exe installers ──

#[test]
fn generic_exe_installers_get_no_default_switches() {
    let manifest = simple_manifest("Contoso.Exe", "1.0.0");
    let installer = Installer::default();
    for mode in [InstallerMode::Silent, InstallerMode::SilentWithProgress] {
        let mut request = InstallRequest::new(PackageQuery::default());
        request.mode = mode;
        assert!(
            installer_command_arguments("exe", &request, &manifest, Path::new("setup.exe"), &installer).is_empty(),
            "{mode:?}"
        );
        assert!(
            !installer_command_arguments("inno", &request, &manifest, Path::new("setup.exe"), &installer).is_empty(),
            "known installer types keep their defaults"
        );
    }
}
