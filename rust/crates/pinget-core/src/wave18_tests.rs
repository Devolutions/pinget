//! Wave 18 regression tests: manifest model, YAML parsing and emission, dependencies.

use super::*;

const HEADER: &str = "PackageIdentifier: Test.Wave18
PackageVersion: 1.0.0
PackageName: Test Wave18
Publisher: Example
ManifestType: singleton
ManifestVersion: 1.10.0
";

fn parse(body: &str) -> Manifest {
    parse_yaml_manifest(format!("{HEADER}{body}").as_bytes()).expect("parse manifest")
}

fn structured(yaml: &str) -> JsonValue {
    let (_, documents) = parse_yaml_manifest_bundle(yaml.as_bytes()).expect("parse manifest");
    collapse_structured_document(&documents)
}

// W18-1: an installer key with an empty (null) value is ignored, like WinGet's populator, instead of
// erasing the value the installer inherits from the root.
#[test]
fn null_installer_value_keeps_root_default() {
    let manifest = parse(
        "Scope: machine
MinimumOSVersion: 10.0.17763.0
Platform: [Windows.Desktop]
Commands: [tool]
InstallerSuccessCodes: [3010]
DownloadCommandProhibited: true
Markets:
  AllowedMarkets: [US]
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
    Scope:
    MinimumOSVersion:
    Platform:
    Commands:
    InstallerSuccessCodes:
    DownloadCommandProhibited:
    Markets:
",
    );
    let installer = &manifest.installers[0];
    assert_eq!(installer.scope.as_deref(), Some("machine"));
    assert_eq!(installer.minimum_os_version.as_deref(), Some("10.0.17763.0"));
    assert_eq!(installer.platforms, vec!["Windows.Desktop"]);
    assert_eq!(installer.commands, vec!["tool"]);
    assert_eq!(installer.installer_success_codes, vec![3010]);
    assert!(installer.download_command_prohibited);
    assert_eq!(installer.allowed_markets, vec!["US"]);
}

// W18-2: a root ProductCode / AppsAndFeaturesEntries only flows into installers whose effective type
// writes an ARP entry (WinGet `DoesInstallerTypeUseProductCode` / `...WriteAppsAndFeaturesEntry`).
#[test]
fn root_product_code_and_arp_entries_only_reach_arp_installer_types() {
    let manifest = parse(
        "ProductCode: '{ROOT-PRODUCT}'
AppsAndFeaturesEntries:
  - DisplayName: Root
    UpgradeCode: '{ROOT-UPGRADE}'
Installers:
  - InstallerType: msi
    InstallerUrl: https://example.test/a.msi
  - InstallerType: msix
    InstallerUrl: https://example.test/a.msix
  - InstallerType: zip
    NestedInstallerType: wix
    NestedInstallerFiles:
      - RelativeFilePath: setup.msi
    InstallerUrl: https://example.test/a.zip
",
    );
    let [msi, msix, zip] = &manifest.installers[..] else {
        panic!("expected three installers");
    };
    assert_eq!(msi.product_code.as_deref(), Some("{ROOT-PRODUCT}"));
    assert_eq!(msi.apps_and_features_entries.len(), 1);
    assert_eq!(msix.product_code, None);
    assert!(msix.apps_and_features_entries.is_empty());
    assert_eq!(zip.product_code.as_deref(), Some("{ROOT-PRODUCT}"));
    assert_eq!(zip.apps_and_features_entries.len(), 1);
}

// W18-3: a root PackageFamilyName only flows into msix/msstore installers (or one whose
// AppsAndFeaturesEntries name an msix type), like WinGet `DoesInstallerTypeUsePackageFamilyName`.
#[test]
fn root_package_family_name_only_reaches_msix_installer_types() {
    let manifest = parse(
        "PackageFamilyName: Contoso.App_8wekyb3d8bbwe
Installers:
  - InstallerType: appx
    InstallerUrl: https://example.test/a.appx
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
  - InstallerType: exe
    InstallerUrl: https://example.test/b.exe
    AppsAndFeaturesEntries:
      - InstallerType: msix
",
    );
    let pfns = manifest
        .installers
        .iter()
        .map(|installer| installer.package_family_name.as_deref())
        .collect::<Vec<_>>();
    assert_eq!(
        pfns,
        vec![
            Some("Contoso.App_8wekyb3d8bbwe"),
            None,
            Some("Contoso.App_8wekyb3d8bbwe")
        ]
    );
}

// W18-4: root NestedInstallerType/NestedInstallerFiles only flow into archive (zip) installers; a
// standalone portable installer next to a zip must not be treated as an archive.
#[test]
fn root_nested_installer_fields_only_reach_archive_installers() {
    let manifest = parse(
        "NestedInstallerType: portable
NestedInstallerFiles:
  - RelativeFilePath: tool/tool.exe
    PortableCommandAlias: tool
Installers:
  - InstallerType: zip
    InstallerUrl: https://example.test/tool.zip
  - InstallerType: portable
    InstallerUrl: https://example.test/standalone.exe
    Commands: [standalone]
",
    );
    let zip = &manifest.installers[0];
    assert_eq!(zip.nested_installer_type.as_deref(), Some("portable"));
    assert_eq!(zip.nested_installer_files.len(), 1);
    let portable = &manifest.installers[1];
    assert_eq!(portable.nested_installer_type, None);
    assert!(portable.nested_installer_files.is_empty());
    assert_eq!(determine_portable_alias(portable).as_deref(), Some("standalone"));
}

