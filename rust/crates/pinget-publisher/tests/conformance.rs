use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

#[cfg(not(windows))]
use pinget_publisher::PublisherError;
use pinget_publisher::{BuildOptions, build, diff, status, verify};
#[cfg(windows)]
use pinget_publisher::{PublishOptions, Signing, publish, verify_published_destination};
use rusqlite::Connection;
#[cfg(windows)]
use serde_json::Value;

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

#[test]
#[cfg(windows)]
fn build_multifile_manifest_set_emits_complete_v2_index_and_feed() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("multi-file fixture should build");

    assert!(!report.build_id.is_empty());
    assert!(verify(&state, Some(&report.build_id), None).is_ok());
    let latest_build_id = status(&state)
        .expect("status should reopen the newly built state")
        .last_build
        .map(|latest| latest.build_id);
    assert_eq!(latest_build_id.as_deref(), Some(report.build_id.as_str()));

    let snapshot = state.join("builds").join(&report.build_id);
    let index = snapshot.join("Public").join("index.db");
    let feed = snapshot.join("feed");
    assert!(index.is_file());
    let metadata: Value = serde_json::from_slice(
        &fs::read(snapshot.join("snapshot.json")).expect("snapshot metadata should be readable"),
    )
    .expect("snapshot metadata should be JSON");
    assert_eq!(metadata["schemaVersion"].as_u64(), Some(1));
    assert_eq!(metadata["artifactFormatVersion"].as_u64(), Some(2));
    let inventory_json =
        serde_json::to_string(&metadata["inventory"]).expect("snapshot inventory should have canonical JSON");
    let expected_build_id = sha256_hex(format!("pinget-publisher-state-v2\n{inventory_json}").as_bytes());
    let legacy_build_id = sha256_hex(format!("pinget-publisher-state-v1\n{inventory_json}").as_bytes());
    assert_eq!(report.build_id, expected_build_id);
    assert_ne!(report.build_id, legacy_build_id);
    let state_database = Connection::open(state.join("publisher.db")).expect("publisher state database should open");
    let state_artifact_format: String = state_database
        .query_row(
            "SELECT value FROM metadata WHERE name='artifactFormatVersion'",
            [],
            |row| row.get(0),
        )
        .expect("publisher artifact format marker should exist");
    let state_schema_version: String = state_database
        .query_row("SELECT value FROM metadata WHERE name='schemaVersion'", [], |row| {
            row.get(0)
        })
        .expect("publisher schema marker should exist");
    let state_user_version: i64 = state_database
        .query_row("PRAGMA user_version", [], |row| row.get(0))
        .expect("publisher SQLite user_version should be readable");
    assert_eq!(state_artifact_format, "2");
    assert_eq!(state_schema_version, "1");
    assert_eq!(state_user_version, 1);
    let expected_index_hash = sha256_file(&index);
    assert_eq!(
        metadata["indexHash"].as_str(),
        Some(expected_index_hash.as_str()),
        "snapshot metadata must bind the exact v2 index bytes"
    );
    let database = Connection::open_with_flags(&index, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
        .expect("SQLite index should open independently");
    let package: (String, String, String, Vec<u8>) = database
        .query_row("SELECT id,name,latest_version,hash FROM packages", [], |row| {
            Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?))
        })
        .expect("index should contain the package");
    assert_eq!(package.0, "Pinget.Test.Matrix");
    assert_eq!(package.1, "Matrix Package");
    assert_eq!(package.2, "1.10.0");
    assert_eq!(package.3.len(), 32);

    assert_eq!(
        read_mapped_values(&database, "tags2", "tag", "tags2_map", "tag"),
        ["Matrix", "matrice", "publisher-conformance"]
    );
    assert_eq!(
        read_mapped_values(&database, "commands2", "command", "commands2_map", "command"),
        ["Matrix", "OtherCmd", "matrix-cli"]
    );
    let arp_bounds: (String, String) = database
        .query_row(
            "SELECT arp_min_version,arp_max_version FROM packages WHERE id=?1",
            ["Pinget.Test.Matrix"],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .expect("index should contain ARP version bounds");
    // WinGet stores the latest version's ARP range on the package row.
    assert_eq!(arp_bounds, ("1.10.0".to_owned(), "1.10.0".to_owned()));

    for (table, column, expected) in [
        // Stored case-folded, as WinGet does, so lowercase exact lookups match.
        ("pfns2", "pfn", "pinget.matrix_1234567890abc"),
        ("productcodes2", "productcode", "{00112233-4455-6677-8899-aabbccddeeff}"),
        ("upgradecodes2", "upgradecode", "{11223344-5566-7788-99aa-bbccddeeff00}"),
    ] {
        assert_eq!(
            read_correlations(&database, table, column),
            [expected],
            "{table} mapping"
        );
    }
    assert_eq!(
        read_correlations(&database, "norm_names2", "norm_name"),
        ["matrice", "matrixdesktop", "matrixpackage"]
    );
    assert_eq!(
        read_correlations(&database, "norm_publishers2", "norm_publisher"),
        ["pingetlocalizationlab", "pingettestfixtures"]
    );

    let sidecar_hash = encode_hex(&package.3);
    let sidecar = feed
        .join("packages")
        .join("Pinget.Test.Matrix")
        .join(&sidecar_hash[..8])
        .join("versionData.mszyml");
    assert!(sidecar.is_file());
    assert_eq!(sha256_file(&sidecar), sidecar_hash);
    let hosted_manifest_count = count_files_with_extension(&feed.join("manifests"), "yaml");
    assert_eq!(
        hosted_manifest_count, 2,
        "one hosted manifest should be emitted per version"
    );

    let locale_directory = feed.join("manifests/Pinget.Test.Matrix/1.10.0");
    let manifest_path = find_first_file(&locale_directory);
    let manifest: Value = serde_json::from_slice(&fs::read(manifest_path).expect("merged manifest should be readable"))
        .expect("merged manifest should be JSON");
    let installers = manifest["Installers"]
        .as_array()
        .expect("merged manifest should retain installer entries");
    assert_eq!(
        installers[0]["InstallerSha256"].as_str(),
        Some("1111111111111111111111111111111111111111111111111111111111111111"),
        "all-digit InstallerSha256 scalars are schema strings, not numbers"
    );
    assert_eq!(
        installers[1]["InstallerSha256"].as_str(),
        Some("2222222222222222222222222222222222222222222222222222222222222222"),
        "all-digit InstallerSha256 scalars are schema strings, not numbers"
    );
    let older_locale_directory = feed.join("manifests/Pinget.Test.Matrix/1.9.0");
    let older_manifest_path = find_first_file(&older_locale_directory);
    let older_manifest: Value =
        serde_json::from_slice(&fs::read(older_manifest_path).expect("older merged manifest should be readable"))
            .expect("older merged manifest should be JSON");
    assert_eq!(
        older_manifest["Installers"][0]["InstallerSha256"].as_str(),
        Some("0000000000000000000000000000000000000000000000000000000000000000"),
        "all-zero InstallerSha256 must not be coerced to numeric zero"
    );

    let current_manifest_path = find_first_file(&feed.join("manifests/Pinget.Test.Matrix/1.10.0"));
    let current_manifest: Value =
        serde_json::from_slice(&fs::read(current_manifest_path).expect("merged manifest should be readable"))
            .expect("merged manifest should be JSON");
    assert_eq!(current_manifest["PackageLocale"].as_str(), Some("en-US"));
    assert_eq!(current_manifest["PackageName"].as_str(), Some("Matrix Package"));
    assert_eq!(current_manifest["Publisher"].as_str(), Some("Pinget Test Fixtures"));
    assert_eq!(current_manifest["PackageVersion"].as_str(), Some("1.10.0"));
    let french_localization = current_manifest["Localization"]
        .as_array()
        .expect("non-default locale should be embedded in Localization");
    assert_eq!(french_localization.len(), 1);
    assert_eq!(french_localization[0]["PackageLocale"].as_str(), Some("fr-FR"));
    assert_eq!(french_localization[0]["PackageName"].as_str(), Some("Matrice"));
    assert_eq!(
        french_localization[0]["Publisher"].as_str(),
        Some("Pinget Localization Lab")
    );
    assert!(french_localization[0].get("ManifestType").is_none());
    let installers = current_manifest["Installers"]
        .as_array()
        .expect("French merged manifest should retain installer entries");
    assert_eq!(installers.len(), 2);
    assert_eq!(
        current_manifest["Installers"][0]["Dependencies"]["PackageDependencies"][0]["PackageIdentifier"].as_str(),
        Some("Pinget.Test.Runtime")
    );
    assert_eq!(
        current_manifest["Installers"][0]["InstallerSwitches"]["Silent"].as_str(),
        Some("/quiet")
    );
    assert!(
        installers
            .iter()
            .any(|installer| installer["InstallerType"].as_str() == Some("portable"))
    );
}

