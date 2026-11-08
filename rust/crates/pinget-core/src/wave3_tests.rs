use super::*;

fn pinned_item(available_version: &str) -> ListMatch {
    ListMatch {
        name: "Contoso Tool".to_owned(),
        id: "Contoso.Tool".to_owned(),
        local_id: r"ARP\User\X64\Contoso.Tool".to_owned(),
        installed_version: "1.0.0".to_owned(),
        available_version: Some(available_version.to_owned()),
        source_name: Some("winget".to_owned()),
        publisher: None,
        scope: None,
        installer_category: None,
        install_location: None,
        package_family_names: Vec::new(),
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
    }
}

fn pin(pin_type: PinType, version: &str, source_id: &str) -> PinRecord {
    PinRecord {
        package_id: "Contoso.Tool".to_owned(),
        version: version.to_owned(),
        source_id: source_id.to_owned(),
        pin_type,
    }
}

// I1: a Pinning pin blocks implicit upgrades regardless of its recorded version.
#[test]
fn pinning_pin_blocks_implicit_upgrade_regardless_of_version() {
    let item = pinned_item("2.0.0");
    assert!(is_upgrade_blocked_by_pin(
        &item,
        &[pin(PinType::Pinning, "1.0.0", "winget")]
    ));
    assert!(is_upgrade_blocked_by_pin(
        &item,
        &[pin(PinType::Pinning, "2.0.0", "winget")]
    ));
    assert!(is_upgrade_blocked_by_pin(&item, &[pin(PinType::Pinning, "*", "")]));
}

#[test]
fn include_pinned_bypasses_only_pinning_pins() {
    assert!(!pin_blocks_upgrade(
        &pin(PinType::Pinning, "1.0.0", "winget"),
        "2.0.0",
        true
    ));
    assert!(pin_blocks_upgrade(
        &pin(PinType::Blocking, "*", "winget"),
        "2.0.0",
        true
    ));
    assert!(pin_blocks_upgrade(
        &pin(PinType::Gating, "1.*", "winget"),
        "2.0.0",
        true
    ));
    assert!(!pin_blocks_upgrade(
        &pin(PinType::Gating, "2.*", "winget"),
        "2.0.0",
        true
    ));
}

// I3: gating patterns compare version parts, mirroring WinGet's GatedVersion.
#[test]
fn gating_pattern_compares_version_parts() {
    assert!(version_matches_pin_pattern("1.2", "1.2.*"));
    assert!(version_matches_pin_pattern("1.2.0", "1.2.*"));
    assert!(version_matches_pin_pattern("1.2.5", "1.2.*"));
    assert!(version_matches_pin_pattern("1.2.5.7", "1.2.*"));
    assert!(version_matches_pin_pattern("1", "1.0.*"));
    assert!(!version_matches_pin_pattern("1.20", "1.2.*"));
    assert!(!version_matches_pin_pattern("1.3.0", "1.2.*"));
    assert!(version_matches_pin_pattern("1.2.0", "1.2"));
    assert!(version_matches_pin_pattern("01.2", "1.2"));
    assert!(!version_matches_pin_pattern("1.2.1", "1.2"));
    assert!(version_matches_pin_pattern("9.9", "*"));
}

#[test]
fn gating_pin_blocks_implicit_upgrade_outside_range() {
    let pins = [pin(PinType::Gating, "1.2.*", "winget")];
    assert!(is_upgrade_blocked_by_pin(&pinned_item("1.20.0"), &pins));
    assert!(!is_upgrade_blocked_by_pin(&pinned_item("1.2.7"), &pins));
}

// I8: source-less pins apply even when the source name differs in case.
#[test]
fn applicable_pin_matches_source_case_insensitively_and_sourceless() {
    let item = pinned_item("2.0.0");
    assert!(find_applicable_pin(&item, &[pin(PinType::Blocking, "*", "WinGet")]).is_some());
    assert!(find_applicable_pin(&item, &[pin(PinType::Blocking, "*", "")]).is_some());
    assert!(find_applicable_pin(&item, &[pin(PinType::Blocking, "*", "msstore")]).is_none());
}

