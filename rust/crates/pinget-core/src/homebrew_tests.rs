use super::*;

#[test]
fn homebrew_identifiers_are_stable_and_collision_safe() {
    let first = homebrew_package_identifier("homebrew/core", "foo+bar").expect("valid Homebrew identity");
    let same_identity = homebrew_package_identifier("Homebrew/Core", "FOO+BAR").expect("valid Homebrew identity");
    let different_formula = homebrew_package_identifier("homebrew/core", "foo-bar").expect("valid Homebrew identity");
    let different_tap = homebrew_package_identifier("other/core", "foo+bar").expect("valid Homebrew identity");

    assert_eq!(first, same_identity);
    assert_eq!(first, "Pinget.Homebrew.foo-bar-aa8e189c75775fb4");
    assert_ne!(first, different_formula);
    assert_ne!(first, different_tap);
    assert!(first.starts_with("Pinget.Homebrew."));
    assert!(homebrew_package_identifier("homebrew", "jq").is_err());
    assert!(homebrew_package_identifier("homebrew/core", " jq").is_err());
    assert!(homebrew_package_identifier("///", "jq").is_err());
    assert!(homebrew_package_identifier("homebrew/core", "++").is_err());
    assert_eq!(map_homebrew_bottle_tag("x86_64_linux"), Some(("Linux", "x64")));
    assert_eq!(map_homebrew_bottle_tag("aarch64_linux"), Some(("Linux", "arm64")));
    assert_eq!(map_homebrew_bottle_tag("x86_64_linux_gnu"), None);
}

const VALID_HOME_BREW_MANIFEST: &str = r#"
PackageIdentifier: Pinget.Homebrew.jq-929958fef124d633
PackageVersion: 1.8.1_1
PackageName: jq
ManifestType: singleton
ManifestVersion: 1.10.0
Installers:
  - InstallerType: homebrew-bottle
    Architecture: arm64
    Platform: [macOS]
    InstallerUrl: https://bottles.example.invalid/jq.tar.gz
    InstallerSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
Pinget:
  Homebrew:
    SchemaVersion: 1
    Formula: jq
    Tap: homebrew/core
    UpstreamVersion: 1.8.1
    Revision: 1
    VersionScheme: 0
    Aliases: [jqr]
    OldNames: [jq-old]
    KegOnly: false
    Bottles:
      - Url: https://bottles.example.invalid/jq.tar.gz
        Sha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        TargetOS: macOS
        Architecture: arm64
        BottleTag: arm64_sonoma
        Rebuild: 3
        Relocation: any
        MinimumOSVersion: "14.0"
"#;

#[test]
fn parses_and_normalizes_versioned_homebrew_metadata() {
    let manifest = parse_yaml_manifest(VALID_HOME_BREW_MANIFEST.as_bytes()).expect("Homebrew manifest");
    let homebrew = manifest.homebrew.expect("extension metadata");
    assert_eq!(manifest.version, "1.8.1_1");
    assert_eq!(homebrew.schema_version, 1);
    assert_eq!(homebrew.formula, "jq");
    assert_eq!(homebrew.tap, "homebrew/core");
    assert_eq!(homebrew.upstream_version, "1.8.1");
    assert_eq!(homebrew.revision, 1);
    assert_eq!(homebrew.version_scheme, 0);
    assert_eq!(homebrew.aliases, ["jqr"]);
    assert_eq!(homebrew.old_names, ["jq-old"]);
    assert!(!homebrew.keg_only);
    assert_eq!(homebrew.bottles.len(), 1);
    assert_eq!(homebrew.bottles[0].sha256, "a".repeat(64));
    assert_eq!(homebrew.bottles[0].rebuild, 3);
    assert_eq!(homebrew.bottles[0].bottle_tag, "arm64_sonoma");
    assert_eq!(homebrew.bottles[0].minimum_os_version.as_deref(), Some("14.0"));
}

#[test]
fn rejects_unknown_schema_and_mismatched_target_constraints() {
    let unknown_schema = VALID_HOME_BREW_MANIFEST.replace("SchemaVersion: 1", "SchemaVersion: 2");
    assert!(
        parse_yaml_manifest(unknown_schema.as_bytes())
            .expect_err("unknown extension version")
            .to_string()
            .contains("unsupported Pinget.Homebrew.SchemaVersion 2")
    );

    let mismatched_target = VALID_HOME_BREW_MANIFEST
        .replace("TargetOS: macOS", "TargetOS: Linux")
        .replace("Architecture: arm64", "Architecture: x64")
        .replace("BottleTag: arm64_sonoma", "BottleTag: x86_64_linux");
    assert!(
        parse_yaml_manifest(mismatched_target.as_bytes())
            .expect_err("macOS constraint on Linux")
            .to_string()
            .contains("macOS product-version fields cannot be used for a Linux bottle target")
    );

    let unknown_tag = VALID_HOME_BREW_MANIFEST.replace("arm64_sonoma", "arm64_unknown");
    assert!(
        parse_yaml_manifest(unknown_tag.as_bytes())
            .expect_err("unknown bottle tag")
            .to_string()
            .contains("unsupported Homebrew bottle tag 'arm64_unknown'")
    );

    let mismatched_package_id = VALID_HOME_BREW_MANIFEST.replace(
        "Pinget.Homebrew.jq-929958fef124d633",
        "Pinget.Homebrew.jq",
    );
    assert!(
        parse_yaml_manifest(mismatched_package_id.as_bytes())
            .expect_err("package id must be derived from exact formula identity")
            .to_string()
            .contains("PackageIdentifier must equal Homebrew-derived identifier")
    );

    let incorrect_revision_version = VALID_HOME_BREW_MANIFEST.replace("1.8.1_1", "1.8.1");
    assert!(
        parse_yaml_manifest(incorrect_revision_version.as_bytes())
            .expect_err("revision must participate in PackageVersion")
            .to_string()
            .contains("PackageVersion must equal Homebrew stable pkg_version '1.8.1_1'")
    );

    let mismatched_installer = VALID_HOME_BREW_MANIFEST.replacen(
        "InstallerType: homebrew-bottle\n    Architecture: arm64",
        "InstallerType: homebrew-bottle\n    Architecture: x64",
        1,
    );
    assert!(
        parse_yaml_manifest(mismatched_installer.as_bytes())
            .expect_err("bottle target must match installer")
            .to_string()
            .contains("target must match its installer Platform and Architecture")
    );
}
