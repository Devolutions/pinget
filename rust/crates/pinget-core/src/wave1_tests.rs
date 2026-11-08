//! Regression tests for the installer-execution defect fixes (wave 1).

use super::*;

fn manifest_from_yaml(yaml: &str) -> Manifest {
    parse_yaml_manifest(yaml.as_bytes()).expect("parse manifest")
}

fn simple_manifest() -> Manifest {
    manifest_from_yaml(
        r#"
PackageIdentifier: Contoso.Tool
PackageVersion: 1.2.3
PackageName: Contoso Tool
Publisher: Contoso
ManifestType: merged
ManifestVersion: 1.10.0
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/setup.exe
    InstallerSha256: ABC123
"#,
    )
}

fn list_match(id: &str, local_id: &str) -> ListMatch {
    ListMatch {
        name: id.to_owned(),
        id: id.to_owned(),
        local_id: local_id.to_owned(),
        installed_version: "1.0.0".to_owned(),
        available_version: None,
        source_name: None,
        publisher: None,
        scope: None,
        installer_category: None,
        install_location: None,
        package_family_names: Vec::new(),
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
    }
}

// ── D1: InstallerSuccessCodes / ExpectedReturnCodes / reboot codes ──

#[test]
fn yaml_manifest_parses_success_and_expected_return_codes_with_root_inheritance() {
    let manifest = manifest_from_yaml(
        r#"
PackageIdentifier: Contoso.Tool
PackageVersion: 1.2.3
PackageName: Contoso Tool
ManifestType: merged
ManifestVersion: 1.10.0
InstallerSuccessCodes:
  - 1
  - -2147024891
ExpectedReturnCodes:
  - InstallerReturnCode: 1641
    ReturnResponse: rebootRequiredToFinish
Installers:
  - InstallerType: exe
    InstallerUrl: https://example.test/a.exe
    InstallerSha256: ABC123
  - InstallerType: exe
    InstallerUrl: https://example.test/b.exe
    InstallerSha256: DEF456
    InstallerSuccessCodes: [42]
    ExpectedReturnCodes:
      - InstallerReturnCode: 5
        ReturnResponse: cancelledByUser
"#,
    );

    assert_eq!(manifest.installers[0].installer_success_codes, vec![1, -2147024891]);
    assert_eq!(
        manifest.installers[0].expected_return_codes,
        vec![ExpectedReturnCode {
            installer_return_code: 1641,
            return_response: "rebootRequiredToFinish".to_owned(),
        }]
    );
    assert_eq!(manifest.installers[1].installer_success_codes, vec![42]);
    assert_eq!(manifest.installers[1].expected_return_codes[0].installer_return_code, 5);
}

#[test]
fn rest_manifest_parses_success_codes_with_root_inheritance() {
    let payload = br#"{
      "Data": {
        "PackageIdentifier": "Contoso.Tool",
        "Versions": [
          {
            "PackageVersion": "1.2.3",
            "DefaultLocale": { "PackageLocale": "en-US", "Publisher": "Contoso", "PackageName": "Contoso Tool" },
            "InstallerSuccessCodes": [7],
            "Installers": [
              { "Architecture": "x64", "InstallerType": "exe", "InstallerUrl": "https://example.test/a.exe", "InstallerSha256": "AA" },
              {
                "Architecture": "x86", "InstallerType": "exe", "InstallerUrl": "https://example.test/b.exe", "InstallerSha256": "BB",
                "InstallerSuccessCodes": [8],
                "ExpectedReturnCodes": [{ "InstallerReturnCode": 3010, "ReturnResponse": "rebootRequiredToFinish" }]
              }
            ]
          }
        ]
      }
    }"#;

    let (manifest, _) = parse_rest_manifest(payload, "Contoso.Tool", "1.2.3", "").expect("parse");
    assert_eq!(manifest.installers[0].installer_success_codes, vec![7]);
    assert!(manifest.installers[0].expected_return_codes.is_empty());
    assert_eq!(manifest.installers[1].installer_success_codes, vec![8]);
    assert_eq!(
        manifest.installers[1].expected_return_codes[0].installer_return_code,
        3010
    );
}

