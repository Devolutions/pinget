use std::path::PathBuf;

use super::*;

fn temp_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("pinget-wave20-{}-{name}", std::process::id()))
}

fn parses(args: &[&str]) -> bool {
    let mut full = vec!["pinget"];
    full.extend_from_slice(args);
    Cli::try_parse_from(full).is_ok()
}

// ── W20-1: table columns measure East Asian wide characters as two columns ──

#[test]
fn table_measures_wide_characters_as_two_columns() {
    let rows = vec![vec!["中文名".to_owned(), "A".to_owned()]];
    let lines = render_table(&["Name", "Id"], &rows, None);
    assert_eq!(lines[0], "Name   Id");
    assert_eq!(lines[1], "-".repeat(9));
    assert_eq!(lines[2], "中文名 A");
}

#[test]
fn table_truncates_wide_characters_to_the_column_width() {
    // Name column shrinks to 4 columns: "中" (2) + "…" (1) + 1 padding column.
    let rows = vec![vec!["中文名字".to_owned(), "x".to_owned()]];
    let lines = render_table(&["Name", "Id"], &rows, Some(8));
    assert_eq!(lines[2], "中…  x");
    assert_eq!(display_width(&lines[2]), 6);
}

// ── W20-2: truncation ends with an ellipsis, not a dot ──

#[test]
fn table_truncation_uses_an_ellipsis() {
    let rows = vec![vec!["abcdefghij".to_owned(), "x".to_owned()]];
    let lines = render_table(&["Name", "Id"], &rows, Some(9));
    assert_eq!(lines[2], "abcd\u{2026} x");
    assert_eq!(truncate("Microsoft.VisualStudio", 12), "Microsoft.V\u{2026}");
}

// ── W20-3: export sorts packages by id and drops duplicate ids without versions ──

