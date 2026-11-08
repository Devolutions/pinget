//! Review fixes C regression tests: pin source filters and casing, source-cache
//! path safety, and portable directory tracking.

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let root = std::env::temp_dir().join(format!(
        "pinget-rs-review-c-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ));
    let _ = fs::remove_dir_all(&root);
    fs::create_dir_all(&root).expect("create temp root");
    root
}

fn open_repository(app_root: &Path) -> Repository {
    Repository::open_with_options(RepositoryOptions::new(app_root.to_path_buf())).expect("open repository")
}

fn pin_ids(pins: &[PinRecord]) -> Vec<(String, String)> {
    let mut ids: Vec<(String, String)> = pins
        .iter()
        .map(|pin| (pin.package_id.clone(), pin.source_id.clone()))
        .collect();
    ids.sort();
    ids
}

// ── #4: None = no source filter, "" = the source-less pin ──

#[test]
fn empty_source_id_selects_only_source_less_pins() {
    let root = temp_root("pin-empty-source");
    let repository = open_repository(&root);
    repository
        .add_pin("Contoso.Tool", "1.0", "winget", PinType::Pinning)
        .expect("add winget pin");
    repository
        .add_pin("Contoso.Tool", "*", "", PinType::Blocking)
        .expect("add source-less pin");

    assert_eq!(repository.list_pins(None).expect("list all").len(), 2);
    assert_eq!(
        pin_ids(&repository.list_pins(Some("")).expect("list source-less")),
        [("Contoso.Tool".to_owned(), String::new())]
    );

    assert!(
        repository
            .remove_pin("Contoso.Tool", Some(""))
            .expect("remove source-less")
    );
    assert_eq!(
        pin_ids(&repository.list_pins(None).expect("list remaining")),
        [("Contoso.Tool".to_owned(), "winget".to_owned())]
    );

    repository
        .add_pin("Fabrikam.Tool", "*", "", PinType::Pinning)
        .expect("add another source-less pin");
    repository.reset_pins(Some("")).expect("reset source-less");
    assert_eq!(
        pin_ids(&repository.list_pins(None).expect("list after reset")),
        [("Contoso.Tool".to_owned(), "winget".to_owned())]
    );

    assert!(repository.remove_pin("contoso.tool", None).expect("remove everywhere"));
    assert!(repository.list_pins(None).expect("list empty").is_empty());
    let _ = fs::remove_dir_all(root);
}

// ── #4: add_pin replaces a pin whose id/source differ only by case ──

#[test]
fn add_pin_replaces_case_variant_pin_instead_of_duplicating() {
    let root = temp_root("pin-case");
    let repository = open_repository(&root);
    repository
        .add_pin("contoso.tool", "1.0", "WINGET", PinType::Pinning)
        .expect("add pin");
    repository
        .add_pin("Contoso.Tool", "*", "winget", PinType::Blocking)
        .expect("replace pin");

    let pins = repository.list_pins(None).expect("list pins");
    assert_eq!(pins.len(), 1, "{pins:?}");
    assert_eq!(pins[0].package_id, "Contoso.Tool");
    assert_eq!(pins[0].source_id, "winget");
    assert_eq!(pins[0].pin_type, PinType::Blocking);

    // Other packages and sources are untouched, and WinGet's index is kept.
    repository
        .add_pin("Contoso.Tool", "1.0", "msstore", PinType::Pinning)
        .expect("add other source");
    repository
        .add_pin("Contoso.Other", "1.0", "winget", PinType::Pinning)
        .expect("add other package");
    assert_eq!(repository.list_pins(None).expect("list pins").len(), 3);
    let conn = Repository::open_sqlite_connection(pins_db_path(&root)).expect("open pin db");
    let indexes = query_rows(
        &conn,
        "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'pin'",
        Vec::new(),
        |row| row_string(row, 0),
    )
    .expect("indexes");
    assert!(indexes.iter().any(|name| name == "pin_index"), "{indexes:?}");
    drop(conn);
    let _ = fs::remove_dir_all(root);
}

// ── #6: source index path segments must be Windows-safe file names ──

#[test]
fn source_relative_paths_reject_windows_unsafe_segments() {
    for path in [
        "manifests/c/foo./a.yaml",
        "manifests/c/foo /a.yaml",
        "manifests/CON/a.yaml",
        "manifests/c/com1.yaml",
        "manifests/c/NUL.txt",
        "manifests/c/lpt9",
        "manifests/c/a?b.yaml",
        "manifests/c/a#b.yaml",
        "manifests/c/a*b.yaml",
        "manifests/c/a\"b.yaml",
        "manifests/c/a<b.yaml",
        "manifests/c/a>b.yaml",
        "manifests/c/a|b.yaml",
        "manifests/c/a\u{1}b.yaml",
        "manifests//a.yaml",
    ] {
        assert!(
            validated_source_relative_segments(path).is_err(),
            "{path:?} should be rejected"
        );
    }

    assert_eq!(
        validated_source_relative_segments("manifests/c/Contoso/App/1.0.0/Contoso.App.installer.yaml").expect("safe"),
        [
            "manifests",
            "c",
            "Contoso",
            "App",
            "1.0.0",
            "Contoso.App.installer.yaml"
        ]
    );
    assert_eq!(
        validated_source_relative_segments("packages/Contoso.Console/0123abcd/versionData.mszyml").expect("safe"),
        ["packages", "Contoso.Console", "0123abcd", "versionData.mszyml"]
    );
}

#[test]
fn sanitize_path_segment_yields_windows_safe_names() {
    assert_eq!(sanitize_path_segment("Contoso.Rest"), "Contoso.Rest");
    assert_eq!(sanitize_path_segment("Contoso."), "Contoso");
    assert_eq!(sanitize_path_segment("Contoso. ."), "Contoso");
    assert_eq!(sanitize_path_segment("a?."), "a_");
    assert_eq!(sanitize_path_segment("a\u{1}b"), "a_b");
    assert_eq!(sanitize_path_segment("CON"), "_CON");
    assert_eq!(sanitize_path_segment("nul.txt"), "_nul.txt");
    assert_eq!(sanitize_path_segment("Com1."), "_Com1");
    assert_eq!(sanitize_path_segment(" lead"), "_ lead");
    assert_eq!(sanitize_path_segment("..."), "_");
    assert_eq!(sanitize_path_segment(" . "), "_");
    assert_eq!(sanitize_path_segment(&"x".repeat(300)), "_");
}

// ── #7: removed obsolete directories are not tracked after an upgrade ──

#[test]
fn retained_portable_directories_drop_removed_obsolete_directories() {
    let root = temp_root("portable-dirs");
    fs::create_dir_all(root.join("bin")).expect("bin");
    fs::create_dir_all(root.join("kept")).expect("kept");
    fs::write(root.join("kept").join("user.txt"), b"user").expect("user file");
    fs::create_dir_all(root.join("old")).expect("old");
    fs::write(root.join("old").join("tool.dll"), b"old").expect("old file");

    let payload = PortablePayload {
        files: vec![r"bin\tool.exe".to_owned()],
        directories: vec!["bin".to_owned()],
    };
    let previous_files = vec![r"bin\tool.exe".to_owned(), r"old\tool.dll".to_owned()];
    let previous_directories = vec!["bin".to_owned(), "kept".to_owned(), "old".to_owned()];
    let planned = plan_portable_payload(&payload, &root, true, &previous_files, &previous_directories).expect("plan");
    assert_eq!(planned, ["bin", "kept", "old"]);

    remove_obsolete_portable_files(&root, &payload, &previous_files, &previous_directories).expect("remove obsolete");
    assert!(!root.join("old").exists());
    assert!(root.join("kept").join("user.txt").exists());

    assert_eq!(retained_portable_directories(&root, &payload, planned), ["bin", "kept"]);
    let _ = fs::remove_dir_all(root);
}