// W18-6: an empty Description falls back to ShortDescription, as WinGet `show` does.
#[test]
fn empty_description_falls_back_to_short_description() {
    let manifest = parse(
        "ShortDescription: A short summary
Description: ''
Installers:
  - InstallerUrl: https://example.test/a.exe
",
    );
    assert_eq!(manifest.description.as_deref(), Some("A short summary"));

    let payload = br#"{ "Data": { "PackageIdentifier": "Test.Rest", "Versions": [ {
        "PackageVersion": "1.0.0",
        "DefaultLocale": { "PackageLocale": "en-US", "PackageName": "Test Rest",
                           "ShortDescription": "A short summary", "Description": "" },
        "Installers": [ { "InstallerType": "exe", "InstallerUrl": "https://example.test/a.exe" } ] } ] } }"#;
    let (manifest, _) = parse_rest_manifest(payload, "Test.Rest", "1.0.0", "").expect("parse REST manifest");
    assert_eq!(manifest.description.as_deref(), Some("A short summary"));
}

// W18-7: the additional locales (`Localization`) of a merged manifest survive the structured
// document used by `show -o json|yaml` and the `download` manifest.
#[test]
fn merged_manifest_structured_document_keeps_localization() {
    let document = structured(
        "PackageIdentifier: Test.Merged
PackageVersion: 1.0.0
PackageLocale: en-US
PackageName: Test Merged
Publisher: Example
ShortDescription: English
Localization:
  - PackageLocale: fr-FR
    ShortDescription: Francais
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
ManifestType: merged
ManifestVersion: 1.10.0
",
    );
    assert_eq!(document["ManifestType"], "singleton");
    assert_eq!(document["Localization"][0]["PackageLocale"], "fr-FR");
    assert_eq!(document["Localization"][0]["ShortDescription"], "Francais");
}

// W18-8: dependency ids are trimmed and de-duplicated case-insensitively per kind, keeping the highest
// MinimumVersion, like WinGet `DependencyList::Add`.
#[test]
fn dependency_lists_are_trimmed_and_deduplicated() {
    let manifest = parse(
        "Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
    Dependencies:
      WindowsFeatures: [NetFx3, ' netfx3 ']
      WindowsLibraries: [vcruntime, VCRuntime]
      ExternalDependencies: [Java, java]
      PackageDependencies:
        - PackageIdentifier: Contoso.Runtime
          MinimumVersion: 1.0
        - PackageIdentifier: ' contoso.runtime '
          MinimumVersion: 2.0
",
    );
    let dependencies = &manifest.installers[0].dependencies;
    assert_eq!(dependencies.windows_features, vec!["NetFx3"]);
    assert_eq!(dependencies.windows_libraries, vec!["vcruntime"]);
    assert_eq!(dependencies.external_dependencies, vec!["Java"]);
    assert_eq!(
        dependencies.package_dependencies,
        vec![PackageDependency {
            package_identifier: "Contoso.Runtime".to_owned(),
            minimum_version: Some("2.0".to_owned()),
        }]
    );
    assert_eq!(manifest.installers[0].package_dependencies, vec!["Contoso.Runtime"]);
}

// W18-9: boolean-looking scalars keep their source text (`True`, `FALSE`) like the C# reader and
// WinGet, while boolean fields still read them as booleans.
#[test]
fn boolean_scalars_keep_their_source_text() {
    let manifest = parse_yaml_manifest(
        b"PackageIdentifier: Test.Bool
PackageVersion: 1.0.0
PackageName: True
Publisher: FALSE
Tags: [TRUE, false]
Installers:
  - InstallerUrl: https://example.test/a.exe
    DownloadCommandProhibited: True
ManifestType: singleton
ManifestVersion: 1.10.0
",
    )
    .expect("parse manifest");
    assert_eq!(manifest.name, "True");
    assert_eq!(manifest.publisher.as_deref(), Some("FALSE"));
    assert_eq!(manifest.tags, vec!["TRUE", "false"]);
    assert!(manifest.installers[0].download_command_prohibited);
}

// W18-10: an empty YAML document in the stream (a trailing `---`) does not wipe the structured
// document of a singleton manifest.
#[test]
fn empty_yaml_document_does_not_wipe_structured_document() {
    let document = structured(&format!(
        "{HEADER}Installers:
  - InstallerUrl: https://example.test/a.exe
---
"
    ));
    assert_eq!(document["PackageIdentifier"], "Test.Wave18");
    assert_eq!(document["PackageName"], "Test Wave18");
    assert_eq!(document["Installers"][0]["InstallerUrl"], "https://example.test/a.exe");
}
