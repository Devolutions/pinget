//! Wave 2 (package sources) CLI regression tests.

use super::*;

fn update_result(name: &str, success: bool, detail: &str) -> SourceUpdateResult {
    SourceUpdateResult {
        name: name.to_owned(),
        kind: SourceKind::Rest,
        detail: detail.to_owned(),
        success,
    }
}

#[test]
fn source_update_succeeds_when_every_source_updated() {
    let results = [update_result("a", true, "ok"), update_result("b", true, "ok")];
    assert!(source_update_outcome(&results).is_ok());
}

#[test]
fn source_update_fails_overall_when_any_source_failed() {
    let results = [
        update_result("good", true, "refreshed information cache"),
        update_result("bad", false, "REST information request failed"),
    ];

    let error = source_update_outcome(&results).expect_err("a failed source fails the command");

    assert_eq!(error.to_string(), "1 source(s) failed to update: bad");
    assert_eq!(
        format_source_update(&results[1]),
        "bad [rest]: failed: REST information request failed"
    );
    assert_eq!(
        format_source_update(&results[0]),
        "good [rest]: refreshed information cache"
    );
}
