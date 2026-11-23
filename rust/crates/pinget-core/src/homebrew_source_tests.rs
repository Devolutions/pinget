use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use super::{Repository, RepositoryOptions, SourceKind, homebrew_source, parse_packaged_source_kind};

static TEMP_ID: AtomicU64 = AtomicU64::new(0);

fn temporary_directory() -> PathBuf {
    let path = std::env::temp_dir().join(format!(
        "pinget-homebrew-source-test-{}-{}",
        std::process::id(),
        TEMP_ID.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir_all(&path).expect("create test directory");
    path
}

#[test]
fn source_registration_persists_publisher_and_trust_anchors() {
    if cfg!(windows) {
        return;
    }

    let app_root = temporary_directory();
    let root = app_root.join("test-root.cer");
    fs::write(&root, b"test certificate placeholder").expect("write test root");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.clone())).expect("open repository");
    repository
        .add_homebrew_source(
            "homebrew",
            "https://packages.example.test/brew/",
            "CN=Pinget Homebrew Source",
            std::slice::from_ref(&root),
        )
        .expect("register Homebrew source");

    let source = repository
        .list_sources()
        .into_iter()
        .find(|source| source.name == "homebrew")
        .expect("registered Homebrew source");
    assert_eq!(source.kind, SourceKind::HomebrewPreIndexed);
    assert_eq!(source.arg, "https://packages.example.test/brew");
    assert_eq!(source.publisher.as_deref(), Some("CN=Pinget Homebrew Source"));
    assert_eq!(
        source.trusted_ca,
        [root
            .canonicalize()
            .expect("canonical root")
            .to_string_lossy()
            .into_owned()]
    );

    let reopened = Repository::open_with_options(RepositoryOptions::new(app_root.clone())).expect("reopen repository");
    let restored = reopened
        .list_sources()
        .into_iter()
        .find(|source| source.name == "homebrew")
        .expect("persisted Homebrew source");
    assert_eq!(restored.publisher, source.publisher);
    assert_eq!(restored.trusted_ca, source.trusted_ca);
    assert_eq!(
        parse_packaged_source_kind("Pinget.PreIndexed.Homebrew"),
        Some(SourceKind::HomebrewPreIndexed)
    );

    fs::remove_dir_all(app_root).expect("remove test directory");
}

#[test]
fn source_registration_rejects_secret_or_ambiguous_urls() {
    if cfg!(windows) {
        return;
    }

    let app_root = temporary_directory();
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.clone())).expect("open repository");
    for url in [
        "https://user@packages.example.test/brew",
        "https://packages.example.test/brew?token=secret",
        "https://packages.example.test/brew/../private",
        "https://packages.example.test/brew%2fprivate",
    ] {
        assert!(
            repository
                .add_homebrew_source("homebrew", url, "CN=Pinget Homebrew Source", &[])
                .is_err(),
            "should reject {url}"
        );
    }
    fs::remove_dir_all(app_root).expect("remove test directory");
}

#[test]
fn msix_verification_requires_a_present_psign_tool() {
    let directory = temporary_directory();
    let package_path = directory.join("brew.msix");
    let file = fs::File::create(&package_path).expect("create fake MSIX");
    let mut archive = zip::ZipWriter::new(file);
    let options = zip::write::SimpleFileOptions::default();
    archive
        .start_file("AppxManifest.xml", options)
        .expect("start package manifest");
    std::io::Write::write_all(
        &mut archive,
        br#"<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Pinget.HomebrewSource" Publisher="CN=Pinget Homebrew Source" Version="1.0.0.0" /></Package>"#,
    )
    .expect("write package manifest");
    archive
        .start_file("AppxSignature.p7x", options)
        .expect("start package signature");
    std::io::Write::write_all(&mut archive, &[1, 2, 3]).expect("write fake signature");
    archive.finish().expect("finish fake MSIX");

    let source = super::SourceRecord {
        name: "homebrew".to_owned(),
        kind: SourceKind::HomebrewPreIndexed,
        arg: "https://packages.example.test/brew".to_owned(),
        identifier: "homebrew".to_owned(),
        trust_level: "Trusted".to_owned(),
        explicit: false,
        priority: 0,
        last_update: None,
        source_version: None,
        etag: None,
        last_modified: None,
        publisher: Some("CN=Pinget Homebrew Source".to_owned()),
        trusted_ca: Vec::new(),
    };
    let missing_tool = directory.join("missing-psign-tool");
    let error = homebrew_source::verify_msix(&package_path, &source, Some(&missing_tool))
        .expect_err("missing verifier must fail");
    assert!(error.to_string().contains("psign-tool does not exist"));

    fs::remove_dir_all(directory).expect("remove test directory");
}