#[test]
#[cfg(windows)]
fn build_uses_declared_default_locale_and_embeds_other_localizations_once() {
    let temp = TempDirectory::new();
    let input = temp.path.join("manifests");
    fs::create_dir_all(&input).expect("manifest root should be created");
    let id = "Pinget.Test.LocaleOrder";
    write_locale_order_manifests(&input, id);

    let report = build(&BuildOptions {
        input,
        state: temp.path.join("state"),
    })
    .expect("a default locale that sorts after another locale should build");

    let feed_manifests = report.snapshot_directory.join("feed/manifests").join(id).join("1.0.0");
    assert_eq!(
        count_files_with_extension(&feed_manifests, "yaml"),
        1,
        "one merged manifest should be emitted per version"
    );
    let merged: serde_yaml::Value = serde_yaml::from_slice(
        &fs::read(find_first_file(&feed_manifests)).expect("merged manifest should be readable"),
    )
    .expect("merged manifest should parse as YAML");
    assert_eq!(merged["PackageLocale"].as_str(), Some("en-US"));
    assert_eq!(merged["PackageName"].as_str(), Some("Locale Order English"));
    let localizations = merged["Localization"]
        .as_sequence()
        .expect("non-default locales should be embedded in Localization");
    assert_eq!(localizations.len(), 1);
    assert_eq!(localizations[0]["PackageLocale"].as_str(), Some("de-DE"));
    assert_eq!(localizations[0]["PackageName"].as_str(), Some("Locale Reihenfolge"));
    assert!(localizations[0].get("ManifestType").is_none());
    assert!(localizations[0].get("ManifestVersion").is_none());

    let database = Connection::open_with_flags(
        report.snapshot_directory.join("Public/index.db"),
        rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
    )
    .expect("index should open read-only");
    let indexed_versions: Vec<String> = database
        .prepare("SELECT latest_version FROM packages WHERE id=?1")
        .expect("package query should prepare")
        .query_map([id], |row| row.get(0))
        .expect("package query should run")
        .map(|row| row.expect("version row should be readable"))
        .collect();
    assert_eq!(indexed_versions, ["1.0.0"]);
}

#[test]
#[cfg(windows)]
fn build_preserves_schema_string_scalars_in_canonical_manifest_output() {
    let temp = TempDirectory::new();
    let input = temp.path.join("manifests");
    fs::create_dir_all(&input).expect("manifest root should be created");
    fs::write(
        input.join("scalar.yaml"),
        r#"PackageIdentifier: Pinget.Test.ScalarTypes
PackageVersion: 1.0
PackageLocale: en-US
Publisher: Pinget Test Fixtures
PackageName: Scalar Types
DisplayVersion: 1.0
SignatureSha256: 0000000000000000000000000000000000000000000000000000000000000000
GenericNumber: 1.0
Small: 0.5
Tiny: 1e-6
Threshold: 1e-5
Large: 1e16
NegativeZero: -0.0
MaxUInt: 18446744073709551615
Installers:
  - InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
    Architecture: x64
    InstallerType: exe
    InstallerUrl: https://example.invalid/scalar.exe
ManifestType: singleton
ManifestVersion: 1.9.0
"#,
    )
    .expect("scalar fixture should be written");

    let report = build(&BuildOptions {
        input,
        state: temp.path.join("state"),
    })
    .expect("valid schema scalar fixture should build");

    let merged_path = find_first_file(&report.snapshot_directory.join("feed/manifests"));
    let merged: serde_yaml::Value =
        serde_yaml::from_slice(&fs::read(merged_path).expect("merged output should be readable"))
            .expect("merged output should parse as YAML");
    assert_eq!(merged["PackageVersion"].as_str(), Some("1.0"));
    assert_eq!(merged["DisplayVersion"].as_str(), Some("1.0"));
    assert_eq!(
        merged["SignatureSha256"].as_str(),
        Some("0000000000000000000000000000000000000000000000000000000000000000")
    );
    assert_eq!(
        merged["Installers"][0]["InstallerSha256"].as_str(),
        Some("0000000000000000000000000000000000000000000000000000000000000000")
    );
    assert_eq!(merged["GenericNumber"].as_f64(), Some(1.0));
    assert_eq!(merged["Small"].as_f64(), Some(0.5));
    assert_eq!(merged["Tiny"].as_f64(), Some(1e-6));
    assert_eq!(merged["Threshold"].as_f64(), Some(1e-5));
    assert_eq!(merged["Large"].as_f64(), Some(1e16));
    assert_eq!(merged["NegativeZero"].as_f64(), Some(-0.0));
    assert_eq!(merged["MaxUInt"].as_u64(), Some(u64::MAX));
}

#[test]
#[cfg(windows)]
fn equivalent_input_trees_produce_the_same_build_identifier() {
    let temp = TempDirectory::new();
    let first_input = copy_fixture("multi", &temp.path.join("first"));
    let second_input = copy_fixture("multi", &temp.path.join("second"));
    let first = build(&BuildOptions {
        input: first_input,
        state: temp.path.join("state-a"),
    })
    .expect("first input should build");
    let second = build(&BuildOptions {
        input: second_input,
        state: temp.path.join("state-b"),
    })
    .expect("second input should build");

    assert_eq!(first.build_id, second.build_id);
}

