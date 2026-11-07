use std::path::PathBuf;

use super::*;

fn temp_path(name: &str) -> PathBuf {
    std::env::temp_dir().join(format!("pinget-wave5-{}-{name}", std::process::id()))
}

// ── C4: HRESULT table ──

#[test]
fn hresult_table_is_sorted_and_unique() {
    assert!(
        error_codes::HRESULTS.windows(2).all(|pair| pair[0].0 < pair[1].0),
        "HRESULT table must be strictly sorted by code"
    );
}

#[test]
fn hresult_lookup_matches_winget_errors_header() {
    assert_eq!(
        error_codes::lookup_hresult(0x8A150001),
        Some(("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "Internal Error"))
    );
    assert_eq!(
        error_codes::lookup_hresult(0x8A150014),
        Some(("APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND", "No packages found"))
    );
    assert_eq!(
        error_codes::lookup_hresult(0x8A15002B),
        Some((
            "APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE",
            "No applicable update found"
        ))
    );
    assert_eq!(
        error_codes::lookup_hresult(0x8A150101),
        Some((
            "APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE",
            "Application is currently running. Exit the application then try again."
        ))
    );
    assert_eq!(
        error_codes::lookup_hresult(0x8A150115).map(|(symbol, _)| symbol),
        Some("APPINSTALLER_CLI_ERROR_INSTALL_CUSTOM_ERROR")
    );
    assert_eq!(
        error_codes::lookup_hresult(0x80070005).map(|(symbol, _)| symbol),
        Some("E_ACCESSDENIED")
    );
    assert_eq!(error_codes::lookup_hresult(0x8A15FFFF), None);
}

/// The C# table in `ErrorCodes.cs` must list exactly the same entries.
#[test]
fn hresult_table_matches_dotnet_table() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../dotnet/src/Devolutions.Pinget.Cli/ErrorCodes.cs");
    let Ok(source) = std::fs::read_to_string(&path) else {
        // The dotnet tree is not always present next to the Rust crates (e.g. a crate-only checkout).
        return;
    };
    let dotnet = source
        .lines()
        .map(str::trim)
        .filter(|line| line.starts_with("(0x"))
        .map(|line| line.trim_end_matches(',').to_owned())
        .collect::<Vec<_>>();
    let rust = error_codes::HRESULTS
        .iter()
        .map(|(code, symbol, description)| format!("(0x{code:08X}, \"{symbol}\", \"{description}\")"))
        .collect::<Vec<_>>();
    assert_eq!(rust, dotnet);
}

// ── C5: error input parsing ──

#[test]
fn error_code_parsing_accepts_hex_unsigned_and_negative_decimal() {
    assert_eq!(error_codes::parse_error_code("0x8A150001"), Some(0x8A150001));
    assert_eq!(error_codes::parse_error_code("0X8a150001"), Some(0x8A150001));
    assert_eq!(error_codes::parse_error_code("2316632065"), Some(0x8A150001));
    assert_eq!(error_codes::parse_error_code("-1978335231"), Some(0x8A150001));
    assert_eq!(error_codes::parse_error_code("-1"), Some(0xFFFF_FFFF));
    assert_eq!(error_codes::parse_error_code("0"), Some(0));
    assert_eq!(error_codes::parse_error_code("4294967295"), Some(0xFFFF_FFFF));
}

#[test]
fn error_code_parsing_rejects_out_of_range_and_garbage() {
    assert_eq!(error_codes::parse_error_code("4294967296"), None);
    assert_eq!(error_codes::parse_error_code("-2147483649"), None);
    assert_eq!(error_codes::parse_error_code("0x100000000"), None);
    assert_eq!(error_codes::parse_error_code("8A150001"), None);
    assert_eq!(error_codes::parse_error_code("not-a-code"), None);
    assert_eq!(error_codes::parse_error_code(""), None);
}

#[test]
fn error_command_accepts_negative_decimal_argument() {
    let cli = Cli::try_parse_from(["pinget", "error", "-1978335231"]).expect("negative decimal should parse");
    match cli.command {
        Some(Commands::ErrorLookup(args)) => assert_eq!(args.input, "-1978335231"),
        _ => panic!("expected error command"),
    }
}

// ── C7: unparsable error input fails ──

#[test]
fn error_lookup_of_unparsable_input_is_an_error() {
    assert!(print_error_lookup("definitely-not-a-number").is_err());
    assert!(print_error_lookup("0x8A150001").is_ok());
    assert!(
        print_error_lookup("0x8A15FFFF").is_ok(),
        "unknown but well-formed codes are not errors"
    );
}

// ── C8: named upgrade with nothing to upgrade ──

#[test]
fn named_upgrade_with_no_target_is_an_error_but_upgrade_all_is_not() {
    assert!(no_applicable_upgrade(true).is_ok());
    let error = no_applicable_upgrade(false).expect_err("named upgrade with no target should fail");
    assert_eq!(error.to_string(), NO_APPLICABLE_UPGRADE_MESSAGE);
}

// ── C9: hash ──

const ABC_SHA256: &str = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

#[test]
fn file_hash_is_lowercase_streamed_sha256() {
    let path = temp_path("hash.txt");
    std::fs::write(&path, b"abc").expect("write temp file");
    let hash = file_hash::sha256_file_hex(&path);
    let _ = std::fs::remove_file(&path);
    assert_eq!(hash.expect("hash file"), ABC_SHA256);

    // More than one read buffer's worth of data.
    let data = vec![b'a'; 200 * 1024];
    let streamed = file_hash::sha256_reader_hex(&mut data.as_slice()).expect("hash reader");
    use sha2::{Digest, Sha256};
    let expected = Sha256::digest(&data)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect::<String>();
    assert_eq!(streamed, expected);
}

#[test]
fn msix_signature_hash_is_sha256_of_appx_signature_entry() {
    let path = temp_path("package.msix");
    {
        let file = std::fs::File::create(&path).expect("create temp msix");
        let mut writer = zip::ZipWriter::new(file);
        let options = zip::write::SimpleFileOptions::default();
        writer.start_file("AppxManifest.xml", options).expect("start manifest");
        writer.write_all(b"<Package/>").expect("write manifest");
        writer
            .start_file("AppxSignature.p7x", options)
            .expect("start signature");
        writer.write_all(b"abc").expect("write signature");
        writer.finish().expect("finish zip");
    }
    let hash = file_hash::msix_signature_sha256_hex(&path);
    let _ = std::fs::remove_file(&path);
    assert_eq!(hash.expect("signature hash"), ABC_SHA256);
}

#[test]
fn msix_signature_hash_of_non_package_is_an_error() {
    let path = temp_path("not-a-package.bin");
    std::fs::write(&path, b"abc").expect("write temp file");
    let result = file_hash::msix_signature_sha256_hex(&path);
    let _ = std::fs::remove_file(&path);
    assert!(result.is_err());
}

// ── C10: --count range ──

#[test]
fn count_must_be_between_1_and_1000() {
    assert_eq!(parse_count("1"), Ok(1));
    assert_eq!(parse_count("1000"), Ok(1000));
    for bad in ["0", "1001", "-1", "abc"] {
        assert_eq!(parse_count(bad), Err(COUNT_OUT_OF_BOUNDS_ERROR.to_owned()), "{bad}");
    }

    for command in ["search", "list", "upgrade"] {
        assert!(
            Cli::try_parse_from(["pinget", command, "x", "--count", "0"]).is_err(),
            "{command} 0"
        );
        assert!(
            Cli::try_parse_from(["pinget", command, "x", "-n", "1001"]).is_err(),
            "{command} 1001"
        );
        assert!(
            Cli::try_parse_from(["pinget", command, "x", "--count", "5"]).is_ok(),
            "{command} 5"
        );
    }
}

// ── C12: import parity (Rust already rejects malformed files and searches on dry-run) ──

#[derive(Default)]
struct SearchOnlyImportRepository {
    searched: Vec<String>,
}

impl ImportRepository for SearchOnlyImportRepository {
    fn search(&mut self, query: &PackageQuery) -> Result<SearchResponse> {
        self.searched.push(query.id.clone().unwrap_or_default());
        Ok(SearchResponse {
            matches: Vec::new(),
            warnings: Vec::new(),
            truncated: false,
        })
    }

    fn install_request(&mut self, _request: &InstallRequest) -> Result<InstallResult> {
        panic!("dry-run must not install")
    }
}

#[test]
fn import_rejects_malformed_files_and_dry_run_searches() {
    let mut repository = SearchOnlyImportRepository::default();
    assert!(do_import_from_content(&mut repository, "{", true, false, false, false, false).is_err());
    assert!(do_import_from_content(&mut repository, r#"{"Sources": 5}"#, true, false, false, false, false).is_err());

    let payload =
        r#"{"Sources":[{"SourceDetails":{"Name":"winget"},"Packages":[{"PackageIdentifier":"Contoso.App"}]}]}"#;
    do_import_from_content(&mut repository, payload, true, false, false, false, false).expect("dry-run import");
    assert_eq!(repository.searched, vec!["Contoso.App".to_owned()]);
}

// ── C15: pin list structured output shape (C# matches this) ──

#[test]
fn pin_list_structured_output_shape() {
    let pins = vec![PinRecord {
        package_id: "Contoso.App".to_owned(),
        version: "1.2.*".to_owned(),
        source_id: "winget".to_owned(),
        pin_type: PinType::Gating,
    }];
    let json = serde_json::to_value(&pins).expect("serialize pins");
    assert_eq!(
        json,
        serde_json::json!([{
            "PackageId": "Contoso.App",
            "Version": "1.2.*",
            "SourceId": "winget",
            "PinType": "Gating",
        }])
    );
}
