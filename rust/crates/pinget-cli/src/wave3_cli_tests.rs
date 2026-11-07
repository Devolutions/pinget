use super::*;

fn pin(pin_type: PinType, version: &str) -> PinRecord {
    PinRecord {
        package_id: "Contoso.Tool".to_owned(),
        version: version.to_owned(),
        source_id: "winget".to_owned(),
        pin_type,
    }
}

#[test]
fn pin_add_without_version_is_pinning_with_version_is_gating() {
    assert_eq!(pin_type_for_add(false, None), PinType::Pinning);
    assert_eq!(pin_type_for_add(false, Some("  ")), PinType::Pinning);
    assert_eq!(pin_type_for_add(false, Some("1.2.*")), PinType::Gating);
    assert_eq!(pin_type_for_add(true, Some("1.2.*")), PinType::Blocking);
    assert_eq!(pin_type_for_add(true, None), PinType::Blocking);
}

#[test]
fn pin_add_records_installed_version_by_default() {
    assert_eq!(pin_version_for_add(None, false, Some("1.4.0".to_owned())), "1.4.0");
    assert_eq!(pin_version_for_add(None, false, Some("Unknown".to_owned())), "*");
    assert_eq!(pin_version_for_add(None, false, None), "*");
    assert_eq!(pin_version_for_add(None, true, Some("1.4.0".to_owned())), "*");
    assert_eq!(
        pin_version_for_add(Some("1.2.*"), false, Some("1.4.0".to_owned())),
        "1.2.*"
    );
}

#[test]
fn explicit_upgrade_honors_blocking_and_gating_pins() {
    // Explicit upgrades include Pinning pins.
    assert!(upgrade_pin_block_message(&pin(PinType::Pinning, "1.0.0"), "2.0.0", true).is_none());
    assert!(upgrade_pin_block_message(&pin(PinType::Pinning, "1.0.0"), "2.0.0", false).is_some());

    assert!(upgrade_pin_block_message(&pin(PinType::Blocking, "*"), "2.0.0", true).is_some());

    assert!(upgrade_pin_block_message(&pin(PinType::Gating, "1.2.*"), "1.2.9", true).is_none());
    let message = upgrade_pin_block_message(&pin(PinType::Gating, "1.2.*"), "1.3.0", true).expect("gated");
    assert!(message.contains("1.3.0"));
    assert!(message.contains("1.2.*"));
}