#[test]
fn diff_reports_raw_content_additions_changes_and_removals() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let build_id = create_inventory_state(&input, &state);
    let original_database = fs::read(state.join("publisher.db")).expect("inventory state");
    let unchanged = diff(&BuildOptions {
        input: input.clone(),
        state: state.clone(),
    })
    .expect("unchanged inputs should diff");
    assert!(unchanged.added.is_empty());
    assert!(unchanged.changed.is_empty());
    assert!(unchanged.removed.is_empty());
    assert_eq!(unchanged.last_build_id.as_deref(), Some(build_id.as_str()));

    let changed_manifest = input.join("Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
    let mut changed_content = fs::read_to_string(&changed_manifest).expect("fixture should be readable");
    changed_content.push_str("# raw byte update\n");
    fs::write(changed_manifest, changed_content).expect("metadata file should change");
    fs::write(
        input.join("Pinget.Test.Added.yaml"),
        singleton_manifest("Pinget.Test.Added", "1.0.0"),
    )
    .expect("new manifest should be written");
    fs::remove_file(input.join("Pinget.Test.Matrix_1.9.0.yaml")).expect("old version fixture should be removed");

    let changes = diff(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("changed input should diff");
    assert_eq!(changes.added, ["Pinget.Test.Added.yaml"]);
    assert_eq!(changes.changed, ["Pinget.Test.Matrix_1.10.0.locale.en-US.yaml"]);
    assert_eq!(changes.removed, ["Pinget.Test.Matrix_1.9.0.yaml"]);
    assert!(!changes.last_build_published);
    assert_eq!(
        fs::read(state.join("publisher.db")).expect("unchanged inventory state"),
        original_database
    );
    assert!(
        !state.join("builds").exists(),
        "diff must not require or generate native artifacts"
    );
}

#[cfg(not(windows))]
#[test]
fn non_windows_build_rejects_native_compression_while_diff_succeeds() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let failed_state = temp.path.join("native-state");
    let error = build(&BuildOptions {
        input: input.clone(),
        state: failed_state.clone(),
    })
    .expect_err("native writer must not claim support on a non-Windows host");
    match error {
        PublisherError::Tool(message) => {
            assert!(message.contains("native WinGet MSZIP compression"));
            assert!(message.contains("only on Windows"));
        }
        other => panic!("expected native capability error, got {other}"),
    }
    assert!(
        !failed_state.join("publisher.db").exists(),
        "failed native build must not record a snapshot"
    );
    assert_eq!(
        fs::read_dir(failed_state.join("builds"))
            .expect("snapshot directory")
            .count(),
        0,
        "failed native build must clean private staging"
    );
    let state = temp.path.join("inventory-state");
    create_empty_state(&state);
    let original = fs::read(state.join("publisher.db")).expect("inventory state");
    let changes = diff(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("diff must not call native compression");
    assert_eq!(
        changes.added,
        [
            "Pinget.Test.Matrix_1.10.0.installer.yaml",
            "Pinget.Test.Matrix_1.10.0.locale.en-US.yaml",
            "Pinget.Test.Matrix_1.10.0.locale.fr-FR.yaml",
            "Pinget.Test.Matrix_1.10.0.version.yaml",
            "Pinget.Test.Matrix_1.9.0.yaml",
        ]
    );
    assert!(changes.changed.is_empty());
    assert!(changes.removed.is_empty());
    assert_eq!(changes.last_build_id, None);
    assert!(!changes.last_build_published);
    assert_eq!(
        fs::read(state.join("publisher.db")).expect("inventory state preserved"),
        original
    );
    assert!(!state.join("builds").exists());
}

#[test]
fn readonly_state_commands_do_not_initialize_missing_state() {
    let temp = TempDirectory::new();
    let missing_state = temp.path.join("missing");
    assert!(status(&missing_state).is_err());
    assert!(!missing_state.exists());

    let input = copy_fixture("multi", &temp.path.join("manifests"));
    assert!(
        diff(&BuildOptions {
            input,
            state: missing_state.clone(),
        })
        .is_err()
    );
    assert!(!missing_state.exists());
}

#[test]
fn read_only_operations_reject_reparse_points_and_state_overlap() {
    #[cfg(any(windows, unix))]
    {
        #[cfg(unix)]
        use std::os::unix::fs::symlink as symlink_dir;
        #[cfg(windows)]
        use std::os::windows::fs::symlink_dir;

        let temp = TempDirectory::new();
        let input = copy_fixture("multi", &temp.path.join("manifests"));
        let state = temp.path.join("state");
        create_empty_state(&state);
        let input_link = temp.path.join("manifests-link");
        let state_link = temp.path.join("state-link");
        symlink_dir(&input, &input_link).expect("input directory symlink should be available");
        symlink_dir(&state, &state_link).expect("state directory symlink should be available");
        let diff_options = BuildOptions {
            input: input_link,
            state,
        };
        for error in [
            diff(&diff_options).expect_err("diff must reject symlink input"),
            status(&state_link).expect_err("status must reject symlink state"),
            verify(&state_link, None, None).expect_err("verify must reject symlink state"),
        ] {
            assert!(
                error.to_string().contains("reparse points"),
                "unexpected symlink rejection: {error}"
            );
        }
        #[cfg(windows)]
        {
            fs::remove_dir(&diff_options.input).expect("input directory symlink should be removed");
            fs::remove_dir(state_link).expect("state directory symlink should be removed");
        }
        #[cfg(unix)]
        {
            fs::remove_file(&diff_options.input).expect("input symlink should be unlinked");
            fs::remove_file(state_link).expect("state symlink should be unlinked");
        }
    }

    let temp = TempDirectory::new();
    let state = temp.path.join("state");
    create_empty_state(&state);
    let original_database = fs::read(state.join("publisher.db")).expect("state database");
    let nested_input = state.join("nested-input");
    fs::create_dir(&nested_input).expect("overlapping input directory");
    let error = diff(&BuildOptions {
        input: nested_input,
        state: state.clone(),
    })
    .expect_err("diff must reject an input root nested inside its state root");
    assert!(error.to_string().to_ascii_lowercase().contains("overlap"));
    assert_eq!(
        fs::read(state.join("publisher.db")).expect("preserved state"),
        original_database
    );
    assert_eq!(status(&state).expect("preserved valid state").build_count, 0);
}

#[test]
fn build_rejects_future_sqlite_user_version_without_mutating_state() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    create_empty_state(&state);
    let database_path = state.join("publisher.db");
    {
        let database = Connection::open(&database_path).expect("state database should open");
        database
            .execute_batch("PRAGMA user_version=999;")
            .expect("future schema marker should be written");
    }
    let original_database = fs::read(&database_path).expect("future-version database should be readable");

    let error = build(&BuildOptions { input, state }).expect_err("a future user_version must be rejected");

    assert!(error.to_string().to_ascii_lowercase().contains("version"));
    assert_eq!(
        fs::read(database_path).expect("database should remain readable"),
        original_database,
        "rejecting an unknown state version must not rewrite the database"
    );
}

