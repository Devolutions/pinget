//! Review fixes B: recovery of a publish killed mid package-swap while publisher temporaries remain.

#![cfg_attr(not(windows), allow(unused_imports, dead_code))]

use super::*;

const HASH: &str = "1111111111111111111111111111111111111111111111111111111111111111";
const GUID: &str = "0123456789abcdef0123456789abcdef";

fn review_directory(label: &str) -> PathBuf {
    let name = format!(
        ".publisher-review-b-{label}-{}-{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
    );
    let directory = std::env::current_dir().expect("cwd").join(name);
    fs::create_dir(&directory).expect("create unit directory");
    directory
}

fn manifest(version: &str) -> String {
    format!(
        "PackageIdentifier: Pinget.ReviewB\nPackageVersion: {version}\nPackageLocale: en-US\nPackageName: Review B\n\
         Publisher: Pinget\nLicense: MIT\nShortDescription: Review B\nInstallers:\n  - Architecture: x64\n    \
         InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    InstallerSha256: {HASH}\n\
         ManifestType: singleton\nManifestVersion: 1.9.0\n"
    )
}

/// Builds snapshot A (1.0.0) and snapshot B (1.0.0 + 2.0.0) in one state directory.
fn two_builds(root: &Path) -> (PathBuf, String, String) {
    let state = root.join("state");
    let mut builds = Vec::new();
    for (label, versions) in [("a", &["1.0.0"][..]), ("b", &["1.0.0", "2.0.0"][..])] {
        let input = root.join(format!("input-{label}"));
        fs::create_dir(&input).expect("input");
        for version in versions {
            fs::write(input.join(format!("{version}.yaml")), manifest(version)).expect("manifest");
        }
        let report = build(&BuildOptions {
            input,
            state: state.clone(),
        })
        .expect("build");
        builds.push(report.build_id);
    }
    let build_b = builds.pop().expect("build B");
    let build_a = builds.pop().expect("build A");
    (state, build_a, build_b)
}

fn options(state: &Path, destination: &Path, build_id: &str, version: &str) -> PublishOptions {
    PublishOptions {
        state: state.to_path_buf(),
        destination: destination.to_path_buf(),
        build_id: Some(build_id.to_owned()),
        name: "Pinget.Test.ReviewB".to_owned(),
        publisher: "CN=Pinget Review B Test".to_owned(),
        version: version.to_owned(),
        signing: Signing::UnsignedDevelopment,
        psign_tool: None,
        trusted_ca: Vec::new(),
        timestamp_url: None,
    }
}

fn copy_tree(source: &Path, target: &Path) {
    fs::create_dir_all(target).expect("target directory");
    for entry in fs::read_dir(source).expect("source directory") {
        let entry = entry.expect("entry");
        let destination = target.join(entry.file_name());
        if entry.file_type().expect("file type").is_dir() {
            copy_tree(&entry.path(), &destination);
        } else {
            fs::copy(entry.path(), destination).expect("copy file");
        }
    }
}

fn root_names(directory: &Path) -> Vec<String> {
    let mut names = fs::read_dir(directory)
        .expect("directory")
        .map(|entry| entry.expect("entry").file_name().to_string_lossy().into_owned())
        .collect::<Vec<_>>();
    names.sort();
    names
}

fn marker_json(destination: &Path) -> serde_json::Value {
    serde_json::from_slice(&fs::read(destination.join(".pinget-publisher.json")).expect("marker")).expect("json")
}

/// Publishes A, then publishes B while copying the destination at `step`, which is exactly what a
/// process killed at that point leaves on disk. Returns the copy and B's completed package.
fn interrupted_copy(root: &Path, state: &Path, build_a: &str, build_b: &str, step: CommitStep) -> (PathBuf, Vec<u8>) {
    let destination = root.join("feed");
    let killed = root.join(format!("killed-{step:?}"));
    publish(&options(state, &destination, build_a, "1.0.0.0")).expect("publish A");
    let mut captured = false;
    publish_with_hooks(
        &options(state, &destination, build_b, "1.0.0.1"),
        |_| Ok(()),
        |current, feed| {
            if current == step {
                let staging = state.join(format!(".publish-{build_b}.staging"));
                assert!(staging.is_dir(), "the package is staged under the private state");
                copy_tree(feed, &killed);
                captured = true;
            }
        },
    )
    .expect("publish B");
    assert!(captured);
    let package_b = fs::read(destination.join("source2.msix")).expect("package B");
    (killed, package_b)
}

/// Leaves what the killed run left in the private state: its staging directory.
fn leave_state_staging(state: &Path, build_id: &str) -> PathBuf {
    let staging = state.join(format!(".publish-{build_id}.staging"));
    fs::create_dir(&staging).expect("state staging");
    fs::write(staging.join("source2.msix"), b"partial").expect("partial package");
    staging
}

