//! Review fixes A: `validate` reads manifests like WinGet and checks required values.

use std::path::PathBuf;

use serde_json::json;

use super::*;

fn temp_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("pinget-review-a-{}-{name}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("create dir");
    dir
}

const VALID: &str = "PackageIdentifier: Contoso.Tool\nPackageVersion: 1.10\nManifestType: singleton\nManifestVersion: 1.6.0\nInstallers:\n  - InstallerUrl: https://example.test/a.exe\n";

// ── #3: scalars convert like WinGet's ManifestYamlNodeToJson ──

#[test]
fn validation_json_keeps_text_nulls_and_wingets_typed_fields() {
    let value = parse_manifest_for_validation(
        b"PackageIdentifier: Contoso.Tool
PackageVersion: 1.10
Moniker: ~
Description: 'null'
License: true
Tags: 123
ReleaseNotes:
RequireExplicitUpgrade: yes
InstallerSuccessCodes: [3010, 0x10, -1]
ExpectedReturnCodes:
  - InstallerReturnCode: 0x8A150001
    ReturnResponse: packageInUse
AppsAndFeaturesEntries:
  - DisplayVersion: 1.10
",
    )
    .expect("parse");

    assert_eq!(value["PackageVersion"], json!("1.10"));
    assert_eq!(value["Moniker"], serde_json::Value::Null);
    assert_eq!(value["Description"], json!("null"));
    assert_eq!(value["License"], json!("true"));
    assert_eq!(value["Tags"], json!("123"));
    assert_eq!(value["ReleaseNotes"], serde_json::Value::Null);
    assert_eq!(value["RequireExplicitUpgrade"], json!(true));
    assert_eq!(value["InstallerSuccessCodes"], json!([3010, 16, -1]));
    assert_eq!(
        value["ExpectedReturnCodes"][0]["InstallerReturnCode"],
        json!(0x8A15_0001i64)
    );
    assert_eq!(value["AppsAndFeaturesEntries"][0]["DisplayVersion"], json!("1.10"));
}

#[test]
fn validation_json_rejects_multiple_documents_and_reads_empty_files_as_null() {
    assert!(parse_manifest_for_validation(b"a: 1\n---\nb: 2\n").is_err());
    assert_eq!(
        parse_manifest_for_validation(b"").expect("empty"),
        serde_json::Value::Null
    );
}

// ── #3: schema-invalid list fields are validation results, not parse errors ──

#[test]
fn schema_invalid_list_fields_do_not_abort_validation() {
    let dir = temp_dir("lists");
    let manifest = dir.join("tags.yaml");
    std::fs::write(
        &manifest,
        format!("{VALID}Tags: 123\nCommands:\nPlatform: Windows.Desktop\n"),
    )
    .expect("write");
    let report = validate_manifest_path(manifest.to_str().expect("utf8"));
    let _ = std::fs::remove_dir_all(&dir);
    report.expect("schema-invalid fields must be reported, not fail the parse");
}

// ── #3: an empty/null ManifestType is a singleton; required fields need a value ──

#[test]
fn empty_manifest_type_and_empty_required_fields_are_reported() {
    let dir = temp_dir("required");
    for (name, content) in [
        (
            "empty-type.yaml",
            "PackageIdentifier:\nPackageVersion: ''\nManifestType:\nManifestVersion: 1.6.0\nInstallers: ~\n",
        ),
        (
            "null-type.yaml",
            "PackageIdentifier: ~\nPackageVersion: null\nManifestType: ~\nManifestVersion: 1.6.0\nInstallers: []\n",
        ),
    ] {
        let manifest = dir.join(name);
        std::fs::write(&manifest, content).expect("write");
        let report = validate_manifest_path(manifest.to_str().expect("utf8")).expect("validate");
        for field in ["'Installers'", "'PackageIdentifier'", "'PackageVersion'"] {
            assert!(
                report.errors.iter().any(|error| error.contains(field)),
                "{name}: {field} not reported in {:?}",
                report.errors
            );
        }
    }
    let _ = std::fs::remove_dir_all(&dir);
}

// ── #3: directory mode matches .yaml/.yml case-insensitively and skips directories ──

#[test]
fn directory_mode_matches_extensions_case_insensitively_and_skips_directories() {
    let dir = temp_dir("directory");
    std::fs::write(
        dir.join("Upper.YAML"),
        "PackageIdentifier: Contoso.Tool\nManifestType: singleton\nManifestVersion: 1.6.0\n",
    )
    .expect("write");
    std::fs::write(dir.join("Mixed.Yml"), VALID).expect("write");
    std::fs::create_dir_all(dir.join("folder.yaml")).expect("create folder");

    let report = validate_manifest_path(dir.to_str().expect("utf8"));
    let _ = std::fs::remove_dir_all(&dir);
    let report = report.expect("a directory named *.yaml must be skipped");
    assert!(
        report
            .errors
            .iter()
            .any(|error| error.contains("Upper.YAML") && error.contains("'Installers'")),
        "{:?}",
        report.errors
    );
    assert!(
        !report
            .errors
            .iter()
            .any(|error| error.contains("Mixed.Yml") && error.contains("missing")),
        "{:?}",
        report.errors
    );
}