#[test]
fn build_rejects_legacy_artifact_format_state_without_mutating_it() {
    for format_version in [None, Some("1"), Some("999")] {
        let temp = TempDirectory::new();
        let input = copy_fixture("multi", &temp.path.join("manifests"));
        let state = temp.path.join("state");
        create_empty_state(&state);
        let database_path = state.join("publisher.db");
        {
            let database = Connection::open(&database_path).expect("state database should open");
            if let Some(version) = format_version {
                database
                    .execute(
                        "UPDATE metadata SET value=?1 WHERE name='artifactFormatVersion'",
                        [version],
                    )
                    .expect("unsupported format marker should be written");
            } else {
                database
                    .execute("DELETE FROM metadata WHERE name='artifactFormatVersion'", [])
                    .expect("legacy format marker should be removed");
            }
        }
        let original_database = fs::read(&database_path).expect("legacy-format database should be readable");

        let error = build(&BuildOptions { input, state }).expect_err("unsupported publisher state must be rejected");

        let diagnostic = error.to_string().to_ascii_lowercase();
        assert!(
            diagnostic.contains("artifact format version"),
            "version: {format_version:?}; {diagnostic}"
        );
        assert!(
            diagnostic.contains("--state"),
            "version: {format_version:?}; {diagnostic}"
        );
        assert_eq!(
            fs::read(database_path).expect("database should remain readable"),
            original_database,
            "refusing format {format_version:?} must not migrate or rewrite it"
        );
    }
}

#[test]
#[cfg(windows)]
fn verify_rejects_legacy_artifact_format_snapshot_without_mutating_it() {
    for format_version in [None, Some(1), Some(999)] {
        let temp = TempDirectory::new();
        let input = copy_fixture("multi", &temp.path.join("manifests"));
        let state = temp.path.join("state");
        let report = build(&BuildOptions {
            input,
            state: state.clone(),
        })
        .expect("current-format fixture should create a snapshot");
        let metadata_path = state.join("builds").join(&report.build_id).join("snapshot.json");
        let mut metadata: Value =
            serde_json::from_slice(&fs::read(&metadata_path).expect("snapshot should be readable"))
                .expect("snapshot metadata should be JSON");
        assert_eq!(metadata["artifactFormatVersion"].as_u64(), Some(2));
        if let Some(version) = format_version {
            metadata["artifactFormatVersion"] = Value::from(version);
        } else {
            metadata
                .as_object_mut()
                .expect("snapshot metadata should be an object")
                .remove("artifactFormatVersion");
        }
        fs::write(
            &metadata_path,
            serde_json::to_vec_pretty(&metadata).expect("legacy metadata should serialize"),
        )
        .expect("legacy snapshot metadata should be written");
        let legacy_snapshot = fs::read(&metadata_path).expect("legacy snapshot should be readable");

        let error =
            verify(&state, Some(&report.build_id), None).expect_err("unsupported publisher snapshot must be rejected");

        assert!(error.to_string().to_ascii_lowercase().contains("format"));
        assert_eq!(
            fs::read(&metadata_path).expect("snapshot should remain readable"),
            legacy_snapshot,
            "verification must not upgrade format {format_version:?} snapshot metadata"
        );
    }
}

#[test]
fn build_rejects_foreign_sqlite_state_without_mutating_it() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    fs::create_dir_all(&state).expect("state root should be created");
    let database_path = state.join("publisher.db");
    {
        let database = Connection::open(&database_path).expect("foreign database should be created");
        database
            .execute_batch(
                "CREATE TABLE unrelated_owner(value TEXT); INSERT INTO unrelated_owner VALUES('preserve-me');",
            )
            .expect("foreign schema should be written");
    }
    let original_database = fs::read(&database_path).expect("foreign database should be readable");

    let error = build(&BuildOptions { input, state }).expect_err("unrecognized SQLite state must not be adopted");

    assert!(!error.to_string().is_empty());
    assert_eq!(
        fs::read(database_path).expect("foreign database should remain readable"),
        original_database
    );
}

#[test]
#[cfg(windows)]
fn build_preserves_stale_staging_owned_by_another_attempt_and_completes_snapshot() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let stale_staging = state.join("builds/.staging-stale-owned-attempt");
    fs::create_dir_all(&stale_staging).expect("stale staging directory should be created");
    let marker = stale_staging.join("preserve.txt");
    fs::write(&marker, "not owned by this invocation").expect("stale marker should be written");

    let report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("unrelated stale staging should not block a build");

    assert!(verify(&state, Some(&report.build_id), None).is_ok());
    assert_eq!(
        fs::read_to_string(marker).expect("stale marker should remain"),
        "not owned by this invocation"
    );
    assert!(stale_staging.is_dir());
}

#[test]
fn manifest_validation_rejects_missing_version_unsupported_schema_and_traversal() {
    for (case_id, mutation, expected) in [
        ("missing-version", "missing-version", "version manifest"),
        (
            "unsupported-schema",
            "unsupported-schema",
            "unsupported ManifestVersion",
        ),
        ("unsafe-identifier", "unsafe-identifier", "unsafe"),
        ("invalid-version", "invalid-version", "PackageVersion is invalid"),
        ("duplicate-version", "duplicate-version", "duplicate"),
        ("duplicate-locale", "duplicate-locale", "duplicate"),
        ("duplicate-singleton", "duplicate-singleton", "duplicate"),
        ("missing-installer", "missing-installer", "installer"),
        ("empty-default-locale", "empty-default-locale", "DefaultLocale"),
        ("duplicate-default-locale", "duplicate-default-locale", "duplicate"),
        ("missing-singleton-locale", "missing-singleton-locale", "PackageLocale"),
    ] {
        let temp = TempDirectory::new();
        let input = copy_fixture("multi", &temp.path.join("manifests"));
        apply_mutation(&input, mutation);
        let result = build(&BuildOptions {
            input,
            state: temp.path.join("state"),
        });
        let error = result.expect_err(case_id);
        assert!(
            error
                .to_string()
                .to_ascii_lowercase()
                .contains(&expected.to_ascii_lowercase()),
            "{case_id}: unexpected diagnostic: {error}"
        );
    }
}

#[test]
fn manifest_validation_rejects_case_conflicting_package_ids() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    fs::write(
        input.join("case-conflict.yaml"),
        singleton_manifest("pinget.test.matrix", "2.0.0"),
    )
    .expect("case-conflicting manifest should be written");
    let error = build(&BuildOptions {
        input,
        state: temp.path.join("state"),
    })
    .expect_err("package identifiers that differ only by case must be rejected");

    assert!(
        error.to_string().to_ascii_lowercase().contains("cas"),
        "unexpected case conflict diagnostic: {error}"
    );
}

#[test]
fn overlapping_input_and_state_roots_are_rejected_before_state_creation() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = input.join("state");
    let error = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect_err("state must not overlap manifest inputs");

    assert!(error.to_string().to_ascii_lowercase().contains("overlap"));
    assert!(!state.exists());
}

#[cfg(windows)]
#[test]
fn manifest_scan_rejects_reparse_point_without_following_it() {
    use std::os::windows::fs::symlink_dir;

    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let external = temp.path.join("external");
    fs::create_dir_all(&external).expect("external directory should be created");
    let link = input.join("linked");
    symlink_dir(&external, &link).expect("test environment must permit directory symlinks");
    let state = temp.path.join("state");

    let error = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect_err("manifest roots must not follow reparse points");

    assert!(error.to_string().to_ascii_lowercase().contains("reparse"));
    assert!(!state.join("publisher.db").exists());
}

