//! Wave 21 publisher regression tests.

// Keep the private encoder test-only; do not expose it through the public API.
#[cfg(any(target_os = "linux", target_os = "macos"))]
#[path = "mszip_interop_tests.rs"]
mod mszip_interop_tests;

use super::*;

const HASH: &str = "1111111111111111111111111111111111111111111111111111111111111111";

fn wave21_directory(label: &str) -> PathBuf {
    let name = format!(
        "pinget-publisher-wave21-{label}-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
    );
    let directory = std::env::temp_dir().join(name);
    fs::create_dir(&directory).expect("create test directory");
    directory
}

/// A singleton manifest; `root` is inserted at the root and `installers` replaces the installer list.
fn manifest(root: &str, installers: &str) -> String {
    format!(
        "PackageIdentifier: Pinget.Wave21\nPackageVersion: 1.0.0\nPackageLocale: en-US\nPackageName: Wave Twenty-One\n\
         Publisher: Pinget\nLicense: MIT\nShortDescription: Wave 21\n{root}Installers:\n{installers}\
         ManifestType: singleton\nManifestVersion: 1.9.0\n"
    )
}

fn installer(extra: &str) -> String {
    format!(
        "  - Architecture: x64\n    InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    \
         InstallerSha256: {HASH}\n{extra}"
    )
}

fn fields_of(label: &str, yaml: &str) -> CatalogFields {
    let root = wave21_directory(label);
    fs::write(root.join("manifest.yaml"), yaml).expect("write manifest");
    let packages = scan_manifests(&root).and_then(|(_, groups)| {
        groups
            .into_iter()
            .map(|((id, _), documents)| merge_documents(&id, documents))
            .collect::<Result<Vec<_>>>()
    });
    fs::remove_dir_all(root).expect("cleanup");
    catalog_fields_all(&packages.expect("merge")).expect("catalog fields")
}

#[test]
fn r21_1_agreement_fields_keep_their_schema_string_text() {
    let value = parse_manifest_yaml("Agreements:\n  - AgreementLabel: 1.10\n    Agreement: 0x10\n").expect("parse");
    let agreement = value["Agreements"][0].as_mapping().expect("agreement mapping");
    assert_eq!(
        map_get(agreement, "AgreementLabel"),
        Some(&Value::String("1.10".to_owned()))
    );
    assert_eq!(map_get(agreement, "Agreement"), Some(&Value::String("0x10".to_owned())));
}

#[test]
fn r21_2_manifest_after_an_empty_yaml_document_is_rejected() {
    let root = wave21_directory("leading-empty");
    fs::write(
        root.join("manifest.yaml"),
        format!("---\n---\n{}", manifest("", &installer(""))),
    )
    .expect("write manifest");
    let rejected = scan_manifests(&root);
    fs::write(
        root.join("manifest.yaml"),
        format!("---\n{}", manifest("", &installer(""))),
    )
    .expect("write manifest");
    let accepted = scan_manifests(&root);
    fs::remove_dir_all(root).expect("cleanup");
    assert!(
        matches!(rejected, Err(PublisherError::Invalid(ref message)) if message.contains("expected one YAML document")),
        "{:?}",
        rejected.map(|_| ())
    );
    assert!(accepted.is_ok(), "a single explicit document stays valid");
}

#[test]
fn r21_3_installer_commands_replace_root_commands() {
    let overriding = manifest("Commands:\n  - root\n", &installer("    Commands:\n      - own\n"));
    assert_eq!(fields_of("commands-override", &overriding).1, ["own"]);

    let inheriting = manifest(
        "Commands:\n  - root\n",
        &format!("{}{}", installer("    Commands:\n      - own\n"), installer("")),
    );
    assert_eq!(fields_of("commands-inherit", &inheriting).1, ["own", "root"]);
}

#[test]
fn r21_4_arp_display_names_are_also_indexed_with_their_architecture() {
    let yaml = manifest(
        "",
        &installer("    AppsAndFeaturesEntries:\n      - DisplayName: Contoso App (x64)\n"),
    );
    let names = fields_of("arch-names", &yaml).5;
    assert!(names.contains(&"contosoapp".to_owned()), "{names:?}");
    assert!(names.contains(&"contosoapp(X64)".to_owned()), "{names:?}");
    // The package name itself carries no architecture variant.
    assert!(
        !names.iter().any(|name| name.starts_with("wavetwentyone(")),
        "{names:?}"
    );
}

#[test]
fn r21_6_explicit_psign_tool_path_is_absolute() {
    let found = psign::find_psign_tool(Some(Path::new("Cargo.toml"))).expect("existing file");
    assert!(found.is_absolute(), "{}", found.display());
    assert!(found.ends_with("Cargo.toml"));
}

