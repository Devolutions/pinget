// The observable assertion needs a real `winget` to mirror from, so the whole file is
// Windows-only. Gating at the crate level rather than per-test keeps the helpers and
// imports from being dead code on other platforms. Parsing and precedence are covered
// by pinget-core unit tests on every platform.
#![cfg(windows)]

//! Covers the `PINGET_SOURCE_MODE` wiring through a real process.
//!
//! This lives here rather than as a unit test because asserting on an environment
//! variable requires setting one, and `std::env::set_var` may not race with any
//! concurrent read of the environment. The core test binary is multi-threaded, so a
//! child process is the only way to satisfy that precondition.

use std::path::{Path, PathBuf};
use std::process::Command;

fn temp_app_root(name: &str) -> PathBuf {
    let root = std::env::temp_dir()
        .join("pinget-cli-tests")
        .join(format!("{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).expect("create temp app root");
    root
}

fn run_source_list(app_root: &Path, source_mode: Option<&str>) {
    let mut command = Command::new(env!("CARGO_BIN_EXE_pinget"));
    command.args(["source", "list"]).env("PINGET_APPROOT", app_root);

    match source_mode {
        Some(value) => command.env("PINGET_SOURCE_MODE", value),
        None => command.env_remove("PINGET_SOURCE_MODE"),
    };

    let output = command.output().expect("run pinget source list");
    assert!(
        output.status.success(),
        "pinget source list failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn source_mode_environment_variable_overrides_the_custom_app_root_default() {
    let app_root = temp_app_root("with-source-mode");
    run_source_list(&app_root, Some("auto"));
    assert!(
        app_root.join("system-sources.json").exists(),
        "expected PINGET_SOURCE_MODE=auto to mirror the system WinGet sources"
    );
}

#[test]
fn a_custom_app_root_alone_still_selects_the_private_store() {
    let app_root = temp_app_root("without-source-mode");
    run_source_list(&app_root, None);
    assert!(
        !app_root.join("system-sources.json").exists(),
        "expected a custom app root alone to keep using the private source store"
    );
}