#[cfg(windows)]
#[test]
fn existing_writer_lock_prevents_build_mutation() {
    use std::os::windows::fs::OpenOptionsExt;

    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    fs::create_dir_all(&state).expect("state root should be created");
    let held_lock = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .share_mode(0)
        .open(state.join(".publisher.lock"))
        .expect("exclusive writer lock should be held");

    let error = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect_err("a second writer must not mutate locked state");

    assert!(matches!(
        error,
        pinget_publisher::PublisherError::Io(_) | pinget_publisher::PublisherError::Invalid(_)
    ));
    assert!(state.join(".publisher.lock").is_file());
    assert!(!state.join("publisher.db").exists());
    drop(held_lock);
}

#[test]
#[cfg(windows)]
fn publish_unsigned_is_idempotent_and_excludes_staging_files_from_inventory() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let build_report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("fixture should build before publication");

    let first = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.0",
        &build_report.build_id,
    ))
    .expect("unsigned-development publication should succeed");
    assert!(first.valid);
    assert_eq!(first.build_id, build_report.build_id);
    assert!(!first.reused);

    let marker: Value = serde_json::from_slice(
        &fs::read(destination.join(".pinget-publisher.json")).expect("ownership marker should exist"),
    )
    .expect("ownership marker should be JSON");
    assert_eq!(marker["schemaVersion"].as_u64(), Some(1));
    assert!(marker.get("artifactFormatVersion").is_none());
    let artifacts = marker["artifacts"]
        .as_object()
        .expect("ownership marker should list published artifacts");
    assert!(artifacts.contains_key("source2.msix"));
    assert!(
        artifacts
            .keys()
            .all(|path| !path.contains(".source2.") && !path.contains(".publish-")),
        "temporary staged package paths must not be recorded: {artifacts:?}"
    );

    let retry = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.0",
        &build_report.build_id,
    ))
    .expect("same version/build retry should be idempotent");
    assert!(retry.valid);
    assert!(retry.reused);
    assert_eq!(retry.artifact_count, artifacts.len());
    let verified = verify_published_destination(&destination, None, &[])
        .expect("published destination should verify independently");
    assert!(verified.valid);
    assert_eq!(verified.build_id.as_deref(), Some(build_report.build_id.as_str()));
    assert_eq!(verified.artifact_count, artifacts.len());
}

#[test]
#[cfg(windows)]
fn publish_rejects_downgrade_and_retains_immutable_artifacts_on_update() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let first_build = build(&BuildOptions {
        input: input.clone(),
        state: state.clone(),
    })
    .expect("initial fixture should build");
    let first = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.0",
        &first_build.build_id,
    ))
    .expect("initial unsigned publication should succeed");
    let first_index = fs::read(destination.join("source2.msix")).expect("first index should exist");
    let snapshot: Value = serde_json::from_slice(
        &fs::read(first_build.snapshot_directory.join("snapshot.json")).expect("snapshot metadata should exist"),
    )
    .expect("snapshot metadata should be JSON");
    let old_artifacts = snapshot["artifacts"]
        .as_object()
        .expect("snapshot should list immutable feed artifacts")
        .keys()
        .cloned()
        .collect::<Vec<_>>();

    let changed = input.join("Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
    let mut contents = fs::read_to_string(&changed).expect("manifest should be readable");
    contents.push_str("# publication content update\n");
    fs::write(&changed, contents).expect("manifest content should change");
    let second_build = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("updated input should build");
    assert_ne!(first_build.build_id, second_build.build_id);

    let downgrade = publish(&unsigned_publish_options(
        &state,
        &destination,
        "0.9.9.9",
        &second_build.build_id,
    ))
    .expect_err("package version must not move backwards");
    assert!(
        downgrade.to_string().contains("must be greater"),
        "unexpected downgrade diagnostic: {downgrade}"
    );
    assert_eq!(
        first_index,
        fs::read(destination.join("source2.msix")).expect("old MSIX should remain after rejection")
    );

    let updated = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.1",
        &second_build.build_id,
    ))
    .expect("monotonic package version should publish");
    assert!(updated.valid);
    assert_eq!(updated.build_id, second_build.build_id);
    assert_ne!(
        first_index,
        fs::read(destination.join("source2.msix")).expect("updated MSIX should exist")
    );
    for relative in old_artifacts {
        assert!(
            destination.join(&relative).is_file(),
            "previous immutable artifact was not retained: {relative}"
        );
    }
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("updated destination should verify")
            .valid
    );
    assert!(first.valid);
}

#[test]
#[cfg(windows)]
fn publish_rejects_unowned_destination_without_overwriting_unrelated_file() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let build_report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("fixture should build before publication");
    fs::create_dir_all(&destination).expect("unowned destination should be created");
    let unrelated = destination.join("keep-me.txt");
    let original = b"unrelated destination content";
    fs::write(&unrelated, original).expect("unrelated file should be written");

    let error = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.0",
        &build_report.build_id,
    ))
    .expect_err("publisher must not adopt a non-empty unowned directory");

    assert!(
        error.to_string().to_ascii_lowercase().contains("unowned"),
        "unexpected unowned-destination diagnostic: {error}"
    );
    assert_eq!(fs::read(&unrelated).expect("unrelated file should remain"), original);
    assert!(!destination.join(".pinget-publisher.json").exists());
    assert!(!destination.join("source2.msix").exists());
    assert!(
        status(&state)
            .expect("state should remain readable")
            .last_published_build_id
            .is_none()
    );
}

#[test]
#[cfg(windows)]
fn publish_rejects_identity_change_and_untracked_artifact_without_mutation() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let first_build = build(&BuildOptions {
        input: input.clone(),
        state: state.clone(),
    })
    .expect("fixture should build before publication");
    let options = unsigned_publish_options(&state, &destination, "1.0.0.0", &first_build.build_id);
    publish(&options).expect("initial unsigned publication should succeed");
    let original_index = fs::read(destination.join("source2.msix")).expect("index should exist");
    let original_marker = fs::read(destination.join(".pinget-publisher.json")).expect("marker should exist");

    let mut changed_identity = unsigned_publish_options(&state, &destination, "1.0.0.0", &first_build.build_id);
    changed_identity.name = "Pinget.Publisher.Other".to_owned();
    let identity_error = publish(&changed_identity).expect_err("owned package identity must remain stable");
    assert!(
        identity_error.to_string().contains("different package identity"),
        "unexpected identity diagnostic: {identity_error}"
    );
    assert_eq!(
        fs::read(destination.join("source2.msix")).expect("MSIX should remain after identity rejection"),
        original_index
    );
    assert_eq!(
        fs::read(destination.join(".pinget-publisher.json"))
            .expect("ownership marker should remain after identity rejection"),
        original_marker
    );

    let unrelated = destination.join("keep-me.txt");
    let unrelated_bytes = b"untracked owned-destination content";
    fs::write(&unrelated, unrelated_bytes).expect("untracked file should be written");
    let verify_error = verify_published_destination(&destination, None, &[])
        .expect_err("destination verification must reject untracked artifacts");
    assert!(
        verify_error.to_string().to_ascii_lowercase().contains("untracked"),
        "unexpected verification diagnostic: {verify_error}"
    );

    let changed_manifest = input.join("Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
    let mut contents = fs::read_to_string(&changed_manifest).expect("manifest should be readable");
    contents.push_str("# attempted update with untracked artifact\n");
    fs::write(&changed_manifest, contents).expect("manifest content should change");
    let second_build = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("updated input should build");
    let publish_error = publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.1",
        &second_build.build_id,
    ))
    .expect_err("publish must reject untracked destination files");
    assert!(
        publish_error.to_string().to_ascii_lowercase().contains("untracked"),
        "unexpected publish diagnostic: {publish_error}"
    );
    assert_eq!(
        fs::read(&unrelated).expect("untracked file should remain"),
        unrelated_bytes
    );
    assert_eq!(
        fs::read(destination.join("source2.msix")).expect("MSIX should remain after rejection"),
        original_index
    );
    assert_eq!(
        fs::read(destination.join(".pinget-publisher.json")).expect("ownership marker should remain after rejection"),
        original_marker
    );
}