#[test]
fn classify_installer_exit_code_honors_success_codes_and_reboot_codes() {
    let success = InstallerExitOutcome {
        success: true,
        reboot_required: false,
    };
    let reboot = InstallerExitOutcome {
        success: true,
        reboot_required: true,
    };
    let failure = InstallerExitOutcome {
        success: false,
        reboot_required: false,
    };

    let plain = Installer::default();
    assert_eq!(classify_installer_exit_code(&plain, "exe", 0), success);
    assert_eq!(classify_installer_exit_code(&plain, "exe", 1), failure);

    let with_codes = Installer {
        installer_success_codes: vec![1, 2_147_942_405],
        ..Installer::default()
    };
    assert_eq!(classify_installer_exit_code(&with_codes, "exe", 1), success);
    // Codes above i32::MAX in the manifest match the signed HRESULT exit code.
    assert_eq!(
        classify_installer_exit_code(&with_codes, "exe", 2_147_942_405u32.cast_signed()),
        success
    );

    // 3010 is a reboot-required success for MSI-family installers only.
    for installer_type in ["msi", "wix", "burn"] {
        assert_eq!(classify_installer_exit_code(&plain, installer_type, 3010), reboot);
    }
    assert_eq!(classify_installer_exit_code(&plain, "exe", 3010), failure);
    // WinGet treats 1641 (reboot initiated) as a failure unless the manifest says otherwise.
    assert_eq!(classify_installer_exit_code(&plain, "msi", 1641), failure);

    let with_expected = Installer {
        expected_return_codes: vec![
            ExpectedReturnCode {
                installer_return_code: 1641,
                return_response: "RebootRequiredToFinish".to_owned(),
            },
            ExpectedReturnCode {
                installer_return_code: 3010,
                return_response: "rebootRequiredForInstall".to_owned(),
            },
        ],
        ..Installer::default()
    };
    assert_eq!(classify_installer_exit_code(&with_expected, "exe", 1641), reboot);
    // A manifest entry overrides the installer type's default known code.
    assert_eq!(classify_installer_exit_code(&with_expected, "msi", 3010), failure);
}

// ── D2: ARP local id resolves to the exact hive/view/subkey ──

#[test]
fn arp_uninstall_key_candidates_use_exact_hive_view_and_subkey() {
    let uninstall = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    assert_eq!(
        arp_uninstall_key_candidates(r"ARP\Machine\X64\Git_is1"),
        Some(vec![ArpUninstallKeyCandidate {
            machine: true,
            wow64_32: false,
            key_path: format!(r"{uninstall}\Git_is1"),
        }])
    );
    assert_eq!(
        arp_uninstall_key_candidates(r"ARP\Machine\X86\Git_is1"),
        Some(vec![ArpUninstallKeyCandidate {
            machine: true,
            wow64_32: true,
            key_path: format!(r"{uninstall}\Git_is1"),
        }])
    );
    assert_eq!(
        arp_uninstall_key_candidates(r"arp\user\x64\{GUID}"),
        Some(vec![ArpUninstallKeyCandidate {
            machine: false,
            wow64_32: false,
            key_path: format!(r"{uninstall}\{{GUID}}"),
        }])
    );
    let user_x86 = arp_uninstall_key_candidates(r"ARP\User\X86\Tool").expect("user x86");
    assert!(user_x86.iter().all(|candidate| !candidate.machine));
    assert_eq!(
        user_x86[0].key_path,
        r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool"
    );

    for invalid in [
        r"MSIX\Contoso.App_1.0.0.0_x64__abc",
        r"ARP\Machine\X64\",
        r"ARP\Machine\X64",
        r"ARP\Other\X64\Tool",
        r"ARP\Machine\Arm\Tool",
        r"ARP\Machine\X64\a\b",
    ] {
        assert_eq!(arp_uninstall_key_candidates(invalid), None, "{invalid}");
    }
}

