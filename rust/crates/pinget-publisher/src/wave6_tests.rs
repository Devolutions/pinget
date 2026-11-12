//! Wave 6 publisher regression tests (source index builder parity with the .NET publisher).

use super::*;

const HASH: &str = "1111111111111111111111111111111111111111111111111111111111111111";

fn wave6_directory(label: &str) -> PathBuf {
    let name = format!(
        ".publisher-wave6-{label}-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
    );
    let directory = std::env::current_dir().expect("cwd").join(name);
    fs::create_dir(&directory).expect("create unit directory");
    directory
}

fn singleton(id: &str, extra: &str) -> String {
    format!(
        "PackageIdentifier: {id}\nPackageVersion: 1.0.0\nPackageLocale: en-US\nPackageName: Wave Six\n{extra}\
         License: MIT\nShortDescription: Wave 6\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n    \
         InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {HASH}\nManifestType: singleton\n\
         ManifestVersion: 1.9.0\n"
    )
}

/// Scans `files` from a temporary input tree and merges each (id, version) group.
fn merge_tree(label: &str, files: &[(&str, &[u8])]) -> Result<Vec<PackageManifest>> {
    let root = wave6_directory(label);
    for (name, bytes) in files {
        fs::write(root.join(name), bytes).expect("write manifest");
    }
    let result = scan_manifests(&root).and_then(|(_, groups)| {
        groups
            .into_iter()
            .map(|((id, _), documents)| merge_documents(&id, documents))
            .collect::<Result<Vec<_>>>()
    });
    fs::remove_dir_all(root).expect("cleanup");
    result
}

fn canonical_manifest(label: &str, yaml: &[u8]) -> String {
    let packages = merge_tree(label, &[("manifest.yaml", yaml)]).expect("manifest should merge");
    let bytes = serialize_merged(&packages[0].locale_docs[0].document).expect("canonical bytes");
    String::from_utf8(bytes).expect("canonical manifest is UTF-8")
}

#[test]
fn p1_correlation_values_are_case_folded_for_core_lookups() {
    let extra = "Publisher: Pinget\nPackageFamilyName: Pinget.Wave6_ABCdef123\nAppsAndFeaturesEntries:\n  - ProductCode: '{AABB-CC}'\n    \
                 UpgradeCode: '{DDEE-FF}'\n";
    let yaml = singleton("Pinget.Wave6", extra).replace(
        "    InstallerSha256",
        "    ProductCode: '{0011-AABB}'\n    UpgradeCode: '{2233-CCDD}'\n    PackageFamilyName: Other.App_XYZ\n    InstallerSha256",
    );
    let packages = merge_tree("p1", &[("manifest.yaml", yaml.as_bytes())]).expect("merge");
    let fields = catalog_fields_all(&packages).expect("catalog fields");
    // The root PackageFamilyName is not inherited by an exe installer (wave 12, R12-5).
    assert_eq!(fields.2, vec!["other.app_xyz"]);
    assert_eq!(fields.3, vec!["{0011-aabb}", "{aabb-cc}"]);
    assert_eq!(fields.4, vec!["{2233-ccdd}", "{ddee-ff}"]);
}

#[cfg(windows)]
#[test]
fn p1_built_index_matches_core_lowercase_exact_lookup() {
    let root = wave6_directory("p1-index");
    let input = root.join("input");
    fs::create_dir(&input).expect("input");
    let yaml = singleton("Pinget.Wave6", "Publisher: Pinget\n").replace(
        "    InstallerSha256",
        "    ProductCode: '{0011-AABB}'\n    InstallerSha256",
    );
    fs::write(input.join("manifest.yaml"), yaml).expect("manifest");
    let report = build(&BuildOptions {
        input,
        state: root.join("state"),
    })
    .expect("build");
    let index = Connection::open(report.snapshot_directory.join("Public").join("index.db")).expect("index");
    // Same query shape as pinget-core lookup_identity_match_v2.
    let row: Option<i64> = index
        .query_row(
            "SELECT package FROM productcodes2 WHERE productcode = ?1 LIMIT 1",
            ["{0011-AABB}".to_ascii_lowercase()],
            |row| row.get(0),
        )
        .optional()
        .expect("lookup");
    drop(index);
    assert_eq!(row, Some(1));
    fs::remove_dir_all(root).expect("cleanup");
}