#[test]
#[cfg(windows)]
fn publish_failed_packaging_tool_does_not_publish_or_record_build() {
    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let build_report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("fixture should build before packaging");
    let where_tool = PathBuf::from(std::env::var_os("WINDIR").expect("Windows system root"))
        .join("System32")
        .join("where.exe");
    assert!(where_tool.is_file(), "where.exe should be available on Windows");
    let pfx = temp.path.join("placeholder.pfx");
    fs::write(&pfx, b"not a certificate").expect("placeholder PFX");
    let mut options = unsigned_publish_options(&state, &destination, "1.0.0.0", &build_report.build_id);
    options.signing = Signing::Pfx {
        path: pfx,
        // Any set variable works: the controlled failing signing tool never reads it.
        password_env: "WINDIR".to_owned(),
    };
    options.psign_tool = Some(where_tool);

    let error = publish(&options).expect_err("the controlled failing signing tool must fail");

    assert!(
        error.to_string().contains("failed"),
        "unexpected signing diagnostic: {error}"
    );
    assert!(!destination.join("source2.msix").exists());
    assert!(!destination.join(".pinget-publisher.json").exists());
    assert!(
        status(&state)
            .expect("state should remain readable")
            .last_published_build_id
            .is_none()
    );

    options.signing = Signing::UnsignedDevelopment;
    options.psign_tool = None;
    let retry = publish(&options).expect("retry without the failing tool should succeed");
    assert!(retry.valid);
    assert_eq!(retry.build_id, build_report.build_id);
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("retried destination should verify")
            .valid
    );
    assert_eq!(
        status(&state)
            .expect("state should record the successful retry")
            .last_published_build_id,
        Some(build_report.build_id)
    );
}

#[cfg(windows)]
#[test]
fn publish_semantic_metadata_promotion_failure_preserves_old_feed_then_retry_succeeds() {
    use std::os::windows::fs::OpenOptionsExt;

    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let state = temp.path.join("state");
    let destination = temp.path.join("published");
    let first_build = build(&BuildOptions {
        input: input.clone(),
        state: state.clone(),
    })
    .expect("initial fixture should build");
    publish(&unsigned_publish_options(
        &state,
        &destination,
        "1.0.0.0",
        &first_build.build_id,
    ))
    .expect("initial publication should succeed");
    let original_index = fs::read(destination.join("source2.msix")).expect("index should exist");
    let original_marker = fs::read(destination.join(".pinget-publisher.json")).expect("marker should exist");

    let changed_manifest = input.join("Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
    let contents = fs::read_to_string(&changed_manifest).expect("manifest should be readable");
    let updated_contents = contents.replace("PackageName: Matrix Package", "PackageName: Matrix Package Updated");
    assert_ne!(
        contents, updated_contents,
        "the input mutation must change real metadata"
    );
    fs::write(&changed_manifest, updated_contents).expect("manifest metadata should change");
    let second_build = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("updated fixture should build");
    let next = unsigned_publish_options(&state, &destination, "1.0.0.1", &second_build.build_id);

    let locked_index = fs::OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(destination.join("source2.msix"))
        .expect("read-only handle should prevent atomic replacement");
    let error = publish(&next).expect_err("root index promotion must fail while index is open");
    drop(locked_index);

    assert!(error.to_string().to_ascii_lowercase().contains("access"));
    assert_eq!(
        fs::read(destination.join("source2.msix")).expect("MSIX should remain after rollback"),
        original_index
    );
    assert_eq!(
        fs::read(destination.join(".pinget-publisher.json")).expect("ownership marker should remain after rollback"),
        original_marker
    );
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("rolled-back destination should remain verifiable")
            .valid
    );
    assert_eq!(
        status(&state)
            .expect("state should remain readable after rollback")
            .last_published_build_id
            .as_deref(),
        Some(first_build.build_id.as_str()),
        "failed promotion must not record the new build as published"
    );

    let retry = publish(&next).expect("retry after releasing the blocking handle should succeed");
    assert!(retry.valid);
    assert_eq!(retry.build_id, second_build.build_id);
    assert_ne!(
        original_index,
        fs::read(destination.join("source2.msix")).expect("retry should promote the new index")
    );
    assert_ne!(
        original_marker,
        fs::read(destination.join(".pinget-publisher.json")).expect("retry should update ownership metadata")
    );
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("retried destination should verify")
            .valid
    );
    assert_eq!(
        status(&state)
            .expect("state should reflect successful retry")
            .last_published_build_id,
        Some(second_build.build_id)
    );
}

#[test]
#[cfg(windows)]
fn publish_destination_lock_serializes_different_state_roots_and_allows_retry() {
    use std::os::windows::fs::OpenOptionsExt;

    let temp = TempDirectory::new();
    let input = copy_fixture("multi", &temp.path.join("manifests"));
    let first_state = temp.path.join("first-state");
    let second_state = temp.path.join("second-state");
    let destination = temp.path.join("published");
    let first_build = build(&BuildOptions {
        input: input.clone(),
        state: first_state.clone(),
    })
    .expect("first state should build");
    let second_build = build(&BuildOptions {
        input,
        state: second_state.clone(),
    })
    .expect("second state should build");
    assert_eq!(first_build.build_id, second_build.build_id);
    publish(&unsigned_publish_options(
        &first_state,
        &destination,
        "1.0.0.0",
        &first_build.build_id,
    ))
    .expect("first state should publish");
    let old_index = fs::read(destination.join("source2.msix")).expect("old index should exist");
    let old_marker = fs::read(destination.join(".pinget-publisher.json")).expect("old marker should exist");
    let destination_name = destination
        .file_name()
        .expect("destination should have a final path component")
        .to_string_lossy();
    let lock_path = destination.with_file_name(format!("{destination_name}.pinget-publisher.lock"));

    let held_lock = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .share_mode(0)
        .open(lock_path)
        .expect("destination sibling lock should be acquired");
    let error = publish(&unsigned_publish_options(
        &second_state,
        &destination,
        "1.0.0.1",
        &second_build.build_id,
    ))
    .expect_err("a second state root must respect the destination lock");
    assert!(error.to_string().to_ascii_lowercase().contains("lock"));
    assert_eq!(
        fs::read(destination.join("source2.msix")).expect("old index should remain"),
        old_index
    );
    assert_eq!(
        fs::read(destination.join(".pinget-publisher.json")).expect("old marker should remain"),
        old_marker
    );
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("destination should remain valid during lock rejection")
            .valid
    );
    drop(held_lock);

    let retry = publish(&unsigned_publish_options(
        &second_state,
        &destination,
        "1.0.0.1",
        &second_build.build_id,
    ))
    .expect("publication should succeed after the destination lock is released");
    assert!(retry.valid);
    assert_eq!(retry.build_id, second_build.build_id);
    assert!(
        verify_published_destination(&destination, None, &[])
            .expect("retried destination should verify")
            .valid
    );
    assert_eq!(
        status(&second_state)
            .expect("second state should record publication")
            .last_published_build_id,
        Some(second_build.build_id)
    );
}