// ── D3: installer file names from URLs / --rename ──

#[test]
fn installer_download_file_name_rejects_unsafe_url_names_and_fixes_extension() {
    let manifest = simple_manifest();
    let name = |url: &str, installer_type: &str| {
        installer_download_file_name(url, None, installer_type, &manifest).expect("file name")
    };

    assert_eq!(name("https://example.test/dl/setup.exe?x=1#frag", "exe"), "setup.exe");
    assert_eq!(name("https://example.test/dl/My%20Setup.EXE", "inno"), "My Setup.EXE");
    assert_eq!(name("https://example.test/dl/tool.msi", "wix"), "tool.msi");
    assert_eq!(name("https://example.test/dl/app.msixbundle", "msix"), "app.msixbundle");
    // Wrong or missing extension gets the installer type's extension.
    assert_eq!(name("https://example.test/dl/payload.bin", "msi"), "payload.bin.msi");
    assert_eq!(
        name("https://example.test/download.php?id=1", "exe"),
        "download.php.exe"
    );
    assert_eq!(name("https://example.test/rg", "portable"), "rg.exe");
    assert_eq!(name("https://example.test/archive", "zip"), "archive.zip");
    // Unknown installer types keep the URL name.
    assert_eq!(name("https://example.test/font.ttf", "font"), "font.ttf");

    // Empty, traversal, absolute and separator-bearing names fall back to `<id>.<version>`.
    for url in [
        "https://example.test/?id=1",
        "https://example.test/dl/",
        "https://example.test/dl/..",
        "https://example.test/dl/%2E%2E",
        "https://example.test/dl/..%5C..%5Cevil.exe",
        "https://example.test/dl/C:%5CWindows%5Cevil.exe",
        "https://example.test/dl/a%2Fb.exe",
        "https://example.test/dl/evil%00.exe",
        "https://example.test/dl/CON.exe",
        "https://example.test/dl/name.",
    ] {
        assert_eq!(name(url, "exe"), "Contoso.Tool.1.2.3.exe", "{url}");
    }
}

#[test]
fn installer_download_file_name_validates_rename() {
    let manifest = simple_manifest();
    assert_eq!(
        installer_download_file_name("https://example.test/setup.exe", Some("custom.exe"), "exe", &manifest)
            .expect("rename"),
        "custom.exe"
    );
    for invalid in [
        "",
        "..",
        r"..\evil.exe",
        "../evil.exe",
        r"C:\evil.exe",
        "a:b.exe",
        "NUL",
    ] {
        assert!(
            installer_download_file_name("https://example.test/setup.exe", Some(invalid), "exe", &manifest).is_err(),
            "{invalid}"
        );
    }
}

// ── D4: cmd.exe arguments for ARP uninstall strings ──

