//! Regression tests for the COM surface defect fixes (wave 11).

use super::*;

// ── R11-5: an omitted source type is Microsoft.PreIndexed.Package, as in WinGet ──

#[test]
fn parse_source_kind_defaults_to_preindexed() {
    assert!(matches!(parse_source_kind(None), Some(SourceKind::PreIndexed)));
    assert!(matches!(
        parse_source_kind(Some("Microsoft.Rest")),
        Some(SourceKind::Rest)
    ));
    assert!(matches!(parse_source_kind(Some("rest")), Some(SourceKind::Rest)));
    assert!(matches!(
        parse_source_kind(Some("Microsoft.PreIndexed.Package")),
        Some(SourceKind::PreIndexed)
    ));
    assert!(parse_source_kind(Some("bogus")).is_none());
}

// ── R11-7: the install JSON returned to Windows PowerShell carries reboot_required ──

#[test]
fn install_result_json_carries_reboot_required() {
    let result = pinget_core::InstallResult {
        package_id: "Contoso.Tool".to_owned(),
        success: true,
        reboot_required: true,
        ..pinget_core::InstallResult::default()
    };

    let json = serde_json::to_value(&result).expect("serialize");
    assert_eq!(json["reboot_required"], JsonValue::Bool(true));
}
