//! Wave 21 publisher regression tests.

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
