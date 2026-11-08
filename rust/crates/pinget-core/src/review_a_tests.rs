//! Review fixes A: manifest scalar text and BOMs, `--rename` on install downloads.

use super::*;

const SINGLETON: &str = "PackageIdentifier: Test.ReviewA
PackageVersion: 1.10
PackageName: Test ReviewA
Publisher: Example
Tags: [1.10, cli]
ManifestType: singleton
ManifestVersion: 1.10.0
InstallerType: exe
Installers:
  - Architecture: x64
    InstallerUrl: https://example.test/setup.exe
    InstallerSha256: AAA
    MinimumOSVersion: 10.0
    InstallerSuccessCodes: [3010]
    Dependencies:
      PackageDependencies:
        - PackageIdentifier: Microsoft.VCRedist.2015+.x64
          MinimumVersion: 14.30
    AppsAndFeaturesEntries:
      - DisplayVersion: 1.10
";

// ── #1: the structured document keeps scalar source text, like the model and C# ──

#[test]
fn structured_document_keeps_numeric_looking_scalars_as_text() {
    let (manifest, documents) = parse_yaml_manifest_bundle(SINGLETON.as_bytes()).expect("parse");
    assert_eq!(manifest.version, "1.10");

    assert_eq!(documents["PackageVersion"], "1.10");
    assert_eq!(documents["Tags"], serde_json::json!(["1.10", "cli"]));
    let installer = &documents["Installers"][0];
    assert_eq!(installer["MinimumOSVersion"], "10.0");
    assert_eq!(
        installer["Dependencies"]["PackageDependencies"][0]["MinimumVersion"],
        "14.30"
    );
    assert_eq!(installer["AppsAndFeaturesEntries"][0]["DisplayVersion"], "1.10");
}

#[test]
fn download_manifest_keeps_numeric_looking_scalars_as_text() {
    let (manifest, documents) = parse_yaml_manifest_bundle(SINGLETON.as_bytes()).expect("parse");
    let rendered = build_download_manifest_yaml(&manifest, &documents, &manifest.installers[0]).expect("render");
    assert!(rendered.contains("'14.30'"), "{rendered}");
    assert!(!rendered.contains("14.3\n"), "{rendered}");

    let reparsed = parse_yaml_manifest(rendered.as_bytes()).expect("reparse");
    let installer = &reparsed.installers[0];
    assert_eq!(
        installer.dependencies.package_dependencies[0]
            .minimum_version
            .as_deref(),
        Some("14.30")
    );
    assert_eq!(
        installer.apps_and_features_entries[0].display_version.as_deref(),
        Some("1.10")
    );
    assert_eq!(installer.minimum_os_version.as_deref(), Some("10.0"));
    assert_eq!(installer.installer_success_codes, vec![3010]);
    assert_eq!(reparsed.tags, vec!["1.10".to_owned(), "cli".to_owned()]);
}

// ── #1: a UTF-8 BOM does not split a singleton into two documents ──

#[test]
fn bom_prefixed_singleton_keeps_its_structured_document() {
    let mut bytes = b"\xEF\xBB\xBF".to_vec();
    bytes.extend_from_slice(SINGLETON.as_bytes());
    let (manifest, documents) = parse_yaml_manifest_bundle(&bytes).expect("parse with BOM");
    let (_, expected) = parse_yaml_manifest_bundle(SINGLETON.as_bytes()).expect("parse");

    assert_eq!(manifest.id, "Test.ReviewA");
    assert_eq!(manifest.name, "Test ReviewA");
    assert_eq!(documents, expected);
    assert_eq!(documents["PackageName"], "Test ReviewA");
}

#[test]
fn bom_prefixed_multi_file_parts_merge() {
    let version = b"\xEF\xBB\xBFPackageIdentifier: Test.ReviewA\nPackageVersion: 2.0\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.10.0\n";
    let locale = b"\xEF\xBB\xBFPackageIdentifier: Test.ReviewA\nPackageVersion: 2.0\nPackageLocale: en-US\nPackageName: Test ReviewA\nPublisher: Example\nManifestType: defaultLocale\nManifestVersion: 1.10.0\n";
    let installer = b"\xEF\xBB\xBFPackageIdentifier: Test.ReviewA\nPackageVersion: 2.0\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n    InstallerUrl: https://example.test/setup.exe\n    InstallerSha256: AAA\nManifestType: installer\nManifestVersion: 1.10.0\n";
    let parts: [&[u8]; 3] = [version, locale, installer];
    let (manifest, _) = parse_yaml_manifest_bundle_parts(&parts).expect("parse parts");
    assert_eq!(manifest.version, "2.0");
    assert_eq!(manifest.name, "Test ReviewA");
    assert_eq!(manifest.installers.len(), 1);
}

// ── #5: a --rename download name keeps the installer type's extension ──

#[test]
fn renamed_download_gets_the_installer_type_extension() {
    let manifest = parse_yaml_manifest(SINGLETON.as_bytes()).expect("parse");
    let name = |rename: &str, installer_type: &str| {
        installer_download_file_name(
            "https://example.test/setup.bin",
            Some(rename),
            installer_type,
            &manifest,
        )
        .expect("file name")
    };
    assert_eq!(name("mytool", "exe"), "mytool.exe");
    assert_eq!(name("mytool", "inno"), "mytool.exe");
    assert_eq!(name("mytool", "msi"), "mytool.msi");
    assert_eq!(name("mytool", "msix"), "mytool.msix");
    assert_eq!(name("mytool.appxbundle", "msix"), "mytool.appxbundle");
    assert_eq!(name("custom.EXE", "exe"), "custom.EXE");
    assert_eq!(name("mytool", "font"), "mytool");
}

// ── #8: an explicit empty installer-level list overrides the root default (C# aligned to this) ──

#[test]
fn explicit_empty_installer_lists_override_root_defaults() {
    let manifest = parse_yaml_manifest(
        b"PackageIdentifier: Test.ReviewA
PackageVersion: 1.0
PackageName: Test ReviewA
Publisher: Example
ManifestType: singleton
ManifestVersion: 1.10.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: tool.exe
Platform: [Windows.Desktop]
AppsAndFeaturesEntries:
  - DisplayName: Root
Installers:
  - Architecture: x64
    InstallerUrl: https://example.test/x64.zip
    InstallerSha256: AAA
    NestedInstallerFiles: []
    Platform: []
    AppsAndFeaturesEntries: []
  - Architecture: arm64
    InstallerUrl: https://example.test/arm64.zip
    InstallerSha256: BBB
",
    )
    .expect("parse");
    let overriding = &manifest.installers[0];
    assert!(overriding.nested_installer_files.is_empty());
    assert!(overriding.platforms.is_empty());
    assert!(overriding.apps_and_features_entries.is_empty());

    let inheriting = &manifest.installers[1];
    assert_eq!(inheriting.nested_installer_files.len(), 1);
    assert_eq!(inheriting.platforms, vec!["Windows.Desktop".to_owned()]);
    assert_eq!(inheriting.apps_and_features_entries.len(), 1);
}