#[test]
#[cfg(windows)]
fn verify_rejects_corrupted_snapshot_sidecar_and_index() {
    for (component, location) in [
        ("snapshot", ""),
        ("sidecar", "feed/packages"),
        ("manifest", "feed/manifests"),
        ("index", "Public"),
    ] {
        let temp = TempDirectory::new();
        let input = copy_fixture("multi", &temp.path.join("manifests"));
        let state = temp.path.join("state");
        let report = build(&BuildOptions {
            input,
            state: state.clone(),
        })
        .expect("fixture should build before corruption");
        let snapshot = state.join("builds").join(&report.build_id);
        let file = match component {
            "snapshot" => snapshot.join("snapshot.json"),
            "index" => snapshot.join("Public").join("index.db"),
            _ => find_first_file(&snapshot.join(location)),
        };
        let corrupted = if component == "snapshot" {
            b"{not-json".as_slice()
        } else {
            b"tampered bytes".as_slice()
        };
        fs::write(&file, corrupted).expect("snapshot file should be mutated");

        assert!(
            verify(&state, Some(&report.build_id), None).is_err(),
            "corrupted {component} should fail verification"
        );
    }
}

#[test]
fn status_rejects_corrupted_state_database_without_reinitializing_it() {
    let temp = TempDirectory::new();
    let state = temp.path.join("state");
    create_empty_state(&state);
    let database = state.join("publisher.db");
    let corrupted = b"not a sqlite database";
    fs::write(&database, corrupted).expect("state database should be corrupted");

    assert!(status(&state).is_err());
    assert_eq!(
        fs::read(&database).expect("corrupted database should remain in place"),
        corrupted
    );
}

fn create_empty_state(state: &Path) {
    fs::create_dir_all(state).expect("inventory state directory");
    let database = Connection::open(state.join("publisher.db")).expect("inventory state database");
    // Read-only inventory operations require state records, not native index/feed artifacts.
    database
        .execute_batch(
            "PRAGMA user_version=1;
         CREATE TABLE metadata(name TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL) WITHOUT ROWID;
         CREATE TABLE builds(id TEXT PRIMARY KEY NOT NULL,inventory TEXT NOT NULL,summary TEXT NOT NULL) WITHOUT ROWID;
         INSERT INTO metadata VALUES('schemaVersion','1'),('artifactFormatVersion','2');",
        )
        .expect("independent state fixture schema");
}

fn create_inventory_state(input: &Path, state: &Path) -> String {
    create_empty_state(state);
    let initial = diff(&BuildOptions {
        input: input.to_path_buf(),
        state: state.to_path_buf(),
    })
    .expect("scan manifest inventory without compression");
    let inventory: BTreeMap<_, _> = initial
        .added
        .into_iter()
        .map(|relative| {
            let hash = sha256_file(&input.join(&relative));
            (relative, hash)
        })
        .collect();
    let inventory = serde_json::to_string(&inventory).expect("inventory JSON");
    let build_id = sha256_hex(format!("pinget-publisher-state-v2\n{inventory}").as_bytes());
    let database = Connection::open(state.join("publisher.db")).expect("inventory database");
    database
        .execute(
            "INSERT INTO builds(id,inventory,summary) VALUES(?1,?2,?3)",
            rusqlite::params![
                build_id,
                inventory,
                r#"{"createdUtc":"2026-01-01T00:00:00Z","manifestCount":5,"packageCount":1}"#
            ],
        )
        .expect("independent inventory record");
    database
        .execute("INSERT INTO metadata VALUES('lastBuildId',?1)", [&build_id])
        .expect("latest inventory record");
    build_id
}

fn apply_mutation(input: &Path, mutation: &str) {
    let version = input.join("Pinget.Test.Matrix_1.10.0.version.yaml");
    match mutation {
        "missing-version" => fs::remove_file(version).expect("version manifest should be removed"),
        "unsupported-schema" => {
            let content = fs::read_to_string(&version).expect("version manifest should be readable");
            fs::write(
                &version,
                content.replace("ManifestVersion: 1.9.0", "ManifestVersion: 9.9.9"),
            )
            .expect("unsupported schema should be written");
        }
        "unsafe-identifier" => {
            let content = fs::read_to_string(&version).expect("version manifest should be readable");
            fs::write(&version, content.replace("Pinget.Test.Matrix", "../escape"))
                .expect("traversal identifier should be written");
        }
        "invalid-version" => {
            let content = fs::read_to_string(&version).expect("version manifest should be readable");
            fs::write(
                &version,
                content.replace("PackageVersion: 1.10.0", "PackageVersion: ../escape"),
            )
            .expect("invalid package version should be written");
        }
        "duplicate-version" => {
            fs::copy(&version, input.join("duplicate-version.yaml"))
                .expect("duplicate version manifest should be copied");
        }
        "duplicate-locale" => {
            fs::copy(
                input.join("Pinget.Test.Matrix_1.10.0.locale.fr-FR.yaml"),
                input.join("duplicate-fr.yaml"),
            )
            .expect("duplicate locale manifest should be copied");
        }
        "duplicate-singleton" => {
            fs::copy(
                input.join("Pinget.Test.Matrix_1.9.0.yaml"),
                input.join("duplicate-singleton.yaml"),
            )
            .expect("duplicate singleton manifest should be copied");
        }
        "missing-installer" => {
            fs::remove_file(input.join("Pinget.Test.Matrix_1.10.0.installer.yaml"))
                .expect("installer manifest should be removed");
        }
        "empty-default-locale" => {
            let content = fs::read_to_string(&version).expect("version manifest should be readable");
            fs::write(&version, content.replace("DefaultLocale: en-US", "DefaultLocale: ''"))
                .expect("empty default locale should be written");
        }
        "duplicate-default-locale" => {
            let default_locale = input.join("Pinget.Test.Matrix_1.10.0.locale.en-US.yaml");
            let mut content = fs::read_to_string(default_locale).expect("default locale should be readable");
            content = content.replace("PackageLocale: en-US", "PackageLocale: en-us");
            fs::write(input.join("duplicate-default-locale.yaml"), content)
                .expect("duplicate default locale should be written");
        }
        "missing-singleton-locale" => {
            let content =
                singleton_manifest("Pinget.Test.MissingLocale", "1.0.0").replace("PackageLocale: en-US\n", "");
            fs::write(input.join("missing-locale.yaml"), content)
                .expect("singleton without package locale should be written");
        }
        _ => unreachable!("only declared test mutations are used"),
    }
}

