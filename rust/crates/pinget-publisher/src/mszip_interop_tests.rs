//! Non-Windows fixture production using the actual private portable encoder.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::PathBuf;

use sha2::{Digest, Sha256};

const ROOT_ENV: &str = "PINGET_MSZIP_FIXTURE_ROOT";
const RUN_ENV: &str = "PINGET_MSZIP_RUN_ID";
const PROTOCOL: &str = "pinget-portable-mszip-v1";

#[test]
fn portable_encoder_encodes_fixture_matrix_when_requested() {
    let (root, run_id) = match (std::env::var_os(ROOT_ENV), std::env::var(RUN_ENV)) {
        (None, Err(std::env::VarError::NotPresent)) => {
            let original = b"portable encoder smoke check";
            let compressed = crate::compress_mszip(original).expect("portable compression");
            let decoded = pinget_core::decompress_mszip_buffer(&compressed).expect("portable smoke decode");
            assert_eq!(decoded, original);
            return;
        }
        (Some(root), Ok(run_id)) => (PathBuf::from(root), run_id),
        _ => panic!("{ROOT_ENV} and {RUN_ENV} must both be set or both be absent"),
    };

    let manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(root.join("run.json")).expect("read fixture manifest"))
            .expect("parse fixture manifest");
    assert_eq!(manifest["protocol"].as_str(), Some(PROTOCOL));
    assert_eq!(manifest["run_id"].as_str(), Some(run_id.as_str()));
    let cases = manifest["cases"].as_array().expect("fixture cases");
    assert!(!cases.is_empty(), "fixture matrix must not be empty");

    let output = root.join("rust");
    fs::create_dir(&output).expect("create fresh Rust fixture directory");
    for case in cases {
        let name = case["name"].as_str().expect("case name");
        assert!(
            !name.is_empty()
                && name
                    .bytes()
                    .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_'),
            "case name must be report-safe"
        );
        let expected_len = usize::try_from(case["size"].as_u64().expect("case size")).expect("size fits usize");
        let expected_hash = case["sha256"].as_str().expect("input SHA-256");
        let input = fs::read(root.join("inputs").join(format!("{name}.bin"))).expect("read exact original");
        assert_eq!(input.len(), expected_len, "input length for {name}");
        let actual_hash: String = Sha256::digest(&input)
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect();
        assert_eq!(actual_hash, expected_hash, "input hash for {name}");

        let compressed = crate::compress_mszip(&input).expect("production portable MSZIP encoder");
        assert!(compressed.len() >= 24, "missing MSZIP header for {name}");
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(output.join(format!("{name}.mszip")))
            .expect("create new Rust fixture");
        file.write_all(&compressed).expect("write complete Rust fixture");
    }
}

#[test]
fn portable_encoder_rejects_empty_input() {
    let error = crate::compress_mszip(&[]).expect_err("empty input must be rejected");
    assert!(
        matches!(error, crate::PublisherError::Invalid(ref message)
            if message == "MSZIP payload must not be empty"),
        "unexpected empty-input error: {error}"
    );
}

#[test]
fn portable_encoder_rejects_input_over_64_mib() {
    let input = vec![0; crate::MAX_MSZIP_SIZE + 1];
    let error = crate::compress_mszip(&input).expect_err("over-limit input must be rejected");
    assert!(
        matches!(error, crate::PublisherError::Invalid(ref message)
            if message == "MSZIP payload exceeds the 64 MiB limit"),
        "unexpected over-limit error: {error}"
    );
}
