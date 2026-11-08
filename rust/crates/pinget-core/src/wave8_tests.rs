//! Wave 8 regression tests: manifest model, applicability, dependencies and download.

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

fn temp_dir(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let path = std::env::temp_dir().join(format!(
        "pinget-rs-wave8-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ));
    fs::create_dir_all(&path).expect("create temp dir");
    path
}

fn private_repository(root: &Path) -> Repository {
    let options = RepositoryOptions::new(root.to_path_buf()).with_source_mode(SourceMode::Private);
    let mut repository = Repository::open_with_options(options).expect("open repository");
    repository
        .set_admin_setting("LocalManifestFiles", true)
        .expect("enable local manifests");
    for source in repository.list_sources() {
        repository.remove_source(&source.name).expect("remove source");
    }
    repository
}

const HEADER: &str = "PackageIdentifier: Test.Wave8
PackageVersion: 1.0.0
PackageName: Test Wave8
Publisher: Example
ManifestType: singleton
ManifestVersion: 1.10.0
";

fn parse(body: &str) -> Manifest {
    parse_yaml_manifest(format!("{HEADER}{body}").as_bytes()).expect("parse manifest")
}

fn installer(url: &str) -> Installer {
    Installer {
        url: Some(url.to_owned()),
        ..Installer::default()
    }
}

fn download(label: &str, body: &str, accept_package_agreements: bool) -> Result<DownloadOutput> {
    let root = temp_dir(label);
    let manifest_path = root.join("manifest.yaml");
    fs::write(&manifest_path, format!("{HEADER}{body}")).expect("write manifest");
    let mut repository = private_repository(&root);
    let mut request = InstallRequest::new(PackageQuery::default());
    request.manifest_path = Some(manifest_path);
    request.accept_package_agreements = accept_package_agreements;
    let result = repository.download_installer_for_request(&request, &root.join("out"));
    let _ = fs::remove_dir_all(root);
    result
}

// An unroutable URL: these tests must fail before any download is attempted.
const UNREACHABLE_INSTALLER: &str = "Installers:
  - Architecture: neutral
    InstallerType: exe
    InstallerUrl: http://127.0.0.1:9/setup.exe
    InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
";

// R8-1: download requires the package agreements to be accepted.
#[test]
fn download_requires_package_agreements() {
    let body = format!(
        "Agreements:
  - AgreementLabel: EULA
    Agreement: Terms
{UNREACHABLE_INSTALLER}"
    );
    let error = download("agreements", &body, false).expect_err("agreements must be accepted");
    assert!(error.to_string().contains("--accept-package-agreements"), "{error}");
}

// R8-1: an installer marked DownloadCommandProhibited (inherited from the root) is refused.
#[test]
fn download_refuses_download_command_prohibited_installer() {
    let body = format!("DownloadCommandProhibited: true\n{UNREACHABLE_INSTALLER}");
    let manifest = parse(&body);
    assert!(manifest.installers[0].download_command_prohibited);

    let error = download("prohibited", &body, true).expect_err("download is prohibited");
    assert!(
        error.to_string().contains("prohibited from being downloaded"),
        "{error}"
    );
}

#[test]
fn rest_manifest_reads_installer_applicability_fields() {
    let payload = br#"{
      "Data": {
        "PackageIdentifier": "Test.Rest",
        "Versions": [
          {
            "PackageVersion": "1.0.0",
            "DefaultLocale": { "PackageLocale": "en-US", "PackageName": "Test Rest" },
            "UpgradeBehavior": "deny",
            "Installers": [
              {
                "Architecture": "x64",
                "InstallerType": "exe",
                "InstallerUrl": "https://example.test/a.exe",
                "DownloadCommandProhibited": true,
                "Markets": { "AllowedMarkets": ["CN"] },
                "UnsupportedOSArchitectures": ["arm64"],
                "InstallerSwitches": { "Upgrade": "/UPDATE" },
                "AppsAndFeaturesEntries": [{ "ProductCode": "App_is1", "UpgradeCode": "{UP}" }],
                "Dependencies": {
                  "WindowsFeatures": ["NetFx3"],
                  "PackageDependencies": [{ "PackageIdentifier": "Dep.One", "MinimumVersion": "14.30" }]
                }
              }
            ]
          }
        ]
      }
    }"#;
    let (manifest, _) = parse_rest_manifest(payload, "Test.Rest", "1.0.0", "").expect("parse");
    let installer = &manifest.installers[0];
    assert!(installer.download_command_prohibited);
    assert_eq!(installer.allowed_markets, vec!["CN".to_owned()]);
    assert_eq!(installer.unsupported_os_architectures, vec!["arm64".to_owned()]);
    assert_eq!(installer.upgrade_behavior.as_deref(), Some("deny"));
    assert_eq!(installer.switches.upgrade.as_deref(), Some("/UPDATE"));
    assert_eq!(
        installer.apps_and_features_entries[0].product_code.as_deref(),
        Some("App_is1")
    );
    assert_eq!(installer.dependencies.windows_features, vec!["NetFx3".to_owned()]);
    assert_eq!(
        installer.dependencies.package_dependencies[0]
            .minimum_version
            .as_deref(),
        Some("14.30")
    );
    assert_eq!(installer.package_dependencies, vec!["Dep.One".to_owned()]);
}

// R8-2: Markets is parsed (root inherited) and filters installer selection.
#[test]
fn markets_filter_installer_selection() {
    let manifest = parse(
        "Markets:
  AllowedMarkets: [CN]
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/cn.exe
  - InstallerType: exe
    InstallerUrl: https://example.test/global.exe
    Markets:
      ExcludedMarkets: [FR]
",
    );
    assert_eq!(manifest.installers[0].allowed_markets, vec!["CN".to_owned()]);
    assert_eq!(manifest.installers[1].allowed_markets, Vec::<String>::new());
    assert_eq!(manifest.installers[1].excluded_markets, vec!["FR".to_owned()]);

    let query = PackageQuery::default();
    let pick = |market: &str| {
        select_installer_for_system(&manifest.installers, &query, market, &[]).and_then(|installer| installer.url)
    };
    assert_eq!(pick("US").as_deref(), Some("https://example.test/global.exe"));
    assert_eq!(pick("cn").as_deref(), Some("https://example.test/cn.exe"));
    assert_eq!(pick("FR"), None);
}

#[test]
fn allowed_markets_take_precedence_over_excluded_markets() {
    let mut candidate = installer("https://example.test/a.exe");
    candidate.allowed_markets = vec!["US".to_owned()];
    candidate.excluded_markets = vec!["US".to_owned()];
    assert!(installer_matches_market(&candidate, "us"));
    assert!(!installer_matches_market(&candidate, "CA"));
}

// R8-2: an installer that lists the system architecture as unsupported is not applicable.
#[test]
fn unsupported_os_architecture_filters_installer_selection() {
    let system = current_architecture();
    let manifest = parse(&format!(
        "UnsupportedOSArchitectures: [{system}]
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/emulated.exe
  - InstallerType: exe
    InstallerUrl: https://example.test/native.exe
    UnsupportedOSArchitectures: [not-this-one]
"
    ));
    assert_eq!(
        manifest.installers[0].unsupported_os_architectures,
        vec![system.to_owned()]
    );
    let selected = select_installer_for_system(&manifest.installers, &PackageQuery::default(), "US", &[])
        .expect("an installer applies");
    assert_eq!(selected.url.as_deref(), Some("https://example.test/native.exe"));
}

// R8-3: UpgradeBehavior is parsed with root inheritance; deny blocks updates only.
#[test]
fn upgrade_behavior_is_parsed_and_deny_blocks_updates() {
    let manifest = parse(
        "UpgradeBehavior: deny
Installers:
  - InstallerUrl: https://example.test/a.exe
  - InstallerUrl: https://example.test/b.exe
    UpgradeBehavior: uninstallPrevious
",
    );
    let deny = &manifest.installers[0];
    let uninstall_previous = &manifest.installers[1];
    assert_eq!(deny.upgrade_behavior.as_deref(), Some("deny"));
    assert!(upgrade_behavior_is(uninstall_previous, "uninstallprevious"));
    assert!(!upgrade_behavior_is(deny, "uninstallPrevious"));

    let error = ensure_upgrade_allowed(deny, true).expect_err("deny blocks an update");
    assert!(error.to_string().contains("cannot be upgraded"), "{error}");
    ensure_upgrade_allowed(deny, false).expect("a fresh install is allowed");
    ensure_upgrade_allowed(uninstall_previous, true).expect("uninstallPrevious allows updates");

    // Only a version change over an existing installation is an update; a repair is not.
    assert!(install_is_update("1.0.0", "2.0.0"));
    assert!(!install_is_update("2.0", "2.0.0"));
}

// R8-5: MinimumVersion keeps its text, and installer dependencies of any kind override the root.
#[test]
fn dependencies_keep_minimum_version_and_any_kind_overrides_root() {
    let manifest = parse(
        "Dependencies:
  PackageDependencies:
    - PackageIdentifier: Root.Dep
      MinimumVersion: 14.30
Installers:
  - InstallerUrl: https://example.test/a.exe
  - InstallerUrl: https://example.test/b.exe
    Dependencies:
      WindowsFeatures: [NetFx3]
",
    );
    let inherited = &manifest.installers[0];
    assert_eq!(
        selected_installer_dependencies(&manifest, inherited),
        vec![PackageDependency {
            package_identifier: "Root.Dep".to_owned(),
            minimum_version: Some("14.30".to_owned()),
        }]
    );

    let features_only = &manifest.installers[1];
    assert_eq!(features_only.dependencies.windows_features, vec!["NetFx3".to_owned()]);
    assert!(selected_installer_dependencies(&manifest, features_only).is_empty());
    assert!(selected_installer_package_dependencies(&manifest, features_only).is_empty());
}

#[test]
fn duplicate_dependencies_keep_the_highest_minimum_version() {
    let mut selected = installer("https://example.test/a.exe");
    selected.dependencies.package_dependencies = vec![
        PackageDependency {
            package_identifier: "Dep".to_owned(),
            minimum_version: Some("1.2".to_owned()),
        },
        PackageDependency {
            package_identifier: "DEP".to_owned(),
            minimum_version: Some("1.10".to_owned()),
        },
    ];
    let manifest = parse("Installers:\n  - InstallerUrl: https://example.test/a.exe\n");
    assert_eq!(
        selected_installer_dependencies(&manifest, &selected),
        vec![PackageDependency {
            package_identifier: "Dep".to_owned(),
            minimum_version: Some("1.10".to_owned()),
        }]
    );
}

#[test]
fn dependency_minimum_version_checks() {
    assert!(dependency_version_satisfies("14.38.33130.0", "14.30"));
    assert!(dependency_version_satisfies("14.30", "14.30"));
    assert!(!dependency_version_satisfies("14.29.30133.0", "14.30"));
    assert!(!dependency_version_satisfies("Unknown", "1.0"));

    ensure_dependency_minimum_version("Dep", "2.0", "1.5").expect("2.0 satisfies 1.5");
    let error = ensure_dependency_minimum_version("Dep", "1.0", "1.5").expect_err("1.0 is too old");
    assert!(error.to_string().contains("minimum version 1.5"), "{error}");
}

// R8-6: the download manifest keeps every field of the selected installer entry.
#[test]
fn download_manifest_keeps_selected_installer_fields() {
    let yaml = format!(
        "{HEADER}InstallerType: zip
Installers:
  - Architecture: x64
    InstallerUrl: https://example.test/x64.zip
    InstallerSha256: AAA
    NestedInstallerType: portable
    NestedInstallerFiles:
      - RelativeFilePath: tool.exe
        PortableCommandAlias: tool
    InstallerSwitches:
      Custom: /custom
    Dependencies:
      PackageDependencies:
        - PackageIdentifier: Dep.One
    InstallerSuccessCodes: [3010]
  - Architecture: arm64
    InstallerUrl: https://example.test/arm64.zip
    InstallerSha256: BBB
"
    );
    let (manifest, documents) = parse_yaml_manifest_bundle(yaml.as_bytes()).expect("parse");
    let selected = manifest.installers[0].clone();
    let rendered = build_download_manifest_yaml(&manifest, &documents, &selected).expect("render");
    let reparsed = parse_yaml_manifest(rendered.as_bytes()).expect("reparse");

    assert_eq!(reparsed.installers.len(), 1);
    let installer = &reparsed.installers[0];
    assert_eq!(installer.url.as_deref(), Some("https://example.test/x64.zip"));
    assert_eq!(installer.installer_type.as_deref(), Some("zip"));
    assert_eq!(installer.nested_installer_type.as_deref(), Some("portable"));
    assert_eq!(
        installer.nested_installer_files[0].portable_command_alias.as_deref(),
        Some("tool")
    );
    assert_eq!(installer.switches.custom.as_deref(), Some("/custom"));
    assert_eq!(installer.package_dependencies, vec!["Dep.One".to_owned()]);
    assert_eq!(installer.installer_success_codes, vec![3010]);
}

// R8-7: the Upgrade switch is parsed (root fallback) and appended after the custom switches.
#[test]
fn upgrade_switch_is_parsed_and_appended() {
    let manifest = parse(
        "InstallerSwitches:
  Upgrade: /UPDATE
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
    InstallerSwitches:
      Silent: /S
      Custom: /custom
",
    );
    let mut selected = manifest.installers[0].clone();
    assert_eq!(selected.switches.upgrade.as_deref(), Some("/UPDATE"));

    let mut request = InstallRequest::new(PackageQuery::default());
    request.mode = InstallerMode::Silent;
    let args = installer_command_arguments("exe", &request, &manifest, Path::new("setup.exe"), &selected);
    assert_eq!(args, vec!["/S", "/custom", "/UPDATE"]);

    // The install flow clears the switch when nothing is installed yet.
    selected.switches.upgrade = None;
    let args = installer_command_arguments("exe", &request, &manifest, Path::new("setup.exe"), &selected);
    assert_eq!(args, vec!["/S", "/custom"]);
}

// R8-8: without --locale, the preferred languages rank installer locales like WinGet.
#[test]
fn installer_locale_follows_preferred_languages() {
    let with_locale = |url: &str, locale: Option<&str>| Installer {
        locale: locale.map(str::to_owned),
        ..installer(url)
    };
    let preferred = vec!["en-US".to_owned(), "fr-FR".to_owned()];
    let query = PackageQuery::default();
    let pick = |installers: &[Installer], preferred: &[String]| {
        select_installer_for_system(installers, &query, "US", preferred).and_then(|installer| installer.url)
    };

    let german_then_english = [with_locale("de", Some("de-DE")), with_locale("en", Some("en-US"))];
    assert_eq!(pick(&german_then_english, &preferred).as_deref(), Some("en"));

    let unknown_then_french = [with_locale("unknown", None), with_locale("fr", Some("fr-FR"))];
    // fr-FR matches the second preference, which beats an unknown locale.
    assert_eq!(pick(&unknown_then_french, &preferred).as_deref(), Some("fr"));
    // With only en-US preferred, the unknown locale beats a non-matching declared one.
    assert_eq!(
        pick(&unknown_then_french, &["en-US".to_owned()]).as_deref(),
        Some("unknown")
    );

    let english_variants = [with_locale("gb", Some("en-GB")), with_locale("us", Some("en-US"))];
    assert_eq!(pick(&english_variants, &preferred).as_deref(), Some("us"));
    let french_and_british = [with_locale("fr", Some("fr-FR")), with_locale("gb", Some("en-GB"))];
    assert_eq!(pick(&french_and_british, &preferred).as_deref(), Some("gb"));

    // No preference at all: the manifest order decides.
    assert_eq!(pick(&german_then_english, &[]).as_deref(), Some("de"));
}

// R8-11: list items keep numeric and boolean scalars as their source text.
#[test]
fn yaml_lists_keep_numeric_and_boolean_items() {
    let manifest = parse(
        "Tags: [2048, game, 1.10, true]
Commands: [7z, 365]
Installers:
  - InstallerUrl: https://example.test/a.exe
",
    );
    assert_eq!(manifest.tags, vec!["2048", "game", "1.10", "true"]);
    assert_eq!(manifest.installers[0].commands, vec!["7z", "365"]);
}

#[test]
fn yaml_hex_return_codes_still_parse() {
    let manifest = parse(
        "Installers:
  - InstallerUrl: https://example.test/a.exe
    InstallerSuccessCodes: [0x10, 3010]
",
    );
    assert_eq!(manifest.installers[0].installer_success_codes, vec![16, 3010]);
}

// R8-12: AppsAndFeaturesEntries codes (root inherited) identify the installed package.
#[test]
fn apps_and_features_entries_supply_correlation_codes() {
    let manifest = parse(
        "AppsAndFeaturesEntries:
  - DisplayName: Git
    ProductCode: Git_is1
Installers:
  - InstallerType: inno
    InstallerUrl: https://example.test/a.exe
  - InstallerType: msi
    InstallerUrl: https://example.test/b.msi
    ProductCode: '{PRODUCT}'
    AppsAndFeaturesEntries:
      - UpgradeCode: '{UPGRADE}'
",
    );
    assert_eq!(
        manifest.installers[0].apps_and_features_entries[0]
            .product_code
            .as_deref(),
        Some("Git_is1")
    );
    let codes = ManifestCorrelationCodes::from_manifest(&manifest);
    assert_eq!(codes.product_codes, vec!["Git_is1".to_owned(), "{PRODUCT}".to_owned()]);
    assert_eq!(codes.upgrade_codes, vec!["{UPGRADE}".to_owned()]);
    assert!(codes.matches(&["git_is1".to_owned()], &[]));
    assert!(codes.matches(&[], &["{upgrade}".to_owned()]));
    assert!(!codes.matches(&["Other".to_owned()], &["{OTHER}".to_owned()]));
    assert!(
        ManifestCorrelationCodes::from_manifest(&parse("Installers:\n  - InstallerUrl: https://x.test/a.exe\n"))
            .is_empty()
    );
}