#[test]
fn build_rejects_reuse_of_legacy_portable_snapshot_without_mutation() {
    let root = wave21_directory("legacy-mszip");
    let input = root.join("input");
    let state = root.join("state");
    fs::create_dir(&input).expect("input");
    fs::write(input.join("manifest.yaml"), manifest("", &installer(""))).expect("manifest");
    let options = BuildOptions {
        input,
        state: state.clone(),
    };
    let report = build(&options).expect("initial build");
    let snapshot = report.snapshot_directory;
    let index_path = snapshot.join("Public").join("index.db");
    let database = Connection::open(&index_path).expect("index");
    let (id, hash): (String, Vec<u8>) = database
        .query_row("SELECT id,hash FROM packages", [], |row| Ok((row.get(0)?, row.get(1)?)))
        .expect("package");
    let old_hash = hash.iter().map(|byte| format!("{byte:02x}")).collect::<String>();
    let old_relative = format!("packages/{id}/{}/versionData.mszyml", &old_hash[..8]);
    let old_path = snapshot.join("feed").join(&old_relative);
    let mut legacy = fs::read(&old_path).expect("sidecar");
    legacy[4..8].fill(0);
    let digest = Sha256::digest(&legacy);
    let legacy_hash = sha256_hex(&legacy);
    let legacy_relative = format!("packages/{id}/{}/versionData.mszyml", &legacy_hash[..8]);
    assert_ne!(old_relative, legacy_relative);
    let legacy_path = snapshot.join("feed").join(&legacy_relative);
    fs::create_dir_all(legacy_path.parent().expect("sidecar parent")).expect("sidecar directory");
    fs::write(&legacy_path, &legacy).expect("legacy sidecar");
    fs::remove_file(&old_path).expect("old sidecar");
    fs::remove_dir(old_path.parent().expect("old sidecar parent")).expect("old sidecar directory");
    database
        .execute(
            "UPDATE packages SET hash=?1 WHERE id=?2",
            params![digest.as_slice(), id],
        )
        .expect("legacy hash");
    drop(database);

    let metadata_path = snapshot.join("snapshot.json");
    let mut metadata: SnapshotMetadata =
        serde_json::from_slice(&fs::read(&metadata_path).expect("metadata")).expect("parse metadata");
    metadata.artifacts.remove(&old_relative).expect("old artifact");
    metadata.artifacts.insert(legacy_relative, legacy_hash);
    metadata.index_hash = sha256_hex(&fs::read(index_path).expect("index bytes"));
    fs::write(
        &metadata_path,
        serde_json::to_vec_pretty(&metadata).expect("serialize metadata"),
    )
    .expect("metadata");
    let snapshot_before = hash_tree(&snapshot).expect("snapshot inventory");
    let state_before = fs::read(state.join("publisher.db")).expect("state bytes");

    let error = build(&options).expect_err("legacy snapshot must not be reused");
    assert!(error.to_string().contains("fresh --state"), "{error}");
    assert_eq!(hash_tree(&snapshot).expect("preserved snapshot"), snapshot_before);
    assert_eq!(
        fs::read(state.join("publisher.db")).expect("preserved state"),
        state_before
    );
    assert_eq!(fs::read(legacy_path).expect("preserved legacy sidecar"), legacy);
    fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn publication_rejects_same_build_and_version_with_different_index() {
    let root = wave21_directory("same-build-different-index");
    let input = root.join("input");
    let first_state = root.join("first-state");
    let second_state = root.join("second-state");
    let destination = root.join("destination");
    fs::create_dir(&input).expect("input");
    fs::write(input.join("manifest.yaml"), manifest("", &installer(""))).expect("manifest");
    let first = build(&BuildOptions {
        input: input.clone(),
        state: first_state.clone(),
    })
    .expect("first build");
    let second = build(&BuildOptions {
        input,
        state: second_state.clone(),
    })
    .expect("fresh-state build");
    assert_eq!(first.build_id, second.build_id);

    let index = second.snapshot_directory.join("Public").join("index.db");
    let original_hash = sha256_hex(&fs::read(&index).expect("original index"));
    let connection = Connection::open(&index).expect("open index");
    connection
        .execute_batch("PRAGMA user_version=31415")
        .expect("change index bytes");
    drop(connection);
    let index_hash = sha256_hex(&fs::read(&index).expect("changed index"));
    assert_ne!(original_hash, index_hash);
    let metadata_path = second.snapshot_directory.join("snapshot.json");
    let mut metadata: SnapshotMetadata =
        serde_json::from_slice(&fs::read(&metadata_path).expect("metadata")).expect("parse metadata");
    metadata.index_hash = index_hash.clone();
    fs::write(
        &metadata_path,
        serde_json::to_vec_pretty(&metadata).expect("serialize metadata"),
    )
    .expect("metadata");

    let package_options = |state: &Path, version: &str, filename: &str, dest: Option<PathBuf>| PackageOptions {
        state: state.to_path_buf(),
        build_id: Some(first.build_id.clone()),
        msix: root.join(filename),
        name: "Pinget.Test.IndexRevision".to_owned(),
        publisher: "CN=Pinget Index Revision".to_owned(),
        version: version.to_owned(),
        destination: dest,
    };
    let initial_package = package_options(&first_state, "1.0.0.0", "initial.msix", None);
    package(&initial_package).expect("initial package");
    let first_publish = PublishOptions {
        state: first_state,
        destination: destination.clone(),
        build_id: Some(first.build_id.clone()),
        msix: initial_package.msix,
        unsigned_development: true,
        psign_tool: None,
        trusted_ca: Vec::new(),
    };
    assert!(!publish(&first_publish).expect("initial publication").reused);
    assert!(publish(&first_publish).expect("same-index retry").reused);
    let before = hash_tree(&destination).expect("published inventory");

    let preflight = package_options(&second_state, "1.0.0.0", "preflight.msix", Some(destination.clone()));
    let error = package(&preflight).expect_err("same version with different index must fail preflight");
    assert!(
        error.to_string().contains("different build content or index"),
        "{error}"
    );
    assert!(!preflight.msix.exists());
    assert_eq!(hash_tree(&destination).expect("preserved destination"), before);

    let candidate = package_options(&second_state, "1.0.0.0", "candidate.msix", None);
    package(&candidate).expect("package without destination preflight");
    let mut next = PublishOptions {
        state: second_state.clone(),
        destination: destination.clone(),
        build_id: Some(second.build_id),
        msix: candidate.msix,
        unsigned_development: true,
        psign_tool: None,
        trusted_ca: Vec::new(),
    };
    let error = publish(&next).expect_err("same version with different index must not be reused");
    assert!(
        error.to_string().contains("different build content or index"),
        "{error}"
    );
    assert_eq!(hash_tree(&destination).expect("preserved destination"), before);

    let upgraded = package_options(&second_state, "1.0.0.1", "upgraded.msix", Some(destination.clone()));
    package(&upgraded).expect("higher-version preflight");
    next.msix = upgraded.msix;
    assert!(!publish(&next).expect("higher-version publication").reused);
    let marker = read_owner_marker(&destination.join(".pinget-publisher.json"))
        .expect("marker")
        .expect("owned destination");
    assert_eq!(marker.index_hash, index_hash);
    assert_eq!(marker.package_version, "1.0.0.1");
    fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn r21_8_catalog_terms_and_arp_values_are_trimmed_like_winget() {
    let yaml = manifest(
        "Tags:\n  - ' padded '\n",
        &installer(
            "    Commands:\n      - \"\\tcmd \"\n    AppsAndFeaturesEntries:\n      - DisplayVersion: ' 1.2 '\n        \
             ProductCode: ' {ABC} '\n        UpgradeCode: ' {DEF} '\n",
        ),
    );
    let fields = fields_of("trim", &yaml);
    assert_eq!(fields.0, ["padded"]);
    assert_eq!(fields.1, ["cmd"]);
    assert_eq!(fields.3, ["{abc}"]);
    assert_eq!(fields.4, ["{def}"]);
    assert_eq!(fields.7.as_deref(), Some("1.2"));
    assert_eq!(fields.8.as_deref(), Some("1.2"));
}

#[cfg(windows)]
fn build_tree(label: &str, yaml: &str) -> (PathBuf, PathBuf, BuildReport) {
    let root = wave21_directory(label);
    let input = root.join("input");
    fs::create_dir(&input).expect("input");
    fs::write(input.join("manifest.yaml"), yaml).expect("write manifest");
    let state = root.join("state");
    let report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("build");
    (root, state, report)
}

#[cfg(windows)]
#[test]
fn r21_7_build_recreates_a_removed_snapshot_of_unchanged_input() {
    let (root, state, report) = build_tree("removed-snapshot", &manifest("", &installer("")));
    let snapshot = state.join("builds").join(&report.build_id);
    fs::rename(&snapshot, root.join("moved-snapshot")).expect("move snapshot away");
    let rebuilt = build(&BuildOptions {
        input: root.join("input"),
        state: state.clone(),
    });
    let snapshot_restored = snapshot.join("snapshot.json").is_file();
    let verified = verify(&state, None, None).is_ok();
    fs::remove_dir_all(&root).expect("cleanup");
    assert_eq!(rebuilt.expect("rebuild").build_id, report.build_id);
    assert!(snapshot_restored);
    assert!(verified);
}

#[cfg(windows)]
#[test]
fn r21_9_package_row_trims_name_and_moniker_and_omits_an_empty_moniker() {
    let package_row = |label: &str, root_fields: &str| {
        let yaml = manifest(root_fields, &installer("")).replace("PackageName: Wave Twenty-One\n", "");
        let (root, state, report) = build_tree(label, &yaml);
        let index = state
            .join("builds")
            .join(&report.build_id)
            .join("Public")
            .join("index.db");
        let row: (String, Option<String>) =
            Connection::open_with_flags(index, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
                .expect("index")
                .query_row("SELECT name,moniker FROM packages", [], |row| {
                    Ok((row.get(0)?, row.get(1)?))
                })
                .expect("package row");
        fs::remove_dir_all(&root).expect("cleanup");
        row
    };
    assert_eq!(
        package_row("padded-row", "PackageName: ' Spaced Name '\nMoniker: ' spaced '\n"),
        ("Spaced Name".to_owned(), Some("spaced".to_owned()))
    );
    assert_eq!(
        package_row("empty-moniker", "PackageName: Named\nMoniker: ''\n"),
        ("Named".to_owned(), None)
    );
}
