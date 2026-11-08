//! Regression tests for the CLI / PowerShell / COM surface defect fixes (wave 11).

use super::*;

fn simple_manifest() -> Manifest {
    parse_yaml_manifest(
        br#"
PackageIdentifier: Contoso.Tool
PackageVersion: 1.2.3
PackageName: Contoso Tool
Publisher: Contoso
ManifestType: merged
ManifestVersion: 1.10.0
Installers:
  - InstallerType: msi
    InstallerUrl: https://example.test/setup.msi
    InstallerSha256: ABC123
"#,
    )
    .expect("parse manifest")
}

// ── R11-7: RebootRequired is carried on InstallResult ──

#[test]
fn completed_install_result_reports_reboot_required_for_msi_3010() {
    let manifest = simple_manifest();
    let installer = Installer::default();

    let result = completed_install_result(
        &manifest,
        &installer,
        PathBuf::from("setup.msi"),
        "msi".to_owned(),
        3010,
        Vec::new(),
    );

    assert!(result.success);
    assert!(result.reboot_required);
    assert!(!result.no_op);
    assert_eq!(result.package_id, "Contoso.Tool");
    assert_eq!(result.warnings, vec![INSTALL_REBOOT_REQUIRED_WARNING.to_owned()]);
}

#[test]
fn completed_install_result_has_no_reboot_for_plain_success_or_failure() {
    let manifest = simple_manifest();
    let installer = Installer::default();

    let ok = completed_install_result(&manifest, &installer, PathBuf::new(), "exe".to_owned(), 0, Vec::new());
    assert!(ok.success);
    assert!(!ok.reboot_required);
    assert!(ok.warnings.is_empty());

    let failed = completed_install_result(
        &manifest,
        &installer,
        PathBuf::new(),
        "exe".to_owned(),
        3010,
        Vec::new(),
    );
    assert!(!failed.success);
    assert!(!failed.reboot_required);
}

#[test]
fn install_result_serializes_reboot_required_for_com_clients() {
    let result = InstallResult {
        package_id: "Contoso.Tool".to_owned(),
        reboot_required: true,
        success: true,
        ..InstallResult::default()
    };

    let json = serde_json::to_value(&result).expect("serialize");
    assert_eq!(json["reboot_required"], serde_json::Value::Bool(true));
    assert_eq!(
        serde_json::to_value(InstallResult::default()).expect("serialize")["reboot_required"],
        false
    );
}