/// Shared with `Wave6DefectTests.CanonicalManifest_TrickyInput_MatchesRustBytes` in the .NET tests.
const TRICKY_MANIFEST: &str = concat!(
    "\u{feff}PackageIdentifier: Pinget.Wave6\n",
    "PackageVersion: 1.0.0\nPackageLocale: en-US\n",
    "Publisher: \"Pinget \\U0001F389 \\\"Q\\\" \\\\ \\u00e9\\u2028\\x01\\x7f\\t\"\n",
    "PackageName: Wave 6 \u{1F389}\n",
    "Author: 0x1F\nInstallationNotes: 12\n",
    "HexValue: 0x1F\nOctalValue: 0o17\nNegativeHex: -0x10\nLeadingZero: 007\n",
    "EmptyValue:\nUpperNull: NULL\nMixedNull: nUlL\nTitleBool: True\nMixedBool: tRuE\n",
    "HugeFloat: 1e999\nFloatValue: 1.50\n",
    "License: MIT\nShortDescription: Wave 6\n",
    "Installers:\n  - Architecture: x64\n    InstallerType: exe\n",
    "    InstallerUrl: https://example.invalid/a.exe\n",
    "    InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111\n",
    "ManifestType: singleton\nManifestVersion: 1.9.0\n",
);

const TRICKY_CANONICAL: &str = concat!(
    "{\"Author\":\"0x1F\",\"EmptyValue\":null,\"FloatValue\":1.5,\"HexValue\":31,\"HugeFloat\":\"1e999\",",
    "\"InstallationNotes\":\"12\",\"Installers\":[{\"Architecture\":\"x64\",",
    "\"InstallerSha256\":\"1111111111111111111111111111111111111111111111111111111111111111\",",
    "\"InstallerType\":\"exe\",\"InstallerUrl\":\"https://example.invalid/a.exe\"}],",
    "\"LeadingZero\":\"007\",\"License\":\"MIT\",\"ManifestType\":\"singleton\",\"ManifestVersion\":\"1.9.0\",",
    "\"MixedBool\":\"tRuE\",\"MixedNull\":\"nUlL\",\"NegativeHex\":-16,\"OctalValue\":15,",
    "\"PackageIdentifier\":\"Pinget.Wave6\",\"PackageLocale\":\"en-US\",\"PackageName\":\"Wave 6 \u{1F389}\",",
    "\"PackageVersion\":\"1.0.0\",",
    "\"Publisher\":\"Pinget \u{1F389} \\\"Q\\\" \\\\ \u{e9}\u{2028}\\u0001\u{7f}\\t\",",
    "\"ShortDescription\":\"Wave 6\",\"TitleBool\":true,\"UpperNull\":null}\n",
);

#[test]
fn p2_p3_p5_canonical_manifest_bytes_match_the_shared_cross_language_vector() {
    assert_eq!(
        canonical_manifest("tricky", TRICKY_MANIFEST.as_bytes()),
        TRICKY_CANONICAL
    );
}

/// Pins the inventory form `Wave6DefectTests.Build_NonBmpPathsAndIdentifiers_UseRawUtf8InBuildIdAndVersionData`
/// recomputes on the .NET side: raw UTF-8, no surrogate escapes.
#[test]
fn p2_inventory_json_keeps_non_bmp_paths_as_raw_utf8() {
    let inventory: BTreeMap<String, String> = [("\u{1F389}.yaml".to_owned(), "ab".to_owned())].into_iter().collect();
    assert_eq!(
        canonical_inventory_json(&inventory).expect("inventory"),
        "{\"\u{1F389}.yaml\":\"ab\"}"
    );
    assert_eq!(json_quote("\u{1F389}\u{1}").expect("quote"), "\"\u{1F389}\\u0001\"");
}

#[test]
fn p3_utf8_bom_does_not_change_canonical_manifest() {
    let plain = singleton("Pinget.Wave6", "Publisher: Pinget\n");
    let mut bom = vec![0xef, 0xbb, 0xbf];
    bom.extend_from_slice(plain.as_bytes());
    assert_eq!(
        canonical_manifest("bom", &bom),
        canonical_manifest("plain", plain.as_bytes())
    );
}

#[test]
fn p6_package_identifier_follows_winget_schema_segments() {
    for id in [
        "Microsoft.VCRedist.2015+.x64",
        "Notepad++.Notepad++",
        "Foo.B\u{e4}r",
        "A.B",
        "Pinget.Test_1.(x86)",
        "a.b.c.d.e.f.g.h",
    ] {
        validate_package_identifier(id).unwrap_or_else(|error| panic!("{id} should be valid: {error}"));
    }
    let long_segment = format!("A.{}", "b".repeat(33));
    let too_long = format!(
        "{}.{}.{}.{}.{}",
        "a".repeat(32),
        "b".repeat(32),
        "c".repeat(32),
        "d".repeat(32),
        "e"
    );
    for id in [
        "Single",
        "A..B",
        "A.B.",
        ".A.B",
        "A B.C",
        "A.B\t",
        "A.B/C",
        "A.B\\C",
        "A.B:C",
        "A.B*",
        "A.B?",
        "A.\"B\"",
        "A.<B>",
        "A.B|C",
        "A.B\u{1}",
        "a.b.c.d.e.f.g.h.i",
        long_segment.as_str(),
        too_long.as_str(),
    ] {
        assert!(validate_package_identifier(id).is_err(), "{id:?} should be rejected");
    }
}

