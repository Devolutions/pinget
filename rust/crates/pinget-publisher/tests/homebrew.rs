use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use base64::Engine;
use pinget_publisher::homebrew::{ImportOptions, import};
use pinget_publisher::{PackageOptions, PublishOptions};
use serde_json::{Value, json};

const FORMULA: &str = include_str!("../../../../tests/homebrew/formula.json");
const BOTTLE: &str = include_str!("../../../../tests/homebrew/bottle.base64");
const WINDOWS_MANIFEST: &str =
    include_str!("../../../../tests/publisher/fixtures/singleton/Pinget.Contract.Singleton.yaml");
static NEXT: AtomicU64 = AtomicU64::new(0);

struct Fixture {
    root: PathBuf,
    options: ImportOptions,
}

impl Fixture {
    fn new() -> Self {
        let root = std::env::current_dir()
            .expect("current directory")
            .join("target/homebrew-tests")
            .join(format!(
                "{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
        fs::create_dir_all(&root).expect("fixture directory");
        let options = ImportOptions {
            formula_json: root.join("formula.json"),
            bottle_archive: root.join("input.tar.gz"),
            manifest_output: root.join("manifests"),
            bottle_mirror: root.join("bottles"),
            tag: "arm64_sonoma".into(),
            target_os: "macOS".into(),
            architecture: "arm64".into(),
            base_url: "https://mirror.example.invalid/bottles/".into(),
        };
        fs::write(&options.formula_json, FORMULA).expect("write pinned formula");
        fs::write(
            &options.bottle_archive,
            base64::engine::general_purpose::STANDARD
                .decode(BOTTLE.trim())
                .expect("decode pinned bottle"),
        )
        .expect("write pinned bottle");
        Self { root, options }
    }

    fn change(&self, mutate: impl FnOnce(&mut Value)) {
        let mut formula: Value = serde_json::from_str(FORMULA).expect("pinned formula JSON");
        mutate(&mut formula);
        fs::write(
            &self.options.formula_json,
            serde_json::to_vec(&formula).expect("serialize formula"),
        )
        .expect("write formula");
    }

    fn reject(&self, message: &str) {
        let error = import(&self.options).expect_err("invalid import must fail").to_string();
        assert!(error.contains(message), "unexpected error: {error}");
        assert!(!self.options.manifest_output.exists());
        assert!(!self.options.bottle_mirror.exists());
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.root).expect("clean fixture");
    }
}

fn publisher_input(fixture: &Fixture) -> PathBuf {
    let input = fixture.root.join("publisher-input");
    let manifests = input.join("manifests");
    let bottles = input.join("bottles");
    fs::create_dir_all(&manifests).expect("manifest bundle directory");
    fs::create_dir_all(&bottles).expect("bottle bundle directory");
    for entry in fs::read_dir(&fixture.options.manifest_output).expect("read generated manifests") {
        let path = entry.expect("manifest entry").path();
        if path
            .extension()
            .is_some_and(|extension| matches!(extension.to_str(), Some("yaml" | "yml")))
        {
            fs::copy(&path, manifests.join(path.file_name().expect("manifest filename"))).expect("bundle manifest");
        }
    }
    for entry in fs::read_dir(&fixture.options.bottle_mirror).expect("read mirrored bottles") {
        let path = entry.expect("bottle entry").path();
        fs::copy(&path, bottles.join(path.file_name().expect("bottle filename"))).expect("bundle bottle");
    }
    input
}

#[test]
fn maps_linux_runtime_dependencies_and_distinguishes_bottle_rebuilds() {
    let mut fixture = Fixture::new();
    fixture.options.tag = "x86_64_linux".into();
    fixture.options.target_os = "Linux".into();
    fixture.options.architecture = "x64".into();
    fixture.change(|value| {
        let mut bottle = value["bottle"]["stable"]["files"]["arm64_sonoma"].clone();
        bottle["url"] = json!(format!(
            "https://ghcr.io/v2/homebrew/core/tinytool/blobs/sha256:{}",
            bottle["sha256"].as_str().expect("hash")
        ));
        bottle["minimum_glibc"] = json!("2.35");
        bottle["loader"] = json!("glibc");
        value["bottle"]["stable"]["files"] = json!({"x86_64_linux": bottle});
        value["dependencies"] = json!(["oniguruma"]);
        value["pinget_runtime_dependencies"] =
            json!([{"full_name":"oniguruma","pkg_version":"6.9.10","bottle_rebuild":0}]);
    });
    let first = import(&fixture.options).expect("import Linux dependency metadata");
    let bytes = fs::read(fixture.options.manifest_output.join(&first.manifest_file)).expect("manifest");
    let parsed: Value = serde_yaml::from_slice(&bytes).expect("native manifest");
    assert_eq!(parsed["Installers"][0]["Architecture"], "x64");
    assert_eq!(first.runtime_dependencies[0]["MinimumVersion"], "6.9.10.bottle.0");
    assert_eq!(
        first.runtime_dependencies[0]["PackageIdentifier"],
        pinget_core::homebrew_package_identifier("homebrew/core", "oniguruma").expect("dependency ID")
    );
    let mut updated: Value = serde_json::from_slice(&fs::read(&fixture.options.formula_json).expect("current formula"))
        .expect("current formula JSON");
    updated["bottle"]["stable"]["rebuild"] = json!(4);
    fs::write(
        &fixture.options.formula_json,
        serde_json::to_vec(&updated).expect("updated formula JSON"),
    )
    .expect("write rebuild-only metadata");
    let next = import(&fixture.options).expect("import rebuild-only update");
    assert_eq!(next.package_version, "1.2.3_1.bottle.4");
    assert_ne!(first.manifest_file, next.manifest_file);
    assert_eq!(first.bottle_file, next.bottle_file);
}

#[test]
fn pinned_import_mirrors_exact_bytes_and_emits_deterministic_manifest_and_report() {
    let fixture = Fixture::new();
    let report = import(&fixture.options).expect("import pinned formula");
    let path = fixture.options.manifest_output.join(&report.manifest_file);
    let manifest_bytes = fs::read(&path).expect("read manifest");
    let manifest: Value = serde_yaml::from_slice(&manifest_bytes).expect("parse manifest YAML");
    assert_eq!(
        report.package_identifier,
        pinget_core::homebrew_package_identifier("homebrew/core", "tinytool").expect("formula ID")
    );
    assert_eq!(report.package_version, "1.2.3_1.bottle.3");
    assert_eq!(manifest["ManifestType"], "singleton");
    assert_eq!(manifest["Installers"][0]["InstallerType"], "homebrew-bottle");
    assert_eq!(manifest["PackageUrl"], "https://tinytool.example.invalid/");
    assert_eq!(manifest["ShortDescription"], "Small dependency-free test utility");
    assert_eq!(manifest["Publisher"], "Unknown");
    assert_ne!(manifest["Publisher"], "homebrew/core");
    assert_eq!(manifest["Installers"][0]["InstallerSha256"], report.bottle_sha256);
    let homebrew = &manifest["Pinget"]["Homebrew"];
    assert_eq!(homebrew["Revision"], 1);
    assert_eq!(homebrew["Bottles"][0]["Rebuild"], 3);
    assert_eq!(homebrew["Bottles"][0]["MinimumOSVersion"], "14.0");
    assert_eq!(
        homebrew["Bottles"][0]["Sha256"],
        manifest["Installers"][0]["InstallerSha256"]
    );
    assert_eq!(homebrew["Bottles"][0]["Url"], manifest["Installers"][0]["InstallerUrl"]);
    assert_eq!(homebrew["Aliases"], json!(["small", "tiny"]));
    assert_eq!(
        fs::read(fixture.options.bottle_mirror.join(&report.bottle_file)).expect("mirrored bottle"),
        fs::read(&fixture.options.bottle_archive).expect("input bottle")
    );
    let report_path = fixture
        .options
        .manifest_output
        .join(format!("{}.import.json", report.manifest_file));
    let report_bytes = fs::read(&report_path).expect("read import report");
    assert_eq!(
        serde_json::from_slice::<Value>(&report_bytes).expect("parse import report"),
        serde_json::to_value(&report).expect("serialize import report")
    );
    let input = publisher_input(&fixture);
    let build = pinget_publisher::build(&pinget_publisher::BuildOptions {
        input,
        state: fixture.root.join("state"),
    })
    .expect("generated Homebrew manifest builds into a v2 source snapshot");
    assert_eq!(build.packages, 1);
    assert_eq!(build.manifests, 1);
    let snapshot: Value =
        serde_json::from_slice(&fs::read(build.snapshot_directory.join("snapshot.json")).expect("snapshot metadata"))
            .expect("parse snapshot metadata");
    assert_eq!(snapshot["profile"], "homebrew");
    let mirrored_file = fixture.options.bottle_mirror.join(&report.bottle_file);
    let snapshot_bottle = build
        .snapshot_directory
        .join("feed")
        .join("bottles")
        .join(&report.bottle_file);
    assert_eq!(
        fs::read(&mirrored_file).expect("read mirrored bottle"),
        fs::read(snapshot_bottle).expect("read snapshot bottle")
    );
    let msix = fixture.root.join("package").join("brew.msix");
    pinget_publisher::package(&PackageOptions {
        state: fixture.root.join("state"),
        build_id: Some(build.build_id.clone()),
        msix: msix.clone(),
        name: "Pinget.Test.Homebrew".into(),
        publisher: "CN=Pinget Homebrew".into(),
        version: "1.0.0.0".into(),
        destination: None,
    })
    .expect("package Homebrew snapshot with brew.msix name");
    let destination = fixture.root.join("static");
    pinget_publisher::publish(&PublishOptions {
        state: fixture.root.join("state"),
        build_id: Some(build.build_id),
        destination: destination.clone(),
        msix,
        unsigned_development: true,
        psign_tool: None,
        trusted_ca: Vec::new(),
    })
    .expect("publish Homebrew snapshot");
    assert!(destination.join("brew.msix").is_file());
    assert!(!destination.join("source2.msix").exists());
    let marker: Value =
        serde_json::from_slice(&fs::read(destination.join(".pinget-publisher.json")).expect("ownership marker"))
            .expect("parse ownership marker");
    assert_eq!(marker["packageFileName"], "brew.msix");
    assert_eq!(marker["profile"], "homebrew");
    assert_eq!(
        fs::read(destination.join("bottles").join(&report.bottle_file)).expect("published bottle"),
        fs::read(mirrored_file).expect("read original mirror")
    );
    pinget_publisher::verify_published_destination(&destination, None, &[])
        .expect("verify published brew.msix destination");
    import(&fixture.options).expect("repeat import");
    assert_eq!(fs::read(path).expect("reread manifest"), manifest_bytes);
    assert_eq!(fs::read(report_path).expect("reread report"), report_bytes);
}

#[test]
fn rejects_optional_dependencies_requirements_and_variations_without_outputs() {
    for field in [
        "recommended_dependencies",
        "optional_dependencies",
        "uses_from_macos",
        "requirements",
        "conflicts_with",
    ] {
        let fixture = Fixture::new();
        fixture.change(|v| v[field] = json!(["runtime"]));
        fixture.reject(field);
    }
    let fixture = Fixture::new();
    fixture.change(|v| v["variations"] = json!({"arm64_sonoma": {"dependencies": ["runtime"]}}));
    fixture.reject("variations");
}

#[test]
fn rejects_mixed_homebrew_and_windows_profiles() {
    let fixture = Fixture::new();
    import(&fixture.options).expect("import pinned formula");
    fs::write(
        fixture.options.manifest_output.join("Pinget.Contract.Singleton.yaml"),
        WINDOWS_MANIFEST,
    )
    .expect("write Windows manifest");
    let error = pinget_publisher::build(&pinget_publisher::BuildOptions {
        input: publisher_input(&fixture),
        state: fixture.root.join("state"),
    })
    .expect_err("mixed source profiles are unsupported");
    assert!(error.to_string().contains("cannot mix Homebrew and Windows"), "{error}");
    assert!(!fixture.root.join("state/builds").exists());
}

#[test]
fn publisher_requires_valid_mirrored_bottles_for_homebrew_manifests() {
    let fixture = Fixture::new();
    import(&fixture.options).expect("import pinned formula");
    let missing_input = publisher_input(&fixture);
    let bottle_dir = missing_input.join("bottles");
    let bottle = fs::read_dir(&bottle_dir)
        .expect("bottle bundle")
        .next()
        .expect("bottle asset")
        .expect("bottle entry")
        .path();
    fs::remove_file(bottle).expect("remove mirrored bottle");
    let error = pinget_publisher::build(&pinget_publisher::BuildOptions {
        input: missing_input,
        state: fixture.root.join("missing-state"),
    })
    .expect_err("missing bottle must fail");
    assert!(
        error.to_string().contains("missing from the bottles/ mirror"),
        "{error}"
    );

    let fixture = Fixture::new();
    import(&fixture.options).expect("import pinned formula");
    let corrupt_input = publisher_input(&fixture);
    let bottle = fs::read_dir(corrupt_input.join("bottles"))
        .expect("bottle bundle")
        .next()
        .expect("bottle asset")
        .expect("bottle entry")
        .path();
    fs::write(bottle, b"tampered").expect("tamper bottle");
    let error = pinget_publisher::build(&pinget_publisher::BuildOptions {
        input: corrupt_input,
        state: fixture.root.join("corrupt-state"),
    })
    .expect_err("corrupt bottle must fail");
    assert!(error.to_string().contains("hash does not match filename"), "{error}");
}

#[test]
fn rejects_bad_identity_version_bottle_and_hash_metadata_without_outputs() {
    for (pointer, value, message) in [
        ("/tap", json!("homebrew"), "identity"),
        ("/full_name", json!("other"), "full_name"),
        ("/versions/stable", json!("../bad"), "version"),
        ("/revision", json!(-1), "revision"),
        ("/homepage", json!("https://user:secret@example.invalid/"), "homepage"),
        ("/version_scheme", json!(6), "version_scheme"),
        ("/bottle/stable/rebuild", json!(-1), "rebuild"),
        (
            "/bottle/stable/files/arm64_sonoma/cellar",
            json!("/opt/homebrew/Cellar"),
            "fixed-cellar",
        ),
        ("/bottle/stable/files/arm64_sonoma/sha256", json!("bad"), "sha256"),
        (
            "/bottle/stable/files/arm64_sonoma/sha256",
            json!("a".repeat(64)),
            "SHA-256 mismatch",
        ),
        (
            "/bottle/stable/files/arm64_sonoma/url",
            json!("https://user:secret@example.invalid/bottle"),
            "public HTTPS",
        ),
    ] {
        let fixture = Fixture::new();
        fixture.change(|v| *v.pointer_mut(pointer).expect("fixture field") = value);
        fixture.reject(message);
    }
    let fixture = Fixture::new();
    fixture.change(|v| v["bottle"]["stable"]["files"] = json!({}));
    fixture.reject("no selected bottle");
}

#[test]
fn rejects_unsupported_targets_overlap_and_secret_urls() {
    let mut fixture = Fixture::new();
    fixture.options.tag = "unknown".into();
    fixture.reject("unsupported target");
    fixture.options.tag = "arm64_sonoma".into();
    fixture.options.architecture = "x64".into();
    fixture.reject("unsupported target");
    fixture.options.architecture = "arm64".into();
    for url in [
        "https://user:secret@example.invalid/bottles",
        "https://example.invalid/bottles?token=secret",
        "https://example.invalid/%2e%2e",
    ] {
        fixture.options.base_url = url.into();
        fixture.reject("base URL");
        assert!(
            !import(&fixture.options)
                .expect_err("secret URL rejected")
                .to_string()
                .contains("secret")
        );
    }
    fixture.options.base_url = "https://example.invalid/bottles".into();
    fixture.options.manifest_output = fixture.root.clone();
    assert!(
        import(&fixture.options)
            .expect_err("overlapping inputs rejected")
            .to_string()
            .contains("disjoint")
    );
    fixture.options.manifest_output = fixture.root.join("out");
    fixture.options.bottle_mirror = fixture.root.join("out/bottles");
    fixture.reject("disjoint");
}

#[test]
fn rejects_corrupt_existing_mirror_without_writing_manifests() {
    let fixture = Fixture::new();
    fs::create_dir(&fixture.options.bottle_mirror).expect("mirror directory");
    let formula: Value = serde_json::from_str(FORMULA).expect("pinned formula JSON");
    let hash = formula["bottle"]["stable"]["files"]["arm64_sonoma"]["sha256"]
        .as_str()
        .expect("pinned SHA-256");
    let path = fixture.options.bottle_mirror.join(format!("{hash}.tar.gz"));
    fs::write(&path, "corruption").expect("write corrupt mirror");
    assert!(
        import(&fixture.options)
            .expect_err("corrupt mirror rejected")
            .to_string()
            .contains("different bytes")
    );
    assert!(!fixture.options.manifest_output.exists());
    assert_eq!(fs::read(path).expect("read corrupt mirror"), b"corruption");
}

#[cfg(unix)]
#[test]
fn rejects_symlink_destinations() {
    let mut fixture = Fixture::new();
    let real = fixture.root.join("real");
    fs::create_dir(&real).expect("real directory");
    let link = fixture.root.join("link");
    std::os::unix::fs::symlink(&real, &link).expect("symlink");
    fixture.options.manifest_output = link.join("manifests");
    fixture.reject("symbolic links");
    assert!(
        fs::read_dir(real)
            .expect("real directory remains empty")
            .next()
            .is_none()
    );
}
