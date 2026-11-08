//! Wave 10 (pins) CLI regression tests.

use super::*;

fn stored_pin(package_id: &str, source_id: &str) -> PinRecord {
    PinRecord {
        package_id: package_id.to_owned(),
        version: "*".to_owned(),
        source_id: source_id.to_owned(),
        pin_type: PinType::Pinning,
    }
}

fn catalog_match(id: &str, source_name: &str) -> SearchMatch {
    SearchMatch {
        source_name: source_name.to_owned(),
        source_kind: SourceKind::PreIndexed,
        id: id.to_owned(),
        name: id.to_owned(),
        moniker: None,
        version: Some("1.0.0".to_owned()),
        channel: None,
        match_criteria: None,
    }
}

// ── R10-13: pin filters compare package id and source case-insensitively ──

#[test]
fn pin_filter_matches_catalog_hits_ignoring_case() {
    let pins = vec![
        stored_pin("contoso.tool", "WinGet"),
        stored_pin("Other.Tool", "winget"),
        stored_pin("CONTOSO.TOOL", ""),
    ];
    let matches = vec![catalog_match("Contoso.Tool", "winget")];

    let kept = retain_pins_for_matches(pins, &matches);

    let kept_keys = kept
        .iter()
        .map(|pin| format!("{}|{}", pin.package_id, pin.source_id))
        .collect::<Vec<_>>();
    assert_eq!(kept_keys, vec!["contoso.tool|WinGet", "CONTOSO.TOOL|"]);
}
