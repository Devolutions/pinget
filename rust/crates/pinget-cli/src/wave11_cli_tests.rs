//! Regression tests for the CLI / PowerShell / COM surface defect fixes (wave 11).

use std::path::PathBuf;

use super::*;

fn temp_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("pinget-wave11-{}-{name}", std::process::id()))
}

// ── R11-2: WinGet common flags are accepted ──

#[test]
fn winget_common_flags_are_accepted_on_any_command() {
    let cli = Cli::try_parse_from([
        "pinget",
        "install",
        "--id",
        "Git.Git",
        "-e",
        "--accept-source-agreements",
        "--accept-package-agreements",
        "--disable-interactivity",
        "--wait",
        "--verbose-logs",
        "--nowarn",
        "--no-vt",
        "--no-progress",
    ])
    .expect("WinGet common flags should parse");
    assert!(cli.common.disable_interactivity);
    assert!(cli.common.ignore_warnings);
    assert!(matches!(cli.command, Some(Commands::Install(_))));

    for argv in [
        vec!["pinget", "--accept-source-agreements", "search", "git"],
        vec!["pinget", "list", "--verbose"],
        vec![
            "pinget",
            "upgrade",
            "--all",
            "--accept-source-agreements",
            "--disable-interactivity",
        ],
        vec![
            "pinget",
            "source",
            "add",
            "corp",
            "https://corp.example",
            "--accept-source-agreements",
        ],
        vec!["pinget", "import", "-i", "packages.json", "--accept-source-agreements"],
        vec!["pinget", "validate", "manifest.yaml", "--ignore-warnings"],
    ] {
        assert!(Cli::try_parse_from(&argv).is_ok(), "{argv:?} should parse");
    }

    let defaults = Cli::try_parse_from(["pinget", "list"]).expect("parse list");
    assert!(!defaults.common.disable_interactivity);
    assert!(!defaults.common.ignore_warnings);
}

#[test]
fn disable_interactivity_suppresses_the_agreements_prompt() {
    assert!(agreements_prompt_allowed(false, true, true));
    assert!(!agreements_prompt_allowed(true, true, true));
    assert!(!agreements_prompt_allowed(false, false, true));
    assert!(!agreements_prompt_allowed(false, true, false));
}

// ── R11-3: no truncation when stdout is not a console ──

#[test]
fn table_is_not_truncated_without_a_console_width() {
    let long_id = "Microsoft.VisualStudio.2022.Community.Preview.With.A.Very.Long.Identifier";
    let rows = vec![vec![
        "Visual Studio Community 2022 Preview".to_owned(),
        long_id.to_owned(),
        "17.10.0".to_owned(),
    ]];

    let redirected = render_table(&["Name", "Id", "Version"], &rows, None);
    assert!(redirected[2].contains(long_id), "{redirected:?}");

    let narrow = render_table(&["Name", "Id", "Version"], &rows, Some(60));
    assert!(!narrow[2].contains(long_id));
    assert!(narrow[1].len() < 60);
}

#[test]
fn table_uses_the_full_console_width() {
    // 59 + 1 + 59 = 119 columns: fits a 120-column console without shrinking.
    let rows = vec![vec!["a".repeat(59), "b".repeat(59)]];
    let lines = render_table(&["Name", "Id"], &rows, Some(120));
    assert_eq!(lines[2], format!("{} {}", "a".repeat(59), "b".repeat(59)));
}

// ── R11-5: source add defaults to Microsoft.PreIndexed.Package ──

#[test]
fn source_add_without_type_defaults_to_preindexed() {
    let cli = Cli::try_parse_from(["pinget", "source", "add", "corp", "https://corp.example/winget"]).expect("parse");
    let Some(Commands::Source {
        command: SourceCommands::Add(args),
    }) = cli.command
    else {
        panic!("expected source add");
    };
    assert_eq!(parse_source_kind(&args.kind).expect("kind"), SourceKind::PreIndexed);

    let cli = Cli::try_parse_from([
        "pinget",
        "source",
        "add",
        "corp",
        "https://x",
        "--type",
        "Microsoft.Rest",
    ])
    .expect("parse");
    let Some(Commands::Source {
        command: SourceCommands::Add(args),
    }) = cli.command
    else {
        panic!("expected source add");
    };
    assert_eq!(parse_source_kind(&args.kind).expect("kind"), SourceKind::Rest);
}