#[test]
fn p7_windows_unsafe_names_are_rejected_for_identifiers_versions_and_paths() {
    for id in ["NUL.Foo", "con.app", "COM1.Tool", "lpt9.x", "Aux.Win", "COM\u{b9}.x"] {
        assert!(validate_package_identifier(id).is_err(), "{id:?} should be rejected");
    }
    for version in ["1.0.", "CON", "nul", "aux.1", "LPT1", "com0"] {
        assert!(
            validate_package_version(version).is_err(),
            "{version:?} should be rejected"
        );
    }
    for version in ["1.0.0", "1.0+build", "CONSOLE", "2024.01"] {
        validate_package_version(version).unwrap_or_else(|error| panic!("{version} should be valid: {error}"));
    }
    let root = Path::new("root");
    assert!(safe_join(root, "manifests/NUL/1.0/x.yaml").is_err());
    assert!(safe_join(root, "manifests/A.B./1.0/x.yaml").is_err());
    assert!(safe_join(root, "manifests/A.B /1.0/x.yaml").is_err());
    assert!(safe_join(root, "manifests/A.B/1.0/en-US/0123456789abcdef.yaml").is_ok());
}

#[test]
fn p9_msix_version_is_normalized_like_dotnet_version() {
    assert_eq!(normalize_msix_version("01.0.0.0").expect("valid"), "1.0.0.0");
    assert_eq!(normalize_msix_version("1.02.003.0004").expect("valid"), "1.2.3.4");
    assert_eq!(normalize_msix_version("65535.0.0.0").expect("valid"), "65535.0.0.0");
    for invalid in ["+1.0.0.0", " 1.0.0.0", "1.0.0", "65536.0.0.0", "1.0.0.-1"] {
        assert!(
            normalize_msix_version(invalid).is_err(),
            "{invalid:?} should be rejected"
        );
    }
    assert_eq!(
        compare_quad_version("01.0.0.0", "1.0.0.0").expect("compare"),
        std::cmp::Ordering::Equal
    );
}

#[test]
fn p10_publisher_temporary_names_are_recognized_narrowly() {
    assert!(is_rust_publish_staging_name(&format!(
        ".publish-{}.staging",
        "a".repeat(64)
    )));
    assert!(!is_rust_publish_staging_name(".publish-abc.staging"));
    assert!(is_dotnet_atomic_temporary_name(&format!(
        "source2.msix.{}.tmp",
        "0f".repeat(16)
    )));
    assert!(is_dotnet_atomic_temporary_name(&format!(
        ".pinget-publisher.json.{}.tmp",
        "1".repeat(32)
    )));
    assert!(!is_dotnet_atomic_temporary_name("notes.tmp"));
    assert!(!is_dotnet_atomic_temporary_name(&format!("{}.tmp", "1".repeat(32))));
    assert!(!is_dotnet_atomic_temporary_name(&format!(
        "file.{}.tmp",
        "A".repeat(32)
    )));
}

#[cfg(windows)]
fn publish_options(state: &Path, destination: &Path, build_id: &str, version: &str) -> PublishOptions {
    let msix = state.parent().expect("root").join(format!(
        "staged-{build_id}-{version}-{}.msix",
        unique_temporary_suffix()
    ));
    package(&PackageOptions {
        state: state.to_path_buf(),
        build_id: Some(build_id.to_owned()),
        msix: msix.clone(),
        name: "Pinget.Test.Wave6".to_owned(),
        publisher: "CN=Pinget Wave6 Test".to_owned(),
        version: version.to_owned(),
        destination: None,
    })
    .expect("package");
    PublishOptions {
        state: state.to_path_buf(),
        destination: destination.to_path_buf(),
        build_id: Some(build_id.to_owned()),
        msix,
        unsigned_development: true,
        psign_tool: None,
        trusted_ca: Vec::new(),
    }
}

#[cfg(windows)]
fn build_fixture(root: &Path) -> (PathBuf, String) {
    let input = root.join("input");
    fs::create_dir(&input).expect("input");
    fs::write(
        input.join("manifest.yaml"),
        singleton("Pinget.Wave6", "Publisher: Pinget\n"),
    )
    .expect("manifest");
    let state = root.join("state");
    let report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("build");
    (state, report.build_id)
}

