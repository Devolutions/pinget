//! Wave 13 regression tests: installer execution and dispatch.

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

fn temp_dir(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let path = std::env::temp_dir().join(format!(
        "pinget-rs-wave13-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ));
    fs::create_dir_all(&path).expect("create temp dir");
    path
}

fn manifest() -> Manifest {
    parse_yaml_manifest(
        b"PackageIdentifier: Contoso.App
PackageVersion: 2.0.0
PackageName: Contoso App
Publisher: Contoso
ManifestType: merged
ManifestVersion: 1.10.0
Installers: []
",
    )
    .expect("parse manifest")
}

fn request(mode: InstallerMode) -> InstallRequest {
    let mut request = InstallRequest::new(PackageQuery::default());
    request.mode = mode;
    request
}

fn installer_with_switches(switches: InstallerSwitches) -> Installer {
    Installer {
        switches,
        ..Installer::default()
    }
}

fn args(installer_type: &str, request: &InstallRequest, installer: &Installer) -> Vec<String> {
    installer_direct_msi_arguments(installer_type, request, &manifest(), Path::new("setup.bin"), installer)
}

#[cfg(windows)]
fn list_match(name: &str, id: &str, local_id: &str) -> ListMatch {
    ListMatch {
        name: name.to_owned(),
        id: id.to_owned(),
        local_id: local_id.to_owned(),
        installed_version: "1.0.0".to_owned(),
        available_version: None,
        source_name: Some("winget".to_owned()),
        publisher: None,
        scope: None,
        installer_category: None,
        install_location: None,
        package_family_names: Vec::new(),
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
    }
}

// R13-1: an interactive uninstall falls back to QuietUninstallString when UninstallString is
// missing (WinGet GetUninstallInfo), instead of failing with "No uninstall command found".
#[test]
fn interactive_uninstall_falls_back_to_the_quiet_command() {
    assert_eq!(
        select_arp_uninstall_command(InstallerMode::Interactive, Some("unins.exe /SILENT".to_owned()), None),
        Some(("unins.exe /SILENT".to_owned(), true))
    );
    assert_eq!(
        select_arp_uninstall_command(
            InstallerMode::Interactive,
            Some("unins.exe /SILENT".to_owned()),
            Some("unins.exe".to_owned())
        ),
        Some(("unins.exe".to_owned(), false))
    );
    assert_eq!(
        select_arp_uninstall_command(
            InstallerMode::Silent,
            Some("unins.exe /SILENT".to_owned()),
            Some("unins.exe".to_owned())
        ),
        Some(("unins.exe /SILENT".to_owned(), true))
    );
}

// R13-2: a blank QuietUninstallString / UninstallString is not run (cmd.exe would exit 0 and the
// uninstall would be reported as successful without removing anything).
#[test]
fn blank_uninstall_strings_are_ignored() {
    assert_eq!(
        select_arp_uninstall_command(
            InstallerMode::Silent,
            Some("   ".to_owned()),
            Some("unins.exe".to_owned())
        ),
        Some(("unins.exe".to_owned(), false))
    );
    assert_eq!(
        select_arp_uninstall_command(
            InstallerMode::Interactive,
            Some("unins.exe /S".to_owned()),
            Some(String::new())
        ),
        Some(("unins.exe /S".to_owned(), true))
    );
    assert_eq!(
        select_arp_uninstall_command(
            InstallerMode::SilentWithProgress,
            Some(String::new()),
            Some(" ".to_owned())
        ),
        None
    );
}

// R13-3: direct MSI execution accepts the property names WinGet's ParsePropertyToken accepts,
// such as dotted names, instead of failing the silent install.
#[test]
fn direct_msi_accepts_dotted_property_names() {
    let parsed = parse_msi_direct_arguments(&[
        "/quiet".to_owned(),
        "MY.PROPERTY=1".to_owned(),
        "_PRIVATE=2".to_owned(),
        "INSTALL.DIR=C:\\Program Files\\Contoso".to_owned(),
    ])
    .expect("dotted MSI property names are valid");
    assert_eq!(
        parsed.properties,
        "MY.PROPERTY=1 _PRIVATE=2 INSTALL.DIR=\"C:\\Program Files\\Contoso\""
    );
    assert!(parse_msi_direct_arguments(&[".BAD=1".to_owned()]).is_err());
    assert!(parse_msi_direct_arguments(&["=1".to_owned()]).is_err());
}

// R13-4: the requested mode's switch comes from the manifest, then from the installer type's
// defaults (WinGet merges them per switch kind); the other mode's manifest switch is only a
// fallback for types without defaults.
#[test]
fn requested_mode_uses_type_default_before_the_other_mode_switch() {
    // MSI declaring only SilentWithProgress: --silent must still be fully silent.
    let msi = installer_with_switches(InstallerSwitches {
        silent_with_progress: Some("/passive".to_owned()),
        ..InstallerSwitches::default()
    });
    assert_eq!(
        args("msi", &request(InstallerMode::Silent), &msi),
        vec!["/quiet", "/norestart"]
    );
    assert_eq!(
        args("msi", &request(InstallerMode::SilentWithProgress), &msi),
        vec!["/passive"]
    );

    // Inno declaring only Silent: the default (silent-with-progress) mode shows progress.
    let inno = installer_with_switches(InstallerSwitches {
        silent: Some("/VERYSILENT".to_owned()),
        ..InstallerSwitches::default()
    });
    assert_eq!(
        args("inno", &request(InstallerMode::SilentWithProgress), &inno),
        vec!["/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART"]
    );

    // A generic exe has no defaults, so the other mode's switch is still used.
    let exe = installer_with_switches(InstallerSwitches {
        silent: Some("--quiet".to_owned()),
        ..InstallerSwitches::default()
    });
    assert_eq!(
        args("exe", &request(InstallerMode::SilentWithProgress), &exe),
        vec!["--quiet"]
    );
}

// R13-5: a delegated `winget uninstall` honours an interactive request.
#[cfg(windows)]
#[test]
fn delegated_winget_uninstall_passes_interactive() {
    let installed = list_match("Contoso App", "Contoso.App", r"ARP\Machine\X64\Contoso.App_is1");
    let mut uninstall = UninstallRequest::new(PackageQuery::default());

    uninstall.mode = InstallerMode::Interactive;
    let args = build_winget_uninstall_arguments(&installed, &uninstall).expect("delegated arguments");
    assert!(args.contains(&"--interactive".to_owned()), "{args:?}");
    assert!(!args.contains(&"--silent".to_owned()), "{args:?}");

    uninstall.mode = InstallerMode::Silent;
    let args = build_winget_uninstall_arguments(&installed, &uninstall).expect("delegated arguments");
    assert!(args.contains(&"--silent".to_owned()), "{args:?}");
    assert!(!args.contains(&"--interactive".to_owned()), "{args:?}");

    uninstall.mode = InstallerMode::SilentWithProgress;
    let args = build_winget_uninstall_arguments(&installed, &uninstall).expect("delegated arguments");
    assert!(!args.contains(&"--silent".to_owned()) && !args.contains(&"--interactive".to_owned()));
}

// R13-6: --uninstall-previous only applies to an update, like WinGet; a same-version reinstall
// does not remove the installed package first.
#[test]
fn uninstall_previous_only_applies_to_updates() {
    let mut install = request(InstallerMode::Silent);
    install.uninstall_previous = true;
    let installer = Installer::default();
    assert!(!uninstalls_previous_version(&install, &installer, false));
    assert!(uninstalls_previous_version(&install, &installer, true));

    let behavior = Installer {
        upgrade_behavior: Some("uninstallPrevious".to_owned()),
        ..Installer::default()
    };
    let plain = request(InstallerMode::Silent);
    assert!(!uninstalls_previous_version(&plain, &behavior, false));
    assert!(uninstalls_previous_version(&plain, &behavior, true));
    assert!(!uninstalls_previous_version(&plain, &installer, true));
}

// R13-7: once the previous version is uninstalled, the installer runs as a fresh install and
// the Upgrade switch is not appended (WinGet clears InstallerExecutionUseUpdate).
#[test]
fn upgrade_switch_is_dropped_after_uninstalling_the_previous_version() {
    assert!(upgrade_switch_applies(true, false));
    assert!(!upgrade_switch_applies(true, true));
    assert!(!upgrade_switch_applies(false, false));
}

// R13-8: the downloaded installer is removed after a successful run (WinGet RemoveInstaller) and
// kept after a failure.
#[test]
fn downloaded_installer_is_removed_after_success_only() {
    let root = temp_dir("remove-installer");
    let installer_path = root.join("setup.exe");
    let result = |success: bool| InstallResult {
        package_id: "Contoso.App".to_owned(),
        version: "2.0.0".to_owned(),
        installer_path: installer_path.clone(),
        installer_type: "exe".to_owned(),
        exit_code: i32::from(!success),
        success,
        no_op: false,
        reboot_required: false,
        warnings: Vec::new(),
    };

    fs::write(&installer_path, b"installer").expect("write installer");
    let mut failed = result(false);
    remove_installer_after_success(&mut failed);
    assert!(installer_path.exists(), "a failed installer is kept");

    let mut succeeded = result(true);
    remove_installer_after_success(&mut succeeded);
    assert!(!installer_path.exists(), "a successful installer is removed");
    assert!(succeeded.warnings.is_empty());

    // Already gone (e.g. a portable that consumed it): no warning.
    let mut again = result(true);
    remove_installer_after_success(&mut again);
    assert!(again.warnings.is_empty());

    let _ = fs::remove_dir_all(root);
}

// R13-9: the InstallLocation switch is only added when --location is supplied (WinGet), even
// when the manifest's switch has no <INSTALLPATH> token.
#[test]
fn install_location_switch_requires_a_location() {
    let installer = installer_with_switches(InstallerSwitches {
        silent: Some("/S".to_owned()),
        install_location: Some("/ALLUSERS".to_owned()),
        ..InstallerSwitches::default()
    });
    let mut install = request(InstallerMode::Silent);
    assert_eq!(args("exe", &install, &installer), vec!["/S"]);

    install.install_location = Some(r"C:\Apps\Contoso".to_owned());
    assert_eq!(args("exe", &install, &installer), vec!["/S", "/ALLUSERS"]);

    let nsis = Installer::default();
    assert_eq!(
        args("nullsoft", &install, &nsis),
        vec!["/S".to_owned(), r"/D=C:\Apps\Contoso".to_owned()]
    );
}

// R13-10: uninstalling an MSIX package never runs the uninstaller of a same-named ARP entry.
#[cfg(windows)]
#[test]
fn msix_packages_do_not_match_arp_entries_by_name() {
    let msix = list_match(
        "Windows Terminal",
        "Microsoft.WindowsTerminal",
        r"MSIX\Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe",
    );
    assert!(!registry_entry_matches_installed_package(
        "Windows Terminal",
        "Windows Terminal",
        None,
        &msix
    ));

    let other = list_match("Windows Terminal", "Contoso.Terminal", "Contoso.Terminal");
    assert!(registry_entry_matches_installed_package(
        "Windows Terminal",
        "Windows Terminal",
        None,
        &other
    ));
}