#[test]
fn upgrade_with_source_filter_applies_sourceless_pins() {
    let app_root = std::env::temp_dir().join(format!("pinget-wave3-pins-{}", std::process::id()));
    let _ = fs::remove_dir_all(&app_root);
    let result = (|| -> Result<()> {
        let repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
        repository.add_pin("Contoso.Tool", "*", "", PinType::Blocking)?;
        // `list` loads every pin for upgrade, so the source-less pin is visible
        // even when the user asked for a specific source.
        let pins = repository.list_pins(None)?;
        assert!(is_upgrade_blocked_by_pin(&pinned_item("2.0.0"), &pins));
        Ok(())
    })();
    let _ = fs::remove_dir_all(&app_root);
    result.expect("source-less pin applies");
}

// I4: settings JSON with BOM, comments and trailing commas (WinGet's default settings.json shape).
#[test]
fn lenient_settings_json_accepts_comments_trailing_commas_and_bom() {
    let text = "\u{feff}{\n    \"$schema\": \"https://aka.ms/winget-settings.schema.json\",\n\n    // For documentation on these settings, see: https://aka.ms/winget-settings\n    // \"source\": {\n    //    \"autoUpdateIntervalInMinutes\": 5\n    // },\n    \"visual\": { \"progressBar\": \"rainbow\", },\n    /* block\n       comment */\n    \"url\": \"http://example.com/a//b/*c*/\",\n    \"quote\": \"say \\\"hi\\\", // not a comment\",\n    \"list\": [1, 2, ],\n}\n";
    let value = parse_lenient_json(text).expect("lenient parse");
    assert_eq!(value["visual"]["progressBar"], "rainbow");
    assert_eq!(value["url"], "http://example.com/a//b/*c*/");
    assert_eq!(value["quote"], "say \"hi\", // not a comment");
    assert_eq!(value["list"], serde_json::json!([1, 2]));
    assert!(value.get("source").is_none());
}

#[test]
fn load_json_object_reads_commented_settings_file() {
    let dir = std::env::temp_dir().join(format!("pinget-wave3-settings-{}", std::process::id()));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).expect("dir");
    let path = dir.join("settings.json");
    let mut bytes = vec![0xEF, 0xBB, 0xBF];
    bytes.extend_from_slice(b"{\n  // comment\n  \"telemetry\": { \"disable\": true, },\n}\n");
    fs::write(&path, bytes).expect("write");
    let loaded = load_json_object(&path);
    let _ = fs::remove_dir_all(&dir);
    let loaded = loaded.expect("load");
    assert_eq!(loaded["telemetry"]["disable"], true);
}

// I5: `--source` alone is not a filter, so bulk-upgrade hiding still applies.
#[test]
fn source_only_upgrade_query_is_not_a_filter() {
    let query = ListQuery {
        source: Some("winget".to_owned()),
        upgrade_only: true,
        ..ListQuery::default()
    };
    assert!(!list_query_needs_available_lookup(&query));
}

// I13: `--exact` disables loose correlation; tag-only queries never use it.
#[test]
fn loose_list_correlation_respects_exact_and_needs_text_filter() {
    let query = ListQuery {
        query: Some("git".to_owned()),
        ..ListQuery::default()
    };
    assert!(allow_loose_list_correlation(&query));
    assert!(!allow_loose_list_correlation(&ListQuery { exact: true, ..query }));
    assert!(!allow_loose_list_correlation(&ListQuery {
        tag: Some("git".to_owned()),
        ..ListQuery::default()
    }));
}

// I12: MSIX family name is Name_PublisherId even when a resource id is present.
#[cfg(windows)]
#[test]
fn msix_family_name_excludes_resource_id() {
    let parsed = parse_msix_package_full_name("Foo_1.0.0.0_neutral_~_8wekyb3d8bbwe").expect("parsed");
    assert_eq!(parsed.family_name, "Foo_8wekyb3d8bbwe");
    assert_eq!(parsed.resource_id, "~");
    assert_eq!(parsed.version, "1.0.0.0");

    let parsed = parse_msix_package_full_name("Foo.Bar_2.1.0.0_x64_en-us_abcdefghijklm").expect("parsed");
    assert_eq!(parsed.family_name, "Foo.Bar_abcdefghijklm");
}

// I6: any nonzero SystemComponent hides the ARP entry, as in WinGet.
#[cfg(windows)]
#[test]
fn arp_flag_is_set_for_any_nonzero_value() {
    assert!(is_arp_flag_set(Some(1)));
    assert!(is_arp_flag_set(Some(2)));
    assert!(!is_arp_flag_set(Some(0)));
    assert!(!is_arp_flag_set(None));
}