#[test]
fn cmd_uninstall_arguments_wrap_line_for_cmd_s_switch() {
    assert_eq!(
        cmd_uninstall_arguments(r#""C:\Program Files (x86)\Tool\unins000.exe" /SILENT"#),
        r#"/D /S /C ""C:\Program Files (x86)\Tool\unins000.exe" /SILENT""#
    );
}

// ── D6: --all-versions keeps the first failure ──

#[test]
fn uninstall_each_match_reports_first_failure_for_all_versions() {
    let matches = vec![
        list_match("A", r"ARP\Machine\X64\A1"),
        list_match("A", r"ARP\Machine\X64\A2"),
        list_match("A", r"ARP\Machine\X64\A3"),
    ];

    let codes = [1603, 0, 0];
    let mut calls = 0;
    let exit_code = uninstall_each_match(&matches, true, |_| {
        calls += 1;
        Ok(codes[calls - 1])
    })
    .expect("uninstall");
    assert_eq!(exit_code, 1603);
    assert_eq!(calls, 3);

    let codes = [0, 5, 7];
    let mut calls = 0;
    let exit_code = uninstall_each_match(&matches, true, |_| {
        calls += 1;
        Ok(codes[calls - 1])
    })
    .expect("uninstall");
    assert_eq!(exit_code, 5);

    let mut calls = 0;
    let exit_code = uninstall_each_match(&matches, false, |_| {
        calls += 1;
        Ok(9)
    })
    .expect("uninstall");
    assert_eq!((exit_code, calls), (9, 1));
}

// ── D7: --uninstall-previous targets the installed package ──

#[test]
fn uninstall_previous_request_targets_installed_package_without_version() {
    let previous = list_match("Contoso.Tool", r"ARP\Machine\X64\Contoso.Tool");
    let request = create_uninstall_previous_request(&previous);
    assert_eq!(request.query.id.as_deref(), Some("Contoso.Tool"));
    assert!(request.query.exact);
    assert_eq!(request.query.version, None);
    assert_eq!(request.query.query, None);
    assert_eq!(request.product_code, None);
    assert_eq!(request.mode, InstallerMode::Silent);
    assert!(!request.preserve && !request.purge);

    ensure_previous_version_uninstalled("Contoso.Tool", 0).expect("success");
    let error = ensure_previous_version_uninstalled("Contoso.Tool", 1603).expect_err("failure");
    assert!(error.to_string().contains("1603"));
}

// ── D8: NSIS /D= stays unquoted and last ──

#[test]
fn nsis_install_location_is_last_and_unquoted() {
    let installer = Installer {
        installer_type: Some("nullsoft".to_owned()),
        ..Installer::default()
    };
    let mut request = InstallRequest::new(PackageQuery::default());
    request.mode = InstallerMode::Silent;
    request.custom = Some("/NCRC".to_owned());
    request.install_location = Some(r"C:\Program Files\My  Tool".to_owned());
    let args = installer_command_arguments(
        "nullsoft",
        &request,
        &simple_manifest(),
        Path::new("setup.exe"),
        &installer,
    );
    assert_eq!(args, vec!["/S", "/NCRC", r"/D=C:\Program Files\My  Tool"]);
    assert_eq!(join_windows_arguments(&args), r"/S /NCRC /D=C:\Program Files\My  Tool");

    // A manifest template that quotes the token is still passed unquoted.
    let quoted = Installer {
        installer_type: Some("exe".to_owned()),
        switches: InstallerSwitches {
            install_location: Some("/D=\"<INSTALLPATH>\"".to_owned()),
            ..InstallerSwitches::default()
        },
        ..Installer::default()
    };
    request.custom = None;
    let args = installer_command_arguments("exe", &request, &simple_manifest(), Path::new("setup.exe"), &quoted);
    assert_eq!(args.last().map(String::as_str), Some(r"/D=C:\Program Files\My  Tool"));
    // A generic exe gets no guessed silent switch (wave 7, R7-15), so the location is the only argument.
    assert_eq!(join_windows_arguments(&args), r"/D=C:\Program Files\My  Tool");
}

// ── D12: missing InstallerSha256 ──

#[test]
fn missing_installer_hash_requires_override_except_for_msstore() {
    let exe = Installer {
        installer_type: Some("exe".to_owned()),
        ..Installer::default()
    };
    let error = installer_expected_hash(&exe, false).expect_err("missing hash");
    assert!(error.to_string().contains("InstallerSha256"));
    assert_eq!(installer_expected_hash(&exe, true).expect("override"), None);

    let blank = Installer {
        sha256: Some("  ".to_owned()),
        ..exe.clone()
    };
    assert!(installer_expected_hash(&blank, false).is_err());

    let hashed = Installer {
        sha256: Some("ABC".to_owned()),
        ..exe
    };
    assert_eq!(installer_expected_hash(&hashed, false).expect("hash"), Some("ABC"));

    let msstore = Installer {
        installer_type: Some("MSStore".to_owned()),
        ..Installer::default()
    };
    assert_eq!(installer_expected_hash(&msstore, false).expect("msstore"), None);

    assert!(verify_installer_hash_hex(None, "ABC", false).is_err());
    verify_installer_hash_hex(None, "ABC", true).expect("override");
    verify_installer_hash_hex(Some("abc"), "ABC", false).expect("case-insensitive match");
}

// ── D14: <LOGPATH>/<INSTALLPATH> templates ──

#[test]
fn switch_templates_replace_tokens_case_insensitively_and_drop_only_unfilled_tokens() {
    assert_eq!(
        populate_switch_template("/log \"<logpath>\"", Some(r"C:\l.log"), None).as_deref(),
        Some(r#"/log "C:\l.log""#)
    );
    assert_eq!(
        populate_switch_template("INSTALLDIR=<InstallPath>", None, Some(r"D:\x")).as_deref(),
        Some(r"INSTALLDIR=D:\x")
    );
    // Token without a value: dropped.
    assert_eq!(populate_switch_template("/log <LOGPATH>", None, Some(r"D:\x")), None);
    assert_eq!(populate_switch_template("/log <LOGPATH>", Some("  "), None), None);
    // Token-less switch: kept even without values.
    assert_eq!(
        populate_switch_template("/VERBOSELOG", None, None).as_deref(),
        Some("/VERBOSELOG")
    );

    let installer = Installer {
        installer_type: Some("exe".to_owned()),
        switches: InstallerSwitches {
            silent: Some("/q".to_owned()),
            log: Some("/VERBOSELOG".to_owned()),
            install_location: Some("/DIR=<installpath>".to_owned()),
            ..InstallerSwitches::default()
        },
        ..Installer::default()
    };
    let mut request = InstallRequest::new(PackageQuery::default());
    request.mode = InstallerMode::Silent;
    let args = installer_command_arguments("exe", &request, &simple_manifest(), Path::new("setup.exe"), &installer);
    assert_eq!(args, vec!["/q", "/VERBOSELOG"]);

    request.install_location = Some(r"D:\Apps".to_owned());
    let args = installer_command_arguments("exe", &request, &simple_manifest(), Path::new("setup.exe"), &installer);
    assert_eq!(args, vec!["/q", "/VERBOSELOG", r"/DIR=D:\Apps"]);
}

// ── D9: portable uninstall with --preserve still unregisters ──

#[cfg(windows)]
#[test]
fn portable_uninstall_preserve_keeps_files_but_removes_arp_entry() {
    use std::time::{SystemTime, UNIX_EPOCH};

    let unique = format!(
        "PingetWave1Preserve_{}_{}",
        std::process::id(),
        SystemTime::now().duration_since(UNIX_EPOCH).expect("time").as_nanos()
    );
    let install_dir = std::env::temp_dir().join(&unique);
    fs::create_dir_all(&install_dir).expect("create install dir");
    let payload = install_dir.join("tool.exe");
    fs::write(&payload, b"payload").expect("write payload");

    let key_path = format!(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{unique}");
    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let (key, _) = hkcu.create_subkey(&key_path).expect("create ARP key");
    key.set_value("DisplayName", &unique).expect("display name");
    key.set_value("WinGetInstallerType", &"portable".to_owned())
        .expect("installer type");
    key.set_value("InstallLocation", &install_dir.to_string_lossy().to_string())
        .expect("install location");
    drop(key);

    let mut installed = list_match(&unique, &format!(r"ARP\User\X64\{unique}"));
    installed.installer_category = Some("portable".to_owned());
    installed.install_location = Some(install_dir.to_string_lossy().to_string());
    installed.scope = Some("user".to_owned());

    let mut request = UninstallRequest::new(PackageQuery::default());
    request.preserve = true;
    let mut warnings = Vec::new();
    let result = uninstall_package(&installed, &request, &mut warnings);

    let key_still_exists = hkcu.open_subkey(&key_path).is_ok();
    if key_still_exists {
        let _ = hkcu.delete_subkey_all(&key_path);
    }
    let payload_kept = payload.exists();
    let _ = fs::remove_dir_all(&install_dir);

    assert_eq!(result.expect("uninstall"), 0);
    assert!(payload_kept, "--preserve must keep the package files");
    assert!(!key_still_exists, "--preserve must still remove the ARP entry");
}