// ── R11-8: validate keeps PackageVersion as text ──

#[test]
fn validate_parses_numeric_looking_versions_as_strings() {
    let value = parse_manifest_for_validation(
        b"\xEF\xBB\xBFPackageIdentifier: Contoso.Tool\nPackageVersion: 1.10\nManifestType: singleton\nManifestVersion: 1.6.0\nInstallers:\n  - InstallerUrl: https://example.test/a.exe\n",
    )
    .expect("parse");
    assert_eq!(value["PackageVersion"], serde_json::Value::String("1.10".to_owned()));

    let schema = serde_json::json!({
        "type": "object",
        "properties": { "PackageVersion": { "type": "string" } }
    });
    let validator = jsonschema::validator_for(&schema).expect("schema");
    assert!(validator.is_valid(&value));

    let value =
        parse_manifest_for_validation(b"PackageIdentifier: Contoso.Tool\nPackageVersion: 2.0\n").expect("parse");
    assert_eq!(value["PackageVersion"], serde_json::Value::String("2.0".to_owned()));
}

#[test]
fn validate_reports_missing_required_fields() {
    let dir = temp_path("validate");
    std::fs::create_dir_all(&dir).expect("create dir");
    let good = dir.join("good.yaml");
    std::fs::write(
        &good,
        "PackageIdentifier: Contoso.Tool\nPackageVersion: 2.0\nManifestType: singleton\nManifestVersion: 1.6.0\nInstallers:\n  - InstallerUrl: https://example.test/a.exe\n",
    )
    .expect("write");
    let report = validate_manifest_path(good.to_str().expect("utf8")).expect("validate");
    assert!(report.errors.is_empty(), "{:?}", report.errors);

    let bad = dir.join("bad.yaml");
    std::fs::write(
        &bad,
        "PackageIdentifier: Contoso.Tool\nManifestType: singleton\nManifestVersion: 1.6.0\n",
    )
    .expect("write");
    let report = validate_manifest_path(bad.to_str().expect("utf8")).expect("validate");
    assert!(
        report.errors.iter().any(|e| e.contains("'Installers'")),
        "{:?}",
        report.errors
    );
    assert!(
        report.errors.iter().any(|e| e.contains("'PackageVersion'")),
        "{:?}",
        report.errors
    );

    let _ = std::fs::remove_dir_all(&dir);
}

// ── R11-11: import accepts BOMs and UTF-16 ──

#[test]
fn import_file_decoding_handles_boms_and_utf16() {
    let json = r#"{"Sources":[]}"#;
    assert_eq!(decode_import_file(json.as_bytes()).expect("plain"), json);

    let mut utf8_bom = b"\xEF\xBB\xBF".to_vec();
    utf8_bom.extend_from_slice(json.as_bytes());
    assert_eq!(decode_import_file(&utf8_bom).expect("utf8 bom"), json);

    let mut little_endian = vec![0xFF, 0xFE];
    little_endian.extend(json.encode_utf16().flat_map(u16::to_le_bytes));
    assert_eq!(decode_import_file(&little_endian).expect("utf16le"), json);

    let mut big_endian = vec![0xFE, 0xFF];
    big_endian.extend(json.encode_utf16().flat_map(u16::to_be_bytes));
    assert_eq!(decode_import_file(&big_endian).expect("utf16be"), json);

    assert!(decode_import_file(&[0xFF, 0xFE, 0x41]).is_err());
}

#[test]
fn import_of_a_bom_prefixed_file_reaches_the_parser() {
    let path = temp_path("import-bom.json");
    let mut bytes = b"\xEF\xBB\xBF".to_vec();
    bytes.extend_from_slice(br#"{"NotSources":[]}"#);
    std::fs::write(&path, &bytes).expect("write");

    let content = decode_import_file(&std::fs::read(&path).expect("read")).expect("decode");
    let parsed: serde_json::Value = serde_json::from_str(&content).expect("BOM must not reach serde_json");
    assert!(parsed.get("NotSources").is_some());

    let _ = std::fs::remove_file(&path);
}
