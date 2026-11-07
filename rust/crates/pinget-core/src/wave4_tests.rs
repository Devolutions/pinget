//! Wave 4 regression tests: manifests, installer selection, dependencies and versions.

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

fn temp_dir(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let path = std::env::temp_dir().join(format!(
        "pinget-rs-wave4-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ));
    fs::create_dir_all(&path).expect("create temp dir");
    path
}

const VERSION_YAML: &str = "PackageIdentifier: Test.Multi
PackageVersion: 1.2.3
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
";

const DEFAULT_LOCALE_YAML: &str = "PackageIdentifier: Test.Multi
PackageVersion: 1.2.3
PackageLocale: en-US
Publisher: Example
PackageName: Test Multi
ShortDescription: English description
ManifestType: defaultLocale
ManifestVersion: 1.10.0
";

const FRENCH_LOCALE_YAML: &str = "PackageIdentifier: Test.Multi
PackageVersion: 1.2.3
PackageLocale: fr-FR
Publisher: Exemple
PackageName: Test Multi FR
ShortDescription: Description francaise
ManifestType: locale
ManifestVersion: 1.10.0
";

const INSTALLER_YAML: &str = "PackageIdentifier: Test.Multi
PackageVersion: 1.2.3
InstallerType: exe
Installers:
  - Architecture: x64
    InstallerUrl: https://example.test/Test.Multi.exe
    InstallerSha256: ABC123
ManifestType: installer
ManifestVersion: 1.10.0
";

fn write_multi_file_manifest(dir: &Path) {
    fs::write(dir.join("Test.Multi.installer.yaml"), INSTALLER_YAML).expect("write installer");
    fs::write(dir.join("Test.Multi.locale.en-US.yaml"), DEFAULT_LOCALE_YAML).expect("write defaultLocale");
    // Sorts after en-US: before the fix its fields would win a last-wins merge.
    fs::write(dir.join("Test.Multi.locale.fr-FR.yaml"), FRENCH_LOCALE_YAML).expect("write locale");
    fs::write(dir.join("Test.Multi.yaml"), VERSION_YAML).expect("write version");
}

fn private_repository(root: &Path) -> Repository {
    let options = RepositoryOptions::new(root.to_path_buf()).with_source_mode(SourceMode::Private);
    let mut repository = Repository::open_with_options(options).expect("open repository");
    repository
        .set_admin_setting("LocalManifestFiles", true)
        .expect("enable local manifests");
    // No sources: a dependency lookup fails fast instead of reaching the network.
    for source in repository.list_sources() {
        repository.remove_source(&source.name).expect("remove source");
    }
    repository
}

// V1: `--manifest <dir>` loads every file of a multi-file manifest.
#[test]
fn manifest_directory_loads_all_multi_file_parts() {
    let root = temp_dir("multi_file_manifest");
    let manifest_dir = root.join("manifest");
    fs::create_dir_all(&manifest_dir).expect("create manifest dir");
    write_multi_file_manifest(&manifest_dir);

    assert_eq!(resolve_manifest_paths(&manifest_dir).expect("resolve").len(), 4);

    let repository = private_repository(&root);
    let (manifest, documents) = repository
        .load_manifest_from_path_with_documents(&manifest_dir)
        .expect("load multi-file manifest");

    assert_eq!(manifest.id, "Test.Multi");
    assert_eq!(manifest.version, "1.2.3");
    // The non-default fr-FR locale must not override the default-locale root fields.
    assert_eq!(manifest.name, "Test Multi");
    assert_eq!(manifest.publisher.as_deref(), Some("Example"));
    assert_eq!(manifest.installers.len(), 1);
    assert_eq!(manifest.installers[0].installer_type.as_deref(), Some("exe"));
    assert_eq!(
        manifest.installers[0].url.as_deref(),
        Some("https://example.test/Test.Multi.exe")
    );

    assert_eq!(documents["ManifestType"].as_str(), Some("singleton"));
    assert_eq!(documents["PackageName"].as_str(), Some("Test Multi"));
    assert!(documents["Installers"].is_array());

    // A single-file path keeps working.
    let single = root.join("single.yaml");
    fs::write(
        &single,
        "PackageIdentifier: Test.Single
PackageVersion: 1.0.0
PackageName: Test Single
ManifestType: singleton
ManifestVersion: 1.10.0
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Single.exe
    InstallerSha256: ABC123
",
    )
    .expect("write singleton");
    let (manifest, _) = repository
        .load_manifest_from_path_with_documents(&single)
        .expect("load singleton manifest");
    assert_eq!(manifest.id, "Test.Single");
    assert_eq!(manifest.installers.len(), 1);

    let _ = fs::remove_dir_all(root);
}

// V1: a `locale` document never overrides the root, even inside one multi-document stream.
#[test]
fn non_default_locale_document_does_not_override_root_fields() {
    let yaml = format!("{VERSION_YAML}---\n{DEFAULT_LOCALE_YAML}---\n{FRENCH_LOCALE_YAML}---\n{INSTALLER_YAML}");
    let manifest = parse_yaml_manifest(yaml.as_bytes()).expect("parse");
    assert_eq!(manifest.name, "Test Multi");
    assert_eq!(manifest.description.as_deref(), Some("English description"));
}

// V6: a singleton manifest's structured document keeps all of its content.
#[test]
fn singleton_manifest_structured_document_keeps_content() {
    let yaml = "PackageIdentifier: Test.Single
PackageVersion: 1.0.0
PackageName: Test Single
Publisher: Example
License: MIT
ManifestType: singleton
ManifestVersion: 1.10.0
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/Test.Single.exe
    InstallerSha256: ABC123
";
    let (_, document) = parse_yaml_manifest_bundle(yaml.as_bytes()).expect("parse");
    assert_eq!(document["ManifestType"].as_str(), Some("singleton"));
    assert_eq!(document["PackageIdentifier"].as_str(), Some("Test.Single"));
    assert_eq!(document["PackageName"].as_str(), Some("Test Single"));
    assert_eq!(document["License"].as_str(), Some("MIT"));
    assert_eq!(document["Installers"].as_array().map(Vec::len), Some(1));
}

// V3: dependencies come from the selected installer (root fallback), deduped case-insensitively.
#[test]
fn selected_installer_dependencies_fall_back_to_root_and_dedupe() {
    let manifest = parse_yaml_manifest(
        b"PackageIdentifier: Test.Deps
PackageVersion: 1.0.0
PackageName: Test Deps
ManifestType: singleton
ManifestVersion: 1.10.0
Dependencies:
  PackageDependencies:
    - PackageIdentifier: Root.Dep
    - PackageIdentifier: root.dep
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/x64.exe
    InstallerSha256: ABC123
  - Architecture: arm64
    InstallerType: exe
    InstallerUrl: https://example.test/arm64.exe
    InstallerSha256: ABC123
    Dependencies:
      PackageDependencies:
        - PackageIdentifier: Arm.Dep
        - PackageIdentifier: ARM.DEP
        - PackageIdentifier: Other.Dep
",
    )
    .expect("parse");

    assert_eq!(
        selected_installer_package_dependencies(&manifest, &manifest.installers[0]),
        vec!["Root.Dep".to_owned()]
    );
    assert_eq!(
        selected_installer_package_dependencies(&manifest, &manifest.installers[1]),
        vec!["Arm.Dep".to_owned(), "Other.Dep".to_owned()]
    );
}

#[cfg_attr(not(windows), allow(dead_code))]
const DEPENDENCY_MANIFEST_HEADER: &str = "PackageIdentifier: Test.DepRoot
PackageVersion: 1.0.0
PackageName: Test Dep Root
ManifestType: singleton
ManifestVersion: 1.10.0
";

#[cfg(windows)]
fn install_from_manifest(
    label: &str,
    yaml: &str,
    configure: impl FnOnce(&mut InstallRequest),
) -> Result<InstallResult> {
    let root = temp_dir(label);
    let manifest_path = root.join("manifest.yaml");
    fs::write(&manifest_path, yaml).expect("write manifest");
    let mut repository = private_repository(&root);
    let mut request = InstallRequest::new(PackageQuery::default());
    request.manifest_path = Some(manifest_path);
    configure(&mut request);
    let result = repository.install_request(&request);
    let _ = fs::remove_dir_all(root);
    result
}

// V3: no applicable installer fails before any dependency is looked up.
#[cfg(windows)]
#[test]
fn install_without_applicable_installer_fails_before_dependencies() {
    let yaml = format!(
        "{DEPENDENCY_MANIFEST_HEADER}Dependencies:
  PackageDependencies:
    - PackageIdentifier: Missing.Dependency
Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/x64.exe
    InstallerSha256: ABC123
"
    );
    let error = install_from_manifest("no_applicable_installer", &yaml, |request| {
        request.query.installer_architecture = Some("arm64".to_owned());
    })
    .expect_err("no installer matches arm64");
    assert!(error.to_string().contains("No applicable installer found"), "{error}");
}

// V3: an unselected installer's dependencies are not installed.
#[cfg(windows)]
#[test]
fn install_dependencies_only_uses_selected_installer_dependencies() {
    let yaml = format!(
        "{DEPENDENCY_MANIFEST_HEADER}Installers:
  - Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.test/x64.exe
    InstallerSha256: ABC123
  - Architecture: arm64
    InstallerType: exe
    InstallerUrl: https://example.test/arm64.exe
    InstallerSha256: ABC123
    Dependencies:
      PackageDependencies:
        - PackageIdentifier: Missing.Dependency
"
    );
    let result = install_from_manifest("selected_installer_dependencies", &yaml, |request| {
        request.query.installer_architecture = Some("x64".to_owned());
        request.dependencies_only = true;
    })
    .expect("the selected x64 installer has no dependencies");
    assert_eq!(result.installer_type, "dependencies");
    assert!(result.success);
}

// V4: a self-dependency (the smallest cycle) is skipped instead of recursing.
#[cfg(windows)]
#[test]
fn install_skips_self_dependency_cycle() {
    let yaml = format!(
        "{DEPENDENCY_MANIFEST_HEADER}Dependencies:
  PackageDependencies:
    - PackageIdentifier: test.deproot
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/neutral.exe
    InstallerSha256: ABC123
"
    );
    let result = install_from_manifest("self_dependency", &yaml, |request| {
        request.dependencies_only = true;
    })
    .expect("a self-dependency is already visited");
    assert_eq!(result.installer_type, "dependencies");
}

fn query() -> PackageQuery {
    PackageQuery {
        os_version: Some("10.0.19041".to_owned()),
        ..PackageQuery::default()
    }
}

fn installer_with(configure: impl FnOnce(&mut Installer)) -> Installer {
    let mut installer = Installer {
        installer_type: Some("exe".to_owned()),
        url: Some("https://example.test/setup.exe".to_owned()),
        ..Installer::default()
    };
    configure(&mut installer);
    installer
}

// V8: MinimumOSVersion compares with zero-padded components.
#[test]
fn minimum_os_version_compares_differing_component_counts() {
    for (minimum, expected) in [
        ("10.0.19041.0", true),
        ("10.0.19041", true),
        ("10", true),
        ("10.0", true),
        ("10.0.19041.1", false),
        ("10.0.22000.0", false),
        ("11", false),
    ] {
        let installer = installer_with(|installer| installer.minimum_os_version = Some(minimum.to_owned()));
        assert_eq!(
            select_installer(&[installer], &query()).is_some(),
            expected,
            "MinimumOSVersion {minimum} against 10.0.19041"
        );
    }
    assert_eq!(compare_dotted_versions(&[10, 0], &[10, 0, 0, 0]), Ordering::Equal);
    assert_eq!(compare_dotted_versions(&[10, 0, 1], &[10, 0, 0, 5]), Ordering::Greater);
}

// V9: --locale excludes installers declaring another language, keeps undeclared ones.
#[test]
fn requested_locale_filters_mismatched_installers() {
    let french = installer_with(|installer| installer.locale = Some("fr-FR".to_owned()));
    let neutral = installer_with(|installer| installer.url = Some("https://example.test/neutral.exe".to_owned()));
    let british = installer_with(|installer| installer.locale = Some("en-GB".to_owned()));
    let mut locale_query = query();
    locale_query.locale = Some("en-US".to_owned());

    assert!(select_installer(std::slice::from_ref(&french), &locale_query).is_none());
    assert_eq!(
        select_installer(&[french.clone(), neutral], &locale_query)
            .and_then(|installer| installer.url)
            .as_deref(),
        Some("https://example.test/neutral.exe")
    );
    assert_eq!(
        select_installer(&[french.clone(), british], &locale_query).and_then(|installer| installer.locale),
        Some("en-GB".to_owned())
    );
    // Without --locale, a declared locale is still applicable.
    assert!(select_installer(&[french], &query()).is_some());
}

// V14: Windows.Universal installers apply on desktop unless a platform is requested.
#[test]
fn universal_platform_installer_applies_without_explicit_platform() {
    let universal = installer_with(|installer| installer.platforms = vec!["Windows.Universal".to_owned()]);
    assert!(select_installer(std::slice::from_ref(&universal), &query()).is_some());

    let mut desktop_query = query();
    desktop_query.platform = Some("Windows.Desktop".to_owned());
    assert!(select_installer(std::slice::from_ref(&universal), &desktop_query).is_none());

    let mut universal_query = query();
    universal_query.platform = Some("Windows.Universal".to_owned());
    assert!(select_installer(&[universal], &universal_query).is_some());
}

// V12: the upgrade compatibility check applies the full installer selection.
#[cfg(windows)]
#[test]
fn manifest_compatibility_applies_full_installer_selection() {
    let manifest = |extra: &str| {
        parse_yaml_manifest(
            format!(
                "PackageIdentifier: Test.Compat
PackageVersion: 1.0.0
PackageName: Test Compat
ManifestType: singleton
ManifestVersion: 1.10.0
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/setup.exe
    InstallerSha256: ABC123
{extra}"
            )
            .as_bytes(),
        )
        .expect("parse")
    };

    assert!(manifest_has_compatible_installer(&manifest("")));
    assert!(!manifest_has_compatible_installer(&manifest(
        "    MinimumOSVersion: 99.0.0.0\n"
    )));
    assert!(!manifest_has_compatible_installer(&manifest(
        "    Platform:\n      - Windows.IoT\n"
    )));
}

// V10: a leading non-digit prefix is trimmed like WinGet's Version.
#[test]
fn version_compare_trims_leading_non_digit_prefix() {
    assert_eq!(compare_version("v1.2.3", "1.2.3"), Ordering::Equal);
    assert_eq!(compare_version("v1.10", "v1.9"), Ordering::Greater);
    assert_eq!(compare_version("v2.0", "1.9.9"), Ordering::Greater);
    assert_eq!(compare_version("version 3", "2.5"), Ordering::Greater);
    assert_eq!(tokenize_version("v1.2.3"), vec!["1", "2", "3"]);
    // The first digit after the first '.' keeps the prefix, as in WinGet.
    assert_eq!(tokenize_version("abc.1"), vec!["abc", "1"]);
    // No digit at all: unchanged.
    assert_eq!(tokenize_version("latest"), vec!["latest"]);
}