#[cfg(windows)]
#[test]
fn p9_publish_records_canonical_version_and_reuses_equal_spelling() {
    let root = wave6_directory("p9-publish");
    let (state, build_id) = build_fixture(&root);
    let destination = root.join("feed");
    let first = publish(&publish_options(&state, &destination, &build_id, "01.0.0.0")).expect("publish");
    assert!(!first.reused);
    let marker = read_owner_marker(&destination.join(".pinget-publisher.json"))
        .expect("marker")
        .expect("owned");
    assert_eq!(marker.package_version, "1.0.0.0");
    let second = publish(&publish_options(&state, &destination, &build_id, "1.0.0.0")).expect("republish");
    assert!(second.reused, "numerically equal version of the same build is a reuse");
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn p10_interrupted_publish_temporaries_do_not_block_next_publish() {
    let root = wave6_directory("p10-publish");
    let (state, build_id) = build_fixture(&root);
    let destination = root.join("feed");
    publish(&publish_options(&state, &destination, &build_id, "1.0.0.0")).expect("first publish");

    let staging = destination.join(format!(".publish-{build_id}.staging"));
    fs::create_dir(&staging).expect("stale staging");
    fs::write(staging.join("source2.msix"), b"partial").expect("partial package");
    fs::write(destination.join(format!(".source2.{build_id}.1.2.new")), b"partial").expect("stale package");
    fs::write(destination.join(".pinget-publisher.json.new"), b"{").expect("stale marker");
    let guid = "0123456789abcdef0123456789abcdef";
    fs::write(destination.join(format!("source2.msix.{guid}.tmp")), b"partial").expect("dotnet temp");
    let sidecar_directory = fs::read_dir(destination.join("packages").join("Pinget.Wave6"))
        .expect("packages")
        .next()
        .expect("sidecar directory")
        .expect("entry")
        .path();
    fs::write(
        sidecar_directory.join(format!("versionData.mszyml.{guid}.tmp")),
        b"partial",
    )
    .expect("nested temp");

    let report = publish(&publish_options(&state, &destination, &build_id, "1.0.0.1")).expect("second publish");
    assert!(report.valid && !report.reused);
    verify_published_destination(&destination, None, &[]).expect("clean destination");
    assert!(!staging.exists());

    fs::write(destination.join("unrelated.txt"), b"keep").expect("untracked file");
    assert!(
        publish(&publish_options(&state, &destination, &build_id, "1.0.0.2")).is_err(),
        "non-temporary untracked files are still rejected"
    );
    assert!(
        destination.join("unrelated.txt").is_file(),
        "untracked files are never deleted"
    );
    fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn p11_multi_file_manifests_require_one_manifest_version() {
    let version = "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.9.0\n";
    let locale = "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nPackageLocale: en-US\nPublisher: Pinget\nPackageName: Wave Six\nLicense: MIT\nShortDescription: Wave 6\nManifestType: defaultLocale\nManifestVersion: 1.9.0\n";
    let installer = format!(
        "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {HASH}\nManifestType: installer\nManifestVersion: 1.9.0\n"
    );
    merge_tree(
        "p11-same",
        &[
            ("v.yaml", version.as_bytes()),
            ("l.yaml", locale.as_bytes()),
            ("i.yaml", installer.as_bytes()),
        ],
    )
    .expect("consistent ManifestVersion merges");
    let mixed = installer.replace("ManifestVersion: 1.9.0", "ManifestVersion: 1.10.0");
    let error = merge_tree(
        "p11-mixed",
        &[
            ("v.yaml", version.as_bytes()),
            ("l.yaml", locale.as_bytes()),
            ("i.yaml", mixed.as_bytes()),
        ],
    )
    .expect_err("mixed ManifestVersion must fail");
    assert!(error.to_string().contains("ManifestVersion"), "{error}");
}

#[test]
fn p12_publisher_is_required_like_the_dotnet_builder() {
    let error = merge_tree("p12-singleton", &[("m.yaml", singleton("Pinget.Wave6", "").as_bytes())])
        .expect_err("singleton without Publisher");
    assert!(error.to_string().contains("Publisher"), "{error}");
    merge_tree(
        "p12-ok",
        &[("m.yaml", singleton("Pinget.Wave6", "Publisher: Pinget\n").as_bytes())],
    )
    .expect("singleton with Publisher");

    let version = "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nDefaultLocale: en-US\nManifestType: version\nManifestVersion: 1.9.0\n";
    let locale = "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nPackageLocale: en-US\nPackageName: Wave Six\nLicense: MIT\nShortDescription: Wave 6\nManifestType: defaultLocale\nManifestVersion: 1.9.0\n";
    let installer = format!(
        "PackageIdentifier: Pinget.Wave6\nPackageVersion: 1.0.0\nInstallers:\n  - Architecture: x64\n    InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {HASH}\nManifestType: installer\nManifestVersion: 1.9.0\n"
    );
    let error = merge_tree(
        "p12-multi",
        &[
            ("v.yaml", version.as_bytes()),
            ("l.yaml", locale.as_bytes()),
            ("i.yaml", installer.as_bytes()),
        ],
    )
    .expect_err("multi-file without Publisher");
    assert!(error.to_string().contains("Publisher"), "{error}");
}