#[cfg(windows)]
#[test]
fn killed_before_package_swap_with_staged_package_left_is_rolled_back_then_republished() {
    let root = review_directory("before-swap");
    let (state, build_a, build_b) = two_builds(&root);
    let (killed, _) = interrupted_copy(&root, &state, &build_a, &build_b, CommitStep::BeforePackageSwap);
    assert!(marker_json(&killed).get("pendingCommit").is_some());
    let names = root_names(&killed);
    assert!(
        names
            .iter()
            .any(|name| name.starts_with(".source2.") && name.ends_with(".new")),
        "the staged package is left behind: {names:?}"
    );
    assert!(
        !names.iter().any(|name| name.starts_with(".publish-")),
        "no staging directory in the destination: {names:?}"
    );
    // A .NET publisher killed while copying the package in leaves a partial copy instead.
    fs::write(killed.join(format!("source2.msix.{GUID}.tmp")), b"partial").expect("partial copy");
    let state_staging = leave_state_staging(&state, &build_b);

    let verified = verify_published_destination(&killed, None, &[]).expect("rolled back view");
    assert_eq!(verified.build_id.as_deref(), Some(build_a.as_str()));

    // Unrelated files are still untracked while a swap is pending.
    let unrelated = root.join("unrelated");
    copy_tree(&killed, &unrelated);
    fs::write(unrelated.join("keep-me.txt"), b"keep").expect("unrelated file");
    let error = verify_published_destination(&unrelated, None, &[]).expect_err("unrelated file");
    assert!(error.to_string().contains("untracked"), "{error}");
    let error = publish(&options(&state, &unrelated, &build_b, "1.0.0.1")).expect_err("unrelated file");
    assert!(error.to_string().contains("untracked"), "{error}");
    assert!(unrelated.join("keep-me.txt").is_file());

    let report = publish(&options(&state, &killed, &build_b, "1.0.0.1")).expect("republish B");
    assert!(!report.reused);
    assert_eq!(report.build_id, build_b);
    let verified = verify_published_destination(&killed, None, &[]).expect("verified");
    assert_eq!(verified.build_id.as_deref(), Some(build_b.as_str()));
    assert!(marker_json(&killed).get("pendingCommit").is_none());
    let names = root_names(&killed);
    assert!(
        !names
            .iter()
            .any(|name| name.ends_with(".new") || name.ends_with(".tmp") || name.starts_with(".publish-")),
        "temporaries are removed: {names:?}"
    );
    assert!(!state_staging.exists());
    fs::remove_dir_all(root).expect("cleanup");
}

#[cfg(windows)]
#[test]
fn killed_after_package_swap_with_temporaries_left_is_completed_then_reused() {
    let root = review_directory("after-swap");
    let (state, build_a, build_b) = two_builds(&root);
    let (killed, package_b) = interrupted_copy(&root, &state, &build_a, &build_b, CommitStep::AfterPackageSwap);
    assert!(marker_json(&killed).get("pendingCommit").is_some());
    assert_eq!(fs::read(killed.join("source2.msix")).expect("package"), package_b);
    // Killed while writing the final marker, after an earlier Rust layout staged in the destination.
    fs::write(killed.join(".pinget-publisher.json.new"), b"{").expect("partial marker");
    let legacy = killed.join(format!(".publish-{build_b}.staging"));
    fs::create_dir(&legacy).expect("legacy staging");
    fs::write(legacy.join("source2.msix"), &package_b).expect("legacy staged package");

    let verified = verify_published_destination(&killed, None, &[]).expect("committed view");
    assert_eq!(verified.build_id.as_deref(), Some(build_b.as_str()));

    let report = publish(&options(&state, &killed, &build_b, "1.0.0.1")).expect("reuse B");
    assert!(report.reused);
    assert!(marker_json(&killed).get("pendingCommit").is_none());
    assert!(!legacy.exists());
    assert!(!killed.join(".pinget-publisher.json.new").exists());
    verify_published_destination(&killed, None, &[]).expect("verified");

    // Without a pending swap, standalone verification stays strict about staging leftovers.
    fs::create_dir(&legacy).expect("staging");
    fs::write(legacy.join("source2.msix"), &package_b).expect("staged package");
    let error = verify_published_destination(&killed, None, &[]).expect_err("untracked staging");
    assert!(error.to_string().contains("untracked"), "{error}");
    fs::remove_dir_all(root).expect("cleanup");
}

#[test]
fn marker_temporary_names_cover_both_publishers() {
    assert!(is_marker_temporary_name(".pinget-publisher.json.new"));
    assert!(is_marker_temporary_name(&format!(".pinget-publisher.json.{GUID}.tmp")));
    assert!(!is_marker_temporary_name(&format!(
        ".pinget-publisher.json.x.{GUID}.tmp"
    )));
    assert!(!is_marker_temporary_name(&format!("source2.msix.{GUID}.tmp")));
    assert!(!is_marker_temporary_name(".pinget-publisher.json"));
}
