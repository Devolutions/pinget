//! Wave 12 publisher regression tests (publisher round 2).

use super::*;

const HASH: &str = "1111111111111111111111111111111111111111111111111111111111111111";

fn wave12_directory(label: &str) -> PathBuf {
    let name = format!(
        ".publisher-wave12-{label}-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
    );
    let directory = std::env::current_dir().expect("cwd").join(name);
    fs::create_dir(&directory).expect("create unit directory");
    directory
}

/// A singleton manifest; `root` is inserted at the root and `installers` replaces the installer list.
fn manifest(id: &str, version: &str, root: &str, installers: &str) -> String {
    format!(
        "PackageIdentifier: {id}\nPackageVersion: {version}\nPackageLocale: en-US\nPackageName: Wave Twelve\n\
         Publisher: Pinget\nLicense: MIT\nShortDescription: Wave 12\n{root}Installers:\n{installers}\
         ManifestType: singleton\nManifestVersion: 1.9.0\n"
    )
}

fn installer(installer_type: &str, extra: &str) -> String {
    format!(
        "  - Architecture: x64\n    InstallerType: {installer_type}\n    InstallerUrl: https://example.invalid/a.exe\n    \
         InstallerSha256: {HASH}\n{extra}"
    )
}

fn merge(label: &str, files: &[(&str, String)]) -> Result<Vec<PackageManifest>> {
    let root = wave12_directory(label);
    for (name, contents) in files {
        fs::write(root.join(name), contents).expect("write manifest");
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

fn fields_of(label: &str, yaml: String) -> CatalogFields {
    let packages = merge(label, &[("manifest.yaml", yaml)]).expect("merge");
    catalog_fields_all(&packages).expect("catalog fields")
}

#[test]
fn r12_4_arp_range_uses_effective_entries_of_supported_installer_types() {
    // Installer entries override the root entries (WinGet copies root entries only when missing).
    let overriding = manifest(
        "Pinget.Wave12",
        "1.0.0",
        "AppsAndFeaturesEntries:\n  - DisplayVersion: '1.0'\n",
        &[
            installer("exe", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 1.0.5\n"),
            installer("msi", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 1.0.5\n"),
        ]
        .concat(),
    );
    let fields = fields_of("r12-4-override", overriding);
    assert_eq!(
        (fields.7.as_deref(), fields.8.as_deref()),
        (Some("1.0.5"), Some("1.0.5"))
    );

    // Portable and msstore installers never contribute an ARP version range.
    for installer_type in ["portable", "msstore"] {
        let yaml = manifest(
            "Pinget.Wave12",
            "1.0.0",
            "",
            &installer(
                installer_type,
                "    AppsAndFeaturesEntries:\n      - DisplayVersion: 2.0.0\n",
            ),
        );
        let fields = fields_of("r12-4-unsupported", yaml);
        assert_eq!((fields.7, fields.8), (None, None), "{installer_type}");
    }

    // Root entries are inherited by an exe installer, and a zip uses its nested installer type.
    let inherited = manifest(
        "Pinget.Wave12",
        "1.0.0",
        "NestedInstallerType: msi\nAppsAndFeaturesEntries:\n  - DisplayVersion: 3.0.0\n",
        &[installer("exe", ""), installer("zip", ""), installer("portable", "")].concat(),
    );
    let fields = fields_of("r12-4-inherit", inherited);
    assert_eq!(
        (fields.7.as_deref(), fields.8.as_deref()),
        (Some("3.0.0"), Some("3.0.0"))
    );

    // msix supports ranges but does not inherit root entries.
    let msix = manifest(
        "Pinget.Wave12",
        "1.0.0",
        "AppsAndFeaturesEntries:\n  - DisplayVersion: 4.0.0\n",
        &installer("msix", "    AppsAndFeaturesEntries:\n      - DisplayVersion: 4.1.0\n"),
    );
    let fields = fields_of("r12-4-msix", msix);
    assert_eq!(
        (fields.7.as_deref(), fields.8.as_deref()),
        (Some("4.1.0"), Some("4.1.0"))
    );
}

#[test]
fn r12_5_root_system_references_are_inherited_only_when_missing_and_type_appropriate() {
    let yaml = manifest(
        "Pinget.Wave12",
        "1.0.0",
        "ProductCode: '{AAAA}'\nPackageFamilyName: Root.Family_abc\nAppsAndFeaturesEntries:\n  - DisplayName: Root Name\n    \
         Publisher: Root Publisher\n    ProductCode: '{RRRR}'\n    UpgradeCode: '{UUUU}'\n",
        &[
            installer(
                "exe",
                "    ProductCode: '{BBBB}'\n    AppsAndFeaturesEntries:\n      - DisplayName: Own Name\n",
            ),
            installer(
                "msi",
                "    ProductCode: '{CCCC}'\n    AppsAndFeaturesEntries:\n      - Publisher: Own Publisher\n",
            ),
        ]
        .concat(),
    );
    let fields = fields_of("r12-5-override", yaml);
    assert!(
        fields.2.is_empty(),
        "exe/msi installers do not use the root PFN: {:?}",
        fields.2
    );
    assert_eq!(fields.3, vec!["{bbbb}", "{cccc}"]);
    assert!(fields.4.is_empty(), "root ARP entries are overridden: {:?}", fields.4);
    assert_eq!(fields.5, vec!["ownname", "wavetwelve"]);
    assert_eq!(fields.6, vec!["ownpublisher", "pinget"]);

    let inherited = manifest(
        "Pinget.Wave12",
        "1.0.0",
        "ProductCode: '{AAAA}'\nPackageFamilyName: Root.Family_abc\n",
        &[installer("msix", ""), installer("exe", "")].concat(),
    );
    let fields = fields_of("r12-5-inherit", inherited);
    assert_eq!(fields.2, vec!["root.family_abc"], "msix inherits the root PFN");
    assert_eq!(fields.3, vec!["{aaaa}"], "exe inherits the root ProductCode");
}

#[test]
fn r12_6_overlapping_arp_ranges_across_versions_fail_the_build() {
    let entries = |versions: &[&str]| {
        let mut text = String::from("    AppsAndFeaturesEntries:\n");
        for version in versions {
            text.push_str(&format!("      - DisplayVersion: '{version}'\n"));
        }
        text
    };
    let overlapping = [
        (
            "a.yaml",
            manifest("Pinget.Wave12", "1.0", "", &installer("exe", &entries(&["1.0"]))),
        ),
        (
            "b.yaml",
            manifest("Pinget.Wave12", "2.0", "", &installer("exe", &entries(&["1.0", "2.0"]))),
        ),
    ];
    let packages = merge("r12-6", &overlapping).expect("merge");
    let error = reject_overlapping_arp_ranges("Pinget.Wave12", &packages).expect_err("overlap must fail");
    assert_eq!(
        error.to_string(),
        "ARP version ranges overlap for Pinget.Wave12: 1.0 [1.0, 1.0] and 2.0 [1.0, 2.0]"
    );

    let disjoint = [
        (
            "a.yaml",
            manifest("Pinget.Wave12", "1.0", "", &installer("exe", &entries(&["1.0"]))),
        ),
        (
            "b.yaml",
            manifest("Pinget.Wave12", "2.0", "", &installer("exe", &entries(&["1.5", "2.0"]))),
        ),
        ("c.yaml", manifest("Pinget.Wave12", "3.0", "", &installer("exe", ""))),
    ];
    let packages = merge("r12-6-ok", &disjoint).expect("merge");
    reject_overlapping_arp_ranges("Pinget.Wave12", &packages).expect("disjoint ranges are accepted");
}

#[test]
fn r12_8_identifier_case_fold_matches_dotnet_ordinal_ignore_case() {
    assert_eq!(ordinal_ignore_case_fold("Contoso.\u{e4}pp"), "CONTOSO.\u{c4}PP");
    assert_eq!(ordinal_ignore_case_fold("stra\u{df}e"), "STRA\u{df}E");
    let error = merge(
        "r12-8",
        &[
            ("a.yaml", manifest("Contoso.\u{c4}pp", "1.0", "", &installer("exe", ""))),
            ("b.yaml", manifest("Contoso.\u{e4}pp", "1.0", "", &installer("exe", ""))),
        ],
    )
    .expect_err("Unicode case variants must collide");
    assert!(error.to_string().contains("differs only by casing"), "{error}");
    // OrdinalIgnoreCase orders by uppercase: 'B' (0x42) sorts before '_' (0x5F).
    assert!(ordinal_ignore_case_key("Pinget.Ab") < ordinal_ignore_case_key("Pinget.A_b"));
}

#[test]
fn r12_9_identity_name_follows_appx_package_name_rule() {
    for name in ["Contoso-Source", "abc", "A.B-C.d", &"a".repeat(50)] {
        validate_package_identity(name, "CN=Pinget").unwrap_or_else(|error| panic!("{name}: {error}"));
    }
    for name in ["AB", "A..B", ".abc", "abc.", "a_b_c", "abc def", &"a".repeat(51)] {
        assert!(
            validate_package_identity(name, "CN=Pinget").is_err(),
            "{name:?} should be rejected"
        );
    }
}

#[test]
fn r12_1_superseded_signature_check_ignores_certificate_validity() {
    // Self-signed `CN=Pinget Signer, O="Example, Inc."` code-signing certificate valid only 2000-2001.
    let expired =
        base64::Engine::decode(&base64::engine::general_purpose::STANDARD, EXPIRED_CERTIFICATE).expect("fixture");
    psign::check_signing_certificate(&expired, SIGNER, false).expect("validity is not required");
    assert!(psign::check_signing_certificate(&expired, SIGNER, true).is_err());
    assert!(psign::check_signing_certificate(&expired, "CN=Someone Else", false).is_err());
}

const SIGNER: &str = "CN=Pinget Signer, O=\"Example, Inc.\"";
const EXPIRED_CERTIFICATE: &str = "MIIBbTCCAROgAwIBAgIIWdNsT75jefAwCgYIKoZIzj0EAwIwMDEWMBQGA1UEChMNRXhhbXBsZSwgSW5jLjEWMBQGA1UEAxMNUGluZ2V0IFNpZ25lcjAeFw0wMDAxMDEwMDAwMDBaFw0wMTAxMDEwMDAwMDBaMDAxFjAUBgNVBAoTDUV4YW1wbGUsIEluYy4xFjAUBgNVBAMTDVBpbmdldCBTaWduZXIwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAARRKZtwV+hYyLwxUZn5XEnvBH/hwn57hHWYcaIyVw3eq0cMwi2MknSjL3sK6siSDerWZawJUSaLKRNaBwznveAyoxcwFTATBgNVHSUEDDAKBggrBgEFBQcDAzAKBggqhkjOPQQDAgNIADBFAiBY4yzGE/tbmppuHreYTvBHR/EdQcMr503haoE/pAwSWwIhAJUuSdHf5t+k4Mrw2IC88vy8UnTdNjZ1CGmsNo1GsR5U";

/// Writes a fake `psign-tool` that logs every response file and reports `EXPIRED_CERTIFICATE` as signer.
#[cfg(windows)]
fn fake_psign_tool(root: &Path) -> (PathBuf, PathBuf) {
    let tool = root.join("fake-psign-tool.cmd");
    let log = root.join("psign.log");
    fs::write(
        &tool,
        format!(
            "@echo off\r\nsetlocal DisableDelayedExpansion\r\nset \"RSP=%~1\"\r\ntype \"%RSP:~1%\" >> \"{}\"\r\n\
             echo {{\"signers\":[{{\"signer_certificate_der_base64\":\"{EXPIRED_CERTIFICATE}\"}}]}}\r\nexit /b 0\r\n",
            log.display()
        ),
    )
    .expect("fake tool");
    (tool, log)
}

#[cfg(windows)]
#[test]
fn r12_1_prior_package_with_expired_certificate_is_verified_without_validity_or_trust() {
    let root = wave12_directory("r12-1");
    let (tool, log) = fake_psign_tool(&root);
    let package = root.join("source2.msix");
    let mut writer = zip::ZipWriter::new(File::create(&package).expect("package"));
    writer
        .start_file("AppxSignature.p7x", zip::write::SimpleFileOptions::default())
        .expect("entry");
    writer.write_all(b"PKCX").expect("signature bytes");
    writer.finish().expect("zip");

    psign::verify(&tool, &package, SIGNER, &[], psign::SignaturePolicy::Superseded)
        .expect("a superseded package only needs digests and signer");
    let calls = fs::read_to_string(&log).expect("log");
    assert!(
        calls.contains("verify-msix") && calls.contains("inspect-pkcs7"),
        "{calls}"
    );
    assert!(
        !calls.contains("--policy") && !calls.contains("trust-verify-msix"),
        "{calls}"
    );

    psign::verify(&tool, &package, SIGNER, &[], psign::SignaturePolicy::Published)
        .expect("published packages leave validity to the timestamp-aware trust check");
    assert!(fs::read_to_string(&log).expect("log").contains("--policy"));

    let error = psign::verify(&tool, &package, SIGNER, &[], psign::SignaturePolicy::NewlySigned)
        .expect_err("a newly signed package needs a currently valid certificate");
    assert!(error.to_string().contains("not currently valid"), "{error}");
    let error = psign::verify(
        &tool,
        &package,
        "CN=Someone Else",
        &[],
        psign::SignaturePolicy::Superseded,
    )
    .expect_err("the signer subject is still checked");
    assert!(error.to_string().contains("does not match"), "{error}");
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
fn build_input(root: &Path, label: &str, files: &[(&str, String)]) -> (PathBuf, String) {
    let input = root.join(format!("input-{label}"));
    fs::create_dir(&input).expect("input");
    for (name, contents) in files {
        fs::write(input.join(name), contents).expect("manifest");
    }
    let state = root.join("state");
    let report = build(&BuildOptions {
        input,
        state: state.clone(),
    })
    .expect("build");
    (state, report.build_id)
}

#[cfg(windows)]
fn options(state: &Path, destination: &Path, build_id: &str, version: &str) -> PublishOptions {
    PublishOptions {
        state: state.to_path_buf(),
        destination: destination.to_path_buf(),
        build_id: Some(build_id.to_owned()),
        name: "Pinget.Test.Wave12".to_owned(),
        publisher: "CN=Pinget Wave12 Test".to_owned(),
        version: version.to_owned(),
        signing: Signing::UnsignedDevelopment,
        psign_tool: None,
        trusted_ca: Vec::new(),
        timestamp_url: None,
    }
}

#[cfg(windows)]
fn snapshot_artifacts(state: &Path, build_id: &str) -> BTreeMap<String, String> {
    let snapshot = state.join("builds").join(build_id);
    let metadata: SnapshotMetadata =
        serde_json::from_slice(&fs::read(snapshot.join("snapshot.json")).expect("snapshot")).expect("metadata");
    metadata.artifacts
}

/// Copies a snapshot's feed artifacts into `destination`, as an interrupted publication would.
#[cfg(windows)]
fn copy_snapshot_artifacts(state: &Path, build_id: &str, destination: &Path) {
    let feed = state.join("builds").join(build_id).join("feed");
    for relative in snapshot_artifacts(state, build_id).keys() {
        let target = destination.join(relative);
        if !target.exists() {
            fs::create_dir_all(target.parent().expect("parent")).expect("directories");
            fs::copy(feed.join(relative), &target).expect("copy artifact");
        }
    }
}

#[cfg(windows)]
fn two_builds(root: &Path) -> (PathBuf, String, String) {
    let first = manifest("Pinget.Wave12", "1.0.0", "", &installer("exe", ""));
    let second = manifest("Pinget.Wave12", "2.0.0", "", &installer("exe", ""));
    let (state, build_a) = build_input(root, "a", &[("a.yaml", first.clone())]);
    let (_, build_b) = build_input(root, "b", &[("a.yaml", first), ("b.yaml", second)]);
    (state, build_a, build_b)
}

#[cfg(windows)]
#[test]
fn r12_2_completed_copies_of_an_interrupted_publish_are_adopted() {
    let root = wave12_directory("r12-2a");
    let (state, build_a, build_b) = two_builds(&root);
    let destination = root.join("feed");
    publish(&options(&state, &destination, &build_a, "1.0.0.0")).expect("first publish");
    copy_snapshot_artifacts(&state, &build_b, &destination);
    assert!(
        verify_published_destination(&destination, None, &[]).is_err(),
        "standalone verification still rejects untracked files"
    );

    let report = publish(&options(&state, &destination, &build_b, "1.0.0.1")).expect("retry adopts the copies");
    assert!(!report.reused);
    let verified = verify_published_destination(&destination, None, &[]).expect("verified");
    assert_eq!(verified.build_id.as_deref(), Some(build_b.as_str()));
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn r12_2_interrupted_first_publish_leftovers_are_adopted_but_unrelated_files_are_not() {
    let root = wave12_directory("r12-2b");
    let (state, _, build_b) = two_builds(&root);
    let destination = root.join("feed");
    fs::create_dir(&destination).expect("destination");
    let staging = destination.join(format!(".publish-{build_b}.staging"));
    fs::create_dir(&staging).expect("staging");
    fs::write(staging.join("source2.msix"), b"partial").expect("partial package");
    copy_snapshot_artifacts(&state, &build_b, &destination);
    let manifests = destination.join("manifests");
    fs::write(
        manifests.join(format!("partial.yaml.{}.tmp", "0123456789abcdef".repeat(2))),
        b"partial",
    )
    .expect("partial copy");

    publish(&options(&state, &destination, &build_b, "1.0.0.0")).expect("retry of the first publish");
    verify_published_destination(&destination, None, &[]).expect("verified");
    assert!(!staging.exists());

    let unowned = root.join("unowned");
    fs::create_dir(&unowned).expect("unowned");
    copy_snapshot_artifacts(&state, &build_b, &unowned);
    fs::write(unowned.join("keep-me.txt"), b"keep").expect("unrelated");
    let error = publish(&options(&state, &unowned, &build_b, "1.0.0.0")).expect_err("unrelated content");
    assert!(error.to_string().contains("unowned"), "{error}");
    assert!(unowned.join("keep-me.txt").is_file());
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn r12_2_interrupted_package_swap_is_rolled_back_or_completed() {
    let root = wave12_directory("r12-2c");
    let (state, build_a, build_b) = two_builds(&root);
    let destination = root.join("feed");
    let marker_path = destination.join(".pinget-publisher.json");
    publish(&options(&state, &destination, &build_a, "1.0.0.0")).expect("publish A");
    let marker_a: serde_json::Value = serde_json::from_slice(&fs::read(&marker_path).expect("marker")).expect("json");
    let package_a = fs::read(destination.join("source2.msix")).expect("package A");
    publish(&options(&state, &destination, &build_b, "1.0.0.1")).expect("publish B");
    let marker_b: serde_json::Value = serde_json::from_slice(&fs::read(&marker_path).expect("marker")).expect("json");
    let package_b = fs::read(destination.join("source2.msix")).expect("package B");
    let pending = |previous: serde_json::Value| {
        let mut marker = marker_b.clone();
        marker["pendingCommit"] = serde_json::json!({ "previous": previous });
        serde_json::to_vec_pretty(&marker).expect("marker bytes")
    };

    // Killed after the pending marker was written, before the package swap: rolled back to A.
    fs::write(&marker_path, pending(marker_a.clone())).expect("pending marker");
    fs::write(destination.join("source2.msix"), &package_a).expect("old package");
    let verified = verify_published_destination(&destination, None, &[]).expect("rolled back view");
    assert_eq!(verified.build_id.as_deref(), Some(build_a.as_str()));
    let report = publish(&options(&state, &destination, &build_b, "1.0.0.1")).expect("republish B");
    assert!(!report.reused);
    assert_eq!(
        verify_published_destination(&destination, None, &[])
            .expect("verified")
            .build_id
            .as_deref(),
        Some(build_b.as_str())
    );

    // Killed after the package swap, before the final marker: completed as B.
    fs::write(&marker_path, pending(marker_a)).expect("pending marker");
    assert_eq!(fs::read(destination.join("source2.msix")).expect("package"), package_b);
    let verified = verify_published_destination(&destination, None, &[]).expect("committed view");
    assert_eq!(verified.build_id.as_deref(), Some(build_b.as_str()));
    let report = publish(&options(&state, &destination, &build_b, "1.0.0.1")).expect("reuse B");
    assert!(report.reused);
    let marker: serde_json::Value = serde_json::from_slice(&fs::read(&marker_path).expect("marker")).expect("json");
    assert!(marker.get("pendingCommit").is_none(), "the pending record is finalised");

    // An interrupted first publication that never swapped its package in can be retried.
    let first = root.join("first");
    publish(&options(&state, &first, &build_b, "1.0.0.1")).expect("publish B");
    let first_marker = first.join(".pinget-publisher.json");
    let mut marker: serde_json::Value =
        serde_json::from_slice(&fs::read(&first_marker).expect("marker")).expect("json");
    marker["pendingCommit"] = serde_json::json!({ "previous": null });
    fs::write(&first_marker, serde_json::to_vec_pretty(&marker).expect("bytes")).expect("pending first marker");
    fs::remove_file(first.join("source2.msix")).expect("no package yet");
    let error = verify_published_destination(&first, None, &[]).expect_err("not committed");
    assert!(error.to_string().contains("interrupted"), "{error}");
    publish(&options(&state, &first, &build_b, "1.0.0.1")).expect("retry first publication");
    verify_published_destination(&first, None, &[]).expect("verified");
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn r12_3_package_identifier_casing_change_is_refused_before_copying() {
    let root = wave12_directory("r12-3");
    let first = manifest("Pinget.Wave12", "1.0.0", "", &installer("exe", ""));
    let (state, build_a) = build_input(&root, "a", &[("a.yaml", first)]);
    let destination = root.join("feed");
    publish(&options(&state, &destination, &build_a, "1.0.0.0")).expect("publish");
    let renamed = manifest("pinget.Wave12", "1.0.0", "", &installer("exe", ""));
    let (_, build_b) = build_input(&root, "b", &[("a.yaml", renamed)]);
    let error = publish(&options(&state, &destination, &build_b, "1.0.0.1")).expect_err("casing change");
    assert!(error.to_string().contains("differs only by casing"), "{error}");
    let verified = verify_published_destination(&destination, None, &[]).expect("destination unchanged");
    assert_eq!(verified.build_id.as_deref(), Some(build_a.as_str()));
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn r12_7_package_row_uses_latest_version_arp_range_and_rows_follow_ordinal_ignore_case() {
    let root = wave12_directory("r12-7");
    let with_arp = installer("exe", "    AppsAndFeaturesEntries:\n      - DisplayVersion: '1.0'\n");
    let (state, build_id) = build_input(
        &root,
        "a",
        &[
            ("a.yaml", manifest("Pinget.Wave12", "1.0", "", &with_arp)),
            ("b.yaml", manifest("Pinget.Wave12", "2.0", "", &installer("exe", ""))),
            ("c.yaml", manifest("Pinget.A_b", "1.0", "", &installer("exe", ""))),
            ("d.yaml", manifest("Pinget.Ab", "1.0", "", &installer("exe", ""))),
        ],
    );
    let index = Connection::open(state.join("builds").join(&build_id).join("Public").join("index.db")).expect("index");
    let bounds: (Option<String>, Option<String>) = index
        .query_row(
            "SELECT arp_min_version, arp_max_version FROM packages WHERE id='Pinget.Wave12'",
            [],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .expect("package row");
    assert_eq!(bounds, (None, None), "the latest version has no ARP range");
    let ids: Vec<String> = index
        .prepare("SELECT id FROM packages ORDER BY rowid")
        .expect("query")
        .query_map([], |row| row.get(0))
        .expect("rows")
        .collect::<rusqlite::Result<_>>()
        .expect("ids");
    // Same rowids as the .NET publisher (Wave12DefectTests.Build_PackageRows_UseLatestArpRangeAndOrdinalIgnoreCaseOrder).
    assert_eq!(ids, ["Pinget.Ab", "Pinget.A_b", "Pinget.Wave12"]);
    drop(index);
    fs::remove_dir_all(root).expect("cleanup");
}