fn installed(id: &str, version: &str) -> ListMatch {
    ListMatch {
        name: id.to_owned(),
        id: id.to_owned(),
        local_id: id.to_owned(),
        installed_version: version.to_owned(),
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

fn exported_ids(payload: &serde_json::Value) -> Vec<(String, Option<String>)> {
    payload["Sources"][0]["Packages"]
        .as_array()
        .expect("packages")
        .iter()
        .map(|package| {
            (
                package["PackageIdentifier"].as_str().expect("id").to_owned(),
                package.get("Version").and_then(|v| v.as_str()).map(str::to_owned),
            )
        })
        .collect()
}

#[test]
fn export_sorts_by_id_and_collapses_side_by_side_installs() {
    let rows = vec![
        installed("Python.Python.3.12", "3.12.1"),
        installed("Git.Git", "2.45.0"),
        installed("Python.Python.3.12", "3.12.0"),
    ];

    let (payload, count) = build_export_payload(&rows, &[], None, false, "2026-11-15T00:00:00.000");
    assert_eq!(count, 2);
    assert_eq!(
        exported_ids(&payload),
        [("Git.Git".to_owned(), None), ("Python.Python.3.12".to_owned(), None)]
    );

    let (payload, count) = build_export_payload(&rows, &[], None, true, "2026-11-15T00:00:00.000");
    assert_eq!(count, 3);
    assert_eq!(
        exported_ids(&payload),
        [
            ("Git.Git".to_owned(), Some("2.45.0".to_owned())),
            ("Python.Python.3.12".to_owned(), Some("3.12.1".to_owned())),
            ("Python.Python.3.12".to_owned(), Some("3.12.0".to_owned())),
        ]
    );
}

// ── W20-4: import reads schema 1.0 files (`Id` instead of `PackageIdentifier`) ──

#[derive(Default)]
struct RecordingImportRepository {
    searched: Vec<String>,
}

impl ImportRepository for RecordingImportRepository {
    fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse> {
        self.searched.push(query.id.clone().unwrap_or_default());
        Ok(SearchResponse {
            matches: Vec::new(),
            warnings: Vec::new(),
            truncated: false,
        })
    }

    fn install_request(&mut self, _request: &InstallRequest) -> Result<InstallResult> {
        panic!("dry-run must not install")
    }
}

#[test]
fn import_reads_schema_1_0_package_ids() {
    let payload = r#"{
        "$schema": "https://aka.ms/winget-packages.schema.1.0.json",
        "WinGetVersion": "1.0.0",
        "Sources": [{
            "SourceDetails": {"Name": "winget", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe",
                              "Argument": "https://cdn.winget.microsoft.com/cache", "Type": "Microsoft.PreIndexed.Package"},
            "Packages": [{"Id": "Git.Git"}, {"Id": "Microsoft.PowerToys", "Version": "0.75.0"}]
        }]
    }"#;
    let mut repository = RecordingImportRepository::default();
    do_import_from_content(&mut repository, payload, true, false, false, false, false).expect("dry-run import");
    assert_eq!(repository.searched, ["Git.Git", "Microsoft.PowerToys"]);
}

// ── W20-5: a failed package action never exits with 0 ──

fn action_result(success: bool, no_op: bool, exit_code: i32) -> InstallResult {
    InstallResult {
        package_id: "Contoso.App".to_owned(),
        version: "1.0".to_owned(),
        installer_path: PathBuf::new(),
        installer_type: "exe".to_owned(),
        exit_code,
        success,
        no_op,
        reboot_required: false,
        warnings: Vec::new(),
    }
}

#[test]
fn failed_package_action_with_zero_exit_code_exits_with_one() {
    assert_eq!(package_action_exit_code(&action_result(false, false, 0)), Some(1));
    assert_eq!(package_action_exit_code(&action_result(false, false, 1603)), Some(1603));
    assert_eq!(package_action_exit_code(&action_result(true, false, 0)), None);
    assert_eq!(package_action_exit_code(&action_result(true, false, 3010)), None);
    assert_eq!(package_action_exit_code(&action_result(false, true, 0)), None);
}

// ── W20-6: list --include-unknown / --include-pinned need --upgrade-available ──

#[test]
fn list_include_flags_require_upgrade_available() {
    assert!(!parses(&["list", "--include-unknown"]));
    assert!(!parses(&["list", "-u"]));
    assert!(!parses(&["list", "--include-pinned"]));
    assert!(parses(&[
        "list",
        "--upgrade-available",
        "--include-unknown",
        "--include-pinned"
    ]));
    assert!(parses(&["list", "--upgrade-available"]));
    // `upgrade` itself takes them without a dependency.
    assert!(parses(&["upgrade", "--include-unknown", "--include-pinned"]));
}

// ── W20-7: WinGet exclusive argument sets ──
// (W20-8, C# parse errors being ignored, has no Rust counterpart: clap already reports them.)

// uninstall: PurgePreserve and AllAndTargetVersion

#[test]
fn uninstall_rejects_mutually_exclusive_arguments() {
    assert!(!parses(&["uninstall", "Contoso.App", "--purge", "--preserve"]));
    assert!(!parses(&[
        "uninstall",
        "Contoso.App",
        "--all-versions",
        "--version",
        "1.0"
    ]));
    assert!(!parses(&["uninstall", "Contoso.App", "--all", "-v", "1.0"]));
    assert!(parses(&["uninstall", "Contoso.App", "--purge"]));
    assert!(parses(&["uninstall", "Contoso.App", "--preserve"]));
    assert!(parses(&["uninstall", "Contoso.App", "--all-versions"]));
    assert!(parses(&["uninstall", "Contoso.App", "--version", "1.0"]));
}

// pin add: PinType (--version | --blocking)

#[test]
fn pin_add_rejects_version_with_blocking() {
    assert!(!parses(&[
        "pin",
        "add",
        "--id",
        "Contoso.App",
        "--version",
        "1.2.*",
        "--blocking"
    ]));
    assert!(parses(&["pin", "add", "--id", "Contoso.App", "--version", "1.2.*"]));
    assert!(parses(&["pin", "add", "--id", "Contoso.App", "--blocking"]));
}

// install: DependenciesConflict (--skip-dependencies | --dependencies-only)

#[test]
fn install_dependency_flags_are_exclusive_and_use_wingets_name() {
    assert!(!parses(&[
        "install",
        "Contoso.App",
        "--skip-dependencies",
        "--dependencies-only"
    ]));
    assert!(!parses(&[
        "install",
        "Contoso.App",
        "--skip-dependencies",
        "--dependencies"
    ]));
    assert!(parses(&["install", "Contoso.App", "--dependencies-only"]));
    assert!(parses(&["install", "Contoso.App", "--dependencies"]));
    assert!(parses(&["install", "Contoso.App", "--skip-dependencies"]));
}

// ── W20-9: hash labels its output like WinGet and accepts --file/-f and -m ──

#[test]
fn hash_output_and_arguments_match_winget() {
    let path = temp_path("hash.bin");
    std::fs::write(&path, b"abc").expect("write temp file");
    let lines = hash_lines(path.to_str().expect("utf-8 path"), false).expect("hash");
    std::fs::remove_file(&path).ok();
    assert_eq!(
        lines,
        ["InstallerSha256: ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"]
    );

    assert!(parses(&["hash", "setup.exe"]));
    assert!(parses(&["hash", "--file", "setup.exe"]));
    assert!(parses(&["hash", "-f", "setup.msix", "-m"]));
    assert!(!parses(&["hash"]));
    assert!(!parses(&["hash", "a.exe", "--file", "b.exe"]));
}

// ── W20-10: import/export take their file positionally or by name ──

#[test]
fn import_and_export_accept_the_file_positionally_or_by_name() {
    for form in [
        &["export", "packages.json"][..],
        &["export", "-o", "packages.json"],
        &["export", "--output", "packages.json", "--include-versions"],
    ] {
        let mut full = vec!["pinget"];
        full.extend_from_slice(form);
        let cli = Cli::try_parse_from(full).unwrap_or_else(|e| panic!("{form:?}: {e}"));
        assert_eq!(resolve_output_format(&cli).expect("format"), OutputFormat::Text);
        match cli.command {
            Some(Commands::Export(args)) => assert_eq!(export_output_file(&args), "packages.json"),
            _ => panic!("expected export"),
        }
    }
    assert!(!parses(&["export"]));
    assert!(!parses(&["export", "a.json", "-o", "b.json"]));

    for form in [
        &["import", "packages.json"][..],
        &["import", "-i", "packages.json"],
        &["import", "--import-file", "packages.json", "--dry-run"],
    ] {
        let mut full = vec!["pinget"];
        full.extend_from_slice(form);
        let cli = Cli::try_parse_from(full).unwrap_or_else(|e| panic!("{form:?}: {e}"));
        match cli.command {
            Some(Commands::Import(args)) => assert_eq!(import_file_path(&args), "packages.json"),
            _ => panic!("expected import"),
        }
    }
    assert!(!parses(&["import"]));
    assert!(!parses(&["import", "a.json", "-i", "b.json"]));
}