#[cfg(windows)]
fn unsigned_publish_options(state: &Path, destination: &Path, version: &str, build_id: &str) -> PublishOptions {
    PublishOptions {
        state: state.to_path_buf(),
        build_id: Some(build_id.to_owned()),
        destination: destination.to_path_buf(),
        name: "Pinget.Publisher.Conformance".to_owned(),
        publisher: "CN=Pinget Publisher Conformance Test".to_owned(),
        version: version.to_owned(),
        signing: Signing::UnsignedDevelopment,
        psign_tool: None,
        trusted_ca: Vec::new(),
        timestamp_url: None,
    }
}

fn singleton_manifest(id: &str, version: &str) -> String {
    format!(
        "PackageIdentifier: {id}\n\
         PackageVersion: {version}\n\
         PackageLocale: en-US\n\
         Publisher: Pinget Test Fixtures\n\
         PackageName: {id}\n\
         License: MIT\n\
         ShortDescription: temporary fixture\n\
         Installers:\n\
           - Architecture: x64\n\
             InstallerType: exe\n\
             InstallerUrl: https://example.invalid/fixture.exe\n\
             InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000\n\
         ManifestType: singleton\n\
         ManifestVersion: 1.9.0\n"
    )
}

#[cfg(windows)]
fn write_locale_order_manifests(input: &Path, id: &str) {
    for (suffix, locale, publisher, name, kind) in [
        (
            "locale.en-US",
            "en-US",
            "Pinget Test Fixtures",
            "Locale Order English",
            "defaultLocale",
        ),
        (
            "locale.de-DE",
            "de-DE",
            "Pinget Test Fixtures",
            "Locale Reihenfolge",
            "locale",
        ),
    ] {
        fs::write(
            input.join(format!("{id}_1.0.0.{suffix}.yaml")),
            format!(
                "PackageIdentifier: {id}\n\
                 PackageVersion: 1.0.0\n\
                 PackageLocale: {locale}\n\
                 Publisher: {publisher}\n\
                 PackageName: {name}\n\
                 License: MIT\n\
                 ShortDescription: locale-order fixture\n\
                 ManifestType: {kind}\n\
                 ManifestVersion: 1.9.0\n"
            ),
        )
        .expect("locale manifest should be written");
    }
    fs::write(
        input.join(format!("{id}_1.0.0.version.yaml")),
        format!(
            "PackageIdentifier: {id}\n\
             PackageVersion: 1.0.0\n\
             DefaultLocale: en-US\n\
             ManifestType: version\n\
             ManifestVersion: 1.9.0\n"
        ),
    )
    .expect("version manifest should be written");
    fs::write(
        input.join(format!("{id}_1.0.0.installer.yaml")),
        format!(
            "PackageIdentifier: {id}\n\
             PackageVersion: 1.0.0\n\
             Installers:\n\
               - Architecture: x64\n\
                 InstallerType: exe\n\
                 InstallerUrl: https://example.invalid/locale-order.exe\n\
                 InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111\n\
             ManifestType: installer\n\
             ManifestVersion: 1.9.0\n"
        ),
    )
    .expect("installer manifest should be written");
}

fn copy_fixture(name: &str, destination: &Path) -> PathBuf {
    let source = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../tests/publisher/fixtures")
        .join(name);
    copy_directory(&source, destination);
    destination.to_path_buf()
}

fn copy_directory(source: &Path, destination: &Path) {
    fs::create_dir_all(destination).expect("fixture destination should be created");
    for entry in fs::read_dir(source).expect("fixture directory should be readable") {
        let entry = entry.expect("fixture entry should be readable");
        let path = entry.path();
        let target = destination.join(entry.file_name());
        if path.is_dir() {
            copy_directory(&path, &target);
        } else {
            fs::copy(path, target).expect("fixture file should be copied");
        }
    }
}

#[cfg(windows)]
fn find_first_file(directory: &Path) -> PathBuf {
    let mut pending = vec![directory.to_path_buf()];
    while let Some(current) = pending.pop() {
        for entry in fs::read_dir(current).expect("snapshot directory should be readable") {
            let path = entry.expect("snapshot entry should be readable").path();
            if path.is_dir() {
                pending.push(path);
            } else {
                return path;
            }
        }
    }
    panic!("expected at least one snapshot file");
}

#[cfg(windows)]
fn count_files_with_extension(directory: &Path, extension: &str) -> usize {
    let mut pending = vec![directory.to_path_buf()];
    let mut count = 0;
    while let Some(current) = pending.pop() {
        for entry in fs::read_dir(current).expect("snapshot directory should be readable") {
            let path = entry.expect("snapshot entry should be readable").path();
            if path.is_dir() {
                pending.push(path);
            } else if path.extension().and_then(|value| value.to_str()) == Some(extension) {
                count += 1;
            }
        }
    }
    count
}

fn sha256_file(path: &Path) -> String {
    use sha2::{Digest, Sha256};
    encode_hex(&Sha256::digest(fs::read(path).expect("sidecar should be readable")))
}

fn sha256_hex(bytes: &[u8]) -> String {
    use sha2::{Digest, Sha256};
    encode_hex(&Sha256::digest(bytes))
}

#[cfg(windows)]
fn read_correlations(database: &Connection, table: &str, column: &str) -> Vec<String> {
    let query = format!(
        "SELECT {column} FROM {table} AS valuesTable \
         JOIN packages AS package ON package.rowid=valuesTable.package \
         WHERE package.id=?1 ORDER BY {column}"
    );
    let mut statement = database.prepare(&query).expect("correlation query prepares");
    statement
        .query_map(["Pinget.Test.Matrix"], |row| row.get(0))
        .expect("correlation query executes")
        .collect::<Result<Vec<_>, _>>()
        .expect("correlation rows should decode")
}

#[cfg(windows)]
fn read_mapped_values(
    database: &Connection,
    value_table: &str,
    value_column: &str,
    map_table: &str,
    map_column: &str,
) -> Vec<String> {
    let query = format!(
        "SELECT value.{value_column} FROM {value_table} AS value \
         JOIN {map_table} AS value_map ON value_map.{map_column}=value.rowid \
         JOIN packages AS package ON package.rowid=value_map.package \
         WHERE package.id=?1 ORDER BY value.{value_column}"
    );
    let mut statement = database.prepare(&query).expect("mapping query prepares");
    statement
        .query_map(["Pinget.Test.Matrix"], |row| row.get(0))
        .expect("mapping query executes")
        .collect::<Result<Vec<_>, _>>()
        .expect("mapping values should decode")
}

fn encode_hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(char::from(DIGITS[usize::from(byte >> 4)]));
        output.push(char::from(DIGITS[usize::from(byte & 0x0f)]));
    }
    output
}

struct TempDirectory {
    path: PathBuf,
}

impl TempDirectory {
    fn new() -> Self {
        let sequence = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir()
            .join("pinget-publisher-rust-tests")
            .join(format!("{}-{sequence}", std::process::id()));
        fs::create_dir_all(&path).expect("test scratch directory should be created");
        Self { path }
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}
