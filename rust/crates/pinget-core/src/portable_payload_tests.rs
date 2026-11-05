use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use super::{plan_portable_payload, read_portable_payload, remove_tracked_portable_files};

static NEXT_FIXTURE: AtomicU64 = AtomicU64::new(0);

struct Fixture(PathBuf);

impl Fixture {
    fn new() -> Self {
        let sequence = NEXT_FIXTURE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!("pinget-payload-{}-{sequence}", std::process::id()));
        std::fs::create_dir(&path).expect("create isolated payload fixture");
        Self(path)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn create_archive(path: &Path, names: &[&str]) {
    let file = std::fs::File::create(path).expect("create fixture archive");
    let mut archive = zip::ZipWriter::new(file);
    for name in names {
        archive
            .start_file(*name, zip::write::SimpleFileOptions::default())
            .expect("create fixture entry");
        archive.write_all(b"package").expect("write fixture entry");
    }
    archive.finish().expect("finish fixture archive");
}

#[test]
fn shared_directory_rejects_collisions_and_removes_only_tracked_files() {
    let fixture = Fixture::new();
    let target = fixture.0.join("shared");
    let bin = target.join("bin");
    std::fs::create_dir_all(&bin).expect("create shared directory");
    let unrelated = target.join("keep.txt");
    let binary = bin.join("tool.exe");
    std::fs::write(&unrelated, "user data").expect("write unrelated file");
    std::fs::write(&binary, "existing file").expect("write conflicting file");
    let archive_path = fixture.0.join("package.zip");
    create_archive(&archive_path, &["bin/tool.exe", "docs/manual.txt"]);

    let payload = read_portable_payload(&archive_path, true).expect("read portable payload");
    assert!(plan_portable_payload(&payload, &target, false, &[], &[]).is_err());
    assert_eq!(
        std::fs::read_to_string(&binary).expect("read conflicting file"),
        "existing file"
    );

    std::fs::remove_file(&binary).expect("remove fixture collision");
    let created_directories = plan_portable_payload(&payload, &target, false, &[], &[]).expect("plan payload");
    let file = std::fs::File::open(&archive_path).expect("open fixture archive");
    zip::ZipArchive::new(file)
        .expect("read fixture archive")
        .extract(&target)
        .expect("extract fixture archive");
    assert_eq!(
        std::fs::read_to_string(&binary).expect("read installed file"),
        "package"
    );
    assert!(target.join("docs").join("manual.txt").is_file());

    remove_tracked_portable_files(&target, &payload.files, &created_directories).expect("remove tracked files");
    assert_eq!(
        std::fs::read_to_string(&unrelated).expect("read unrelated file"),
        "user data"
    );
    assert!(!binary.exists());
    assert!(bin.is_dir());
    assert!(!target.join("docs").exists());
    assert!(target.is_dir());
}

#[test]
fn upgrade_can_replace_an_owned_file_in_a_shared_directory() {
    let fixture = Fixture::new();
    let binary = fixture.0.join("tool.exe");
    std::fs::write(&binary, "old").expect("write existing file");
    let archive = fixture.0.join("package.zip");
    create_archive(&archive, &["tool.exe"]);
    let payload = read_portable_payload(&archive, true).expect("read portable payload");

    plan_portable_payload(&payload, &fixture.0, false, &["tool.exe".to_owned()], &[])
        .expect("allow replacement of tracked file");
    assert_eq!(std::fs::read_to_string(&binary).expect("read existing file"), "old");
    let file = std::fs::File::open(archive).expect("open fixture archive");
    zip::ZipArchive::new(file)
        .expect("read fixture archive")
        .extract(&fixture.0)
        .expect("replace tracked file");
    assert_eq!(
        std::fs::read_to_string(binary).expect("read replacement file"),
        "package"
    );
}

#[test]
fn invalid_and_duplicate_archive_paths_are_rejected() {
    for (index, names) in [
        vec!["../escape.exe"],
        vec!["C:/escape.exe"],
        vec!["folder/CON.txt"],
        vec!["Tool.exe", "tool.exe"],
    ]
    .into_iter()
    .enumerate()
    {
        let fixture = Fixture::new();
        let archive = fixture.0.join(format!("package-{index}.zip"));
        create_archive(&archive, &names);
        assert!(
            read_portable_payload(&archive, true).is_err(),
            "archive entries {names:?}"
        );
    }
}
