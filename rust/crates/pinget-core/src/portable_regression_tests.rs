#![cfg(windows)]

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use winreg::enums::{REG_EXPAND_SZ, REG_SZ, RegType};
use winreg::reg_value::RegValue;
use winreg::types::{FromRegValue, ToRegValue};

use super::{
    ExistingPortableEntry, InstallRequest, Installer, ListMatch, NestedInstallerFile, PackageQuery, PortableScope,
    append_portable_links_path, apply_install_scope, cleanup_previous_portable_install, determine_portable_alias,
    installed_portable_scope, installer_matches_requested, is_owned_portable_directory, portable_link_target_boundary,
    prepare_portable_path_update, remove_owned_portable_link, remove_portable_links_in_directory,
    remove_tracked_portable_files, resolve_portable_scope,
};

#[test]
fn resolve_portable_scope_explicit_request_overrides_installed_and_manifest_scope() {
    assert_eq!(
        resolve_portable_scope(Some("  MaChInE  "), Some("user"), Some("user")).expect("valid requested machine scope"),
        PortableScope::Machine
    );
    assert_eq!(
        resolve_portable_scope(Some("UsEr"), Some("machine"), Some("machine")).expect("valid requested user scope"),
        PortableScope::User
    );
}

#[test]
fn resolve_portable_scope_uses_installed_scope_before_manifest_on_upgrade() {
    assert_eq!(
        resolve_portable_scope(None, Some("MaChInE"), Some("user")).expect("valid installed machine scope"),
        PortableScope::Machine
    );
    assert_eq!(
        resolve_portable_scope(Some(" \t"), Some("USER"), Some("machine")).expect("valid installed user scope"),
        PortableScope::User
    );
}

#[test]
fn resolve_portable_scope_uses_manifest_when_no_request_or_installed_scope() {
    assert_eq!(
        resolve_portable_scope(None, None, Some("MACHINE")).expect("valid manifest machine scope"),
        PortableScope::Machine
    );
    assert_eq!(
        resolve_portable_scope(Some(" "), Some("\t"), Some("user")).expect("valid manifest user scope"),
        PortableScope::User
    );
}

#[test]
fn resolve_portable_scope_defaults_to_user_when_scopes_are_absent_or_blank() {
    assert_eq!(
        resolve_portable_scope(None, None, None).expect("default user scope"),
        PortableScope::User
    );
    assert_eq!(
        resolve_portable_scope(Some(""), Some(" \t"), Some(" ")).expect("blank scopes default to user"),
        PortableScope::User
    );
}

#[test]
fn resolve_portable_scope_rejects_invalid_selected_scope_at_each_precedence() {
    for (requested, installed, manifest) in [
        (Some("invalid-request"), None, None),
        (None, Some("invalid-installed"), None),
        (None, None, Some("invalid-manifest")),
    ] {
        let error = resolve_portable_scope(requested, installed, manifest).expect_err("invalid scope should fail");
        assert!(
            error.to_string().contains("Unsupported install scope"),
            "unexpected error for {requested:?}, {installed:?}, {manifest:?}: {error}"
        );
    }
}

#[test]
fn apply_install_scope_propagates_explicit_scope_to_installer_filter() {
    let request = InstallRequest::new(PackageQuery {
        id: Some("Example.Tool".to_owned()),
        install_scope: Some("  MACHINE  ".to_owned()),
        ..PackageQuery::default()
    });

    let effective = apply_install_scope(&request, Some("user")).expect("apply requested scope");
    assert_eq!(effective.query.install_scope.as_deref(), Some("machine"));
    assert_eq!(effective.query.id.as_deref(), Some("Example.Tool"));
    assert_eq!(matching_installer_scopes(&effective.query), ["machine"]);
}

#[test]
fn apply_install_scope_propagates_both_installed_scopes_on_upgrade() {
    for (installed_scope, expected) in [("MACHINE", "machine"), ("USER", "user")] {
        let request = InstallRequest::new(PackageQuery {
            id: Some("Example.Tool".to_owned()),
            install_scope: Some(" \t".to_owned()),
            ..PackageQuery::default()
        });

        let effective = apply_install_scope(&request, Some(installed_scope)).expect("apply installed scope");
        assert_eq!(effective.query.install_scope.as_deref(), Some(expected));
        assert_eq!(effective.query.id.as_deref(), Some("Example.Tool"));
        assert_eq!(matching_installer_scopes(&effective.query), [expected]);
    }
}

#[test]
fn apply_install_scope_keeps_absent_scope_for_manifest_fallback_and_rejects_invalid() {
    let request = InstallRequest::new(PackageQuery::default());
    let effective = apply_install_scope(&request, None).expect("unspecified scope remains available");
    assert!(
        effective.query.install_scope.is_none(),
        "scope should remain unspecified"
    );
    assert_eq!(
        resolve_portable_scope(effective.query.install_scope.as_deref(), None, Some("machine"))
            .expect("manifest scope is used"),
        PortableScope::Machine
    );

    let invalid = InstallRequest::new(PackageQuery {
        install_scope: Some("invalid".to_owned()),
        ..PackageQuery::default()
    });
    assert!(
        apply_install_scope(&invalid, Some("machine")).is_err(),
        "invalid explicit scope must not be hidden by the installed scope"
    );
    assert!(
        apply_install_scope(&request, Some("invalid")).is_err(),
        "invalid installed scope must not be silently treated as user"
    );
}

fn matching_installer_scopes(query: &PackageQuery) -> Vec<String> {
    ["user", "machine"]
        .into_iter()
        .map(|scope| Installer {
            installer_type: Some("portable".to_owned()),
            scope: Some(scope.to_owned()),
            ..Installer::default()
        })
        .filter(|installer| {
            installer_matches_requested(installer, None, query.install_scope.as_deref(), None, None, None, None)
        })
        .map(|installer| installer.scope.expect("test installer has a scope"))
        .collect()
}

#[test]
fn installed_portable_scope_uses_arp_identity_before_reported_scope() {
    let machine = installed_match(r"ARP\Machine\X64\Example.Tool", Some("user"));
    let user = installed_match(r"aRp\uSeR\X64\Example.Tool", Some("machine"));

    assert_eq!(
        installed_portable_scope(&machine).expect("machine ARP identity"),
        PortableScope::Machine
    );
    assert_eq!(
        installed_portable_scope(&user).expect("user ARP identity"),
        PortableScope::User
    );
}

#[test]
fn installed_portable_scope_falls_back_to_reported_or_default_scope() {
    let machine = installed_match(r"OTHER\Example.Tool", Some("MaChInE"));
    let default = installed_match(r"OTHER\Example.Tool", None);

    assert_eq!(
        installed_portable_scope(&machine).expect("reported machine scope"),
        PortableScope::Machine
    );
    assert_eq!(
        installed_portable_scope(&default).expect("default user scope"),
        PortableScope::User
    );
    assert!(
        installed_portable_scope(&installed_match(r"OTHER\Example.Tool", Some("invalid"))).is_err(),
        "invalid fallback scope should be rejected"
    );
}

fn installed_match(local_id: &str, scope: Option<&str>) -> ListMatch {
    ListMatch {
        name: "Example".to_owned(),
        id: "Example.Tool".to_owned(),
        local_id: local_id.to_owned(),
        installed_version: "1.0".to_owned(),
        available_version: None,
        source_name: None,
        publisher: None,
        scope: scope.map(str::to_owned),
        installer_category: Some("portable".to_owned()),
        install_location: None,
        package_family_names: Vec::new(),
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
    }
}

#[test]
fn is_owned_portable_directory_retains_ownership_for_same_normalized_location() {
    let temp = TemporaryDirectory::new();
    let target = temp.create_directory("owned");
    fs::create_dir(target.join("child")).expect("create child for normalized path");
    let equivalent = PathBuf::from(target.to_string_lossy().to_ascii_uppercase())
        .join("child")
        .join("..");

    assert!(
        is_owned_portable_directory(true, Some(&equivalent), &target).expect("compare equivalent locations"),
        "normalization and case changes must not discard ownership"
    );
}

#[test]
fn is_owned_portable_directory_does_not_transfer_ownership_to_different_or_unknown_location() {
    let temp = TemporaryDirectory::new();
    let original = temp.create_directory("owned");
    let custom = temp.create_directory("owned-extra");

    assert!(
        !is_owned_portable_directory(true, Some(&original), &custom).expect("compare different locations"),
        "a similarly prefixed custom path is not the previously owned directory"
    );
    assert!(
        !is_owned_portable_directory(true, None, &custom).expect("compare missing prior location"),
        "an unknown previous path cannot transfer ownership"
    );
}

#[test]
fn is_owned_portable_directory_does_not_claim_previously_unowned_location() {
    let temp = TemporaryDirectory::new();
    let target = temp.create_directory("custom");

    assert!(
        !is_owned_portable_directory(false, Some(&target), &target).expect("compare unowned location"),
        "an existing custom directory remains unowned"
    );
}

#[test]
fn append_portable_links_path_preserves_raw_references_for_user_and_machine_links() {
    let existing = r"%SystemRoot%\System32;%USERPROFILE%\bin";
    for links in [
        r"C:\Users\Example\AppData\Local\Microsoft\WinGet\Links",
        r"C:\Program Files\WinGet\Links",
    ] {
        let expected = format!("{existing};{links}");
        assert_eq!(
            append_portable_links_path(existing, links, expand_test_variables),
            Some(expected),
            "appending {links} must not expand the stored PATH references"
        );
    }
}

#[test]
fn append_portable_links_path_preserves_trailing_and_empty_segments() {
    let existing = r"%SystemRoot%\System32;;  C:\Tools  ;";
    let links = r"C:\Program Files\WinGet\Links";

    assert_eq!(
        append_portable_links_path(existing, links, expand_test_variables),
        Some(format!("{existing}{links}")),
        "a trailing delimiter is reused without altering empty entries"
    );
    assert_eq!(
        append_portable_links_path("", links, expand_test_variables),
        Some(links.to_owned()),
        "an empty PATH needs no leading delimiter"
    );
}

#[test]
fn append_portable_links_path_recognizes_expanded_quoted_entry_without_changing_raw_path() {
    let existing = r#"  "%ProgramFiles%\WinGet\Links\"  ;%USERPROFILE%\bin"#;
    let links = r"c:\program files\winget\links";
    let expanded_inputs = std::cell::RefCell::new(Vec::new());

    let result = append_portable_links_path(existing, links, |value| {
        expanded_inputs.borrow_mut().push(value.to_owned());
        expand_test_variables(value)
    });

    assert_eq!(result, None, "the expanded path already contains the links directory");
    assert!(
        expanded_inputs
            .borrow()
            .iter()
            .any(|value| value == r"%ProgramFiles%\WinGet\Links\"),
        "the expander must receive the original unexpanded component"
    );
}

#[test]
fn append_portable_links_path_recognizes_literal_duplicates_but_not_prefix_collisions() {
    let links = r"C:\Program Files\WinGet\Links";
    let unrelated = r"C:\Program Files\WinGet\Links-Extra";

    assert_eq!(
        append_portable_links_path(r"c:\program files\WINGET\LINKS", links, expand_test_variables),
        None,
        "case-insensitive literal duplicates must not be appended"
    );
    assert_eq!(
        append_portable_links_path(unrelated, links, expand_test_variables),
        Some(format!("{unrelated};{links}")),
        "a similarly prefixed directory is not the links directory"
    );
}

#[test]
fn append_portable_links_path_recognizes_expanded_entry_with_repeated_trailing_separators() {
    let existing = r"%ProgramFiles%\WinGet\Links\\";
    let links = r"C:\Program Files\WinGet\Links";

    assert_eq!(
        append_portable_links_path(existing, links, expand_test_variables),
        None,
        "multiple trailing separators do not change the links directory"
    );
}

#[test]
fn prepare_portable_path_update_preserves_expandable_kind_and_raw_variables_when_appending() {
    let existing = r"%SystemRoot%\System32;%USERPROFILE%\bin";
    let links = r"C:\Program Files\WinGet\Links";

    let update = prepare_portable_path_update(
        Some(raw_registry_string(existing, REG_EXPAND_SZ)),
        links,
        expand_test_variables,
    )
    .expect("prepare expandable PATH")
    .expect("expected an expandable PATH update");

    assert_eq!(
        String::from_reg_value(&update).expect("decode updated PATH"),
        format!("{existing};{links}")
    );
    assert_eq!(update.vtype, REG_EXPAND_SZ);
}

#[test]
fn prepare_portable_path_update_expands_only_expandable_values_for_duplicate_detection() {
    let existing = r"%ProgramFiles%\WinGet\Links";
    let links = r"C:\Program Files\WinGet\Links";
    let expanded_inputs = std::cell::RefCell::new(Vec::new());

    let expandable = prepare_portable_path_update(Some(raw_registry_string(existing, REG_EXPAND_SZ)), links, |value| {
        expanded_inputs.borrow_mut().push(value.to_owned());
        expand_test_variables(value)
    })
    .expect("prepare expandable PATH");
    let plain = prepare_portable_path_update(Some(raw_registry_string(existing, REG_SZ)), links, |_| {
        panic!("REG_SZ must not be expanded")
    })
    .expect("prepare plain PATH")
    .expect("expected a plain PATH update");

    assert!(
        expandable.is_none(),
        "the expanded entry already matches the links directory"
    );
    assert!(
        expanded_inputs.borrow().iter().any(|value| value == existing),
        "the expander receives the original PATH component"
    );
    assert_eq!(
        String::from_reg_value(&plain).expect("decode updated PATH"),
        format!("{existing};{links}")
    );
    assert_eq!(plain.vtype, REG_SZ);
}

#[test]
fn prepare_portable_path_update_detects_literal_plain_string_duplicate_without_expanding() {
    let links = r"C:\Program Files\WinGet\Links";
    let existing = raw_registry_string(r"c:\program files\WINGET\LINKS", REG_SZ);

    let update = prepare_portable_path_update(Some(existing), links, |_| panic!("REG_SZ must not be expanded"))
        .expect("prepare plain PATH");

    assert!(update.is_none(), "a literal case-insensitive duplicate needs no update");
}

#[test]
fn prepare_portable_path_update_creates_missing_path_as_expandable() {
    let links = r"C:\Program Files\WinGet\Links";

    let update = prepare_portable_path_update(None, links, expand_test_variables)
        .expect("prepare missing PATH")
        .expect("expected a new PATH value");

    assert_eq!(String::from_reg_value(&update).expect("decode new PATH"), links);
    assert_eq!(update.vtype, REG_EXPAND_SZ);
}

#[test]
fn prepare_portable_path_update_rejects_unsupported_value_type() {
    let links = r"C:\Program Files\WinGet\Links";
    let unsupported = 42u32.to_reg_value();

    assert!(
        prepare_portable_path_update(Some(unsupported), links, expand_test_variables).is_err(),
        "a DWORD cannot be treated as a PATH string"
    );
}

fn raw_registry_string(value: &str, value_type: RegType) -> RegValue {
    let mut raw = value.to_reg_value();
    raw.vtype = value_type;
    raw
}

#[test]
fn remove_tracked_portable_files_removes_only_tracked_files_and_empty_directories() {
    let temp = TemporaryDirectory::new();
    let root = temp.create_directory("shared");
    let tools = root.join("tools");
    let bin = tools.join("bin");
    fs::create_dir_all(&bin).expect("create tracked nested directory");
    let managed = bin.join("managed.exe");
    let unrelated = tools.join("notes.txt");
    fs::write(&managed, "managed payload").expect("write tracked file");
    fs::write(&unrelated, "user notes").expect("write unrelated file");

    remove_tracked_portable_files(
        &root,
        &[r"tools\bin\managed.exe".to_owned()],
        &[r"tools\bin".to_owned(), "tools".to_owned()],
    )
    .expect("remove tracked files from a shared directory");

    assert!(!managed.exists(), "the tracked file should be removed");
    assert!(!bin.exists(), "the empty tracked subdirectory should be removed");
    assert!(root.is_dir(), "the shared install root must remain");
    assert!(tools.is_dir(), "a nonempty directory containing user data must remain");
    assert_eq!(
        fs::read_to_string(&unrelated).expect("read unrelated file"),
        "user notes"
    );
}

#[test]
fn remove_tracked_portable_files_rejects_unsafe_file_paths_before_deleting_any_file() {
    for index in 0..4 {
        let temp = TemporaryDirectory::new();
        let root = temp.create_directory("shared");
        let outside = temp.create_directory("outside");
        let safe = root.join("safe.txt");
        let outside_file = outside.join("outside.txt");
        fs::write(&safe, "safe content").expect("write safe tracked file");
        fs::write(&outside_file, "outside content").expect("write outside sentinel");
        let invalid = match index {
            0 => r"..\outside\outside.txt".to_owned(),
            1 => outside_file.to_string_lossy().into_owned(),
            2 => r"nested\..\safe.txt".to_owned(),
            _ => safe.to_string_lossy().into_owned(),
        };

        let files = ["safe.txt".to_owned(), invalid];
        let result = remove_tracked_portable_files(&root, &files, &[]);

        assert!(
            result.is_err(),
            "unsafe tracked file path must be rejected: {}",
            files[1]
        );
        assert_eq!(
            fs::read_to_string(&safe).expect("safe file remains after validation failure"),
            "safe content"
        );
        assert_eq!(
            fs::read_to_string(&outside_file).expect("outside sentinel remains"),
            "outside content"
        );
    }
}

#[test]
fn remove_tracked_portable_files_rejects_unsafe_directory_paths_before_deleting_any_file() {
    for index in 0..3 {
        let temp = TemporaryDirectory::new();
        let root = temp.create_directory("shared");
        let outside = temp.create_directory("outside");
        let safe = root.join("safe.txt");
        let outside_file = outside.join("outside.txt");
        fs::write(&safe, "safe content").expect("write safe tracked file");
        fs::write(&outside_file, "outside content").expect("write outside sentinel");
        let invalid = match index {
            0 => r"..\outside".to_owned(),
            1 => outside.to_string_lossy().into_owned(),
            _ => root.to_string_lossy().into_owned(),
        };

        let directories = [invalid];
        let result = remove_tracked_portable_files(&root, &["safe.txt".to_owned()], &directories);

        assert!(
            result.is_err(),
            "unsafe tracked directory path must be rejected before file deletion: {}",
            directories[0]
        );
        assert_eq!(
            fs::read_to_string(&safe).expect("safe file remains after validation failure"),
            "safe content"
        );
        assert_eq!(
            fs::read_to_string(&outside_file).expect("outside sentinel remains"),
            "outside content"
        );
    }
}

#[test]
fn remove_owned_portable_link_rejects_regular_file_in_links_directory() {
    let temp = TemporaryDirectory::new();
    let links = temp.create_directory("machine-links");
    let install = temp.create_directory("package");
    let unrelated = links.join("unrelated.exe");
    fs::write(&unrelated, "user file").expect("write unrelated links entry");

    let result = remove_owned_portable_link(&unrelated, &links, Some(&install));

    assert!(result.is_err(), "a regular file must not be deleted as a link");
    assert_eq!(
        fs::read_to_string(&unrelated).expect("unrelated file remains"),
        "user file"
    );
}

#[test]
fn remove_owned_portable_link_rejects_links_outside_selected_user_or_machine_root() {
    let temp = TemporaryDirectory::new();
    let machine_links = temp.create_directory("machine-links");
    let user_links = temp.create_directory("user-links");
    let install = temp.create_directory("package");
    let machine_file = machine_links.join("machine.exe");
    let user_file = user_links.join("user.exe");
    fs::write(&machine_file, "machine file").expect("write machine links sentinel");
    fs::write(&user_file, "user file").expect("write user links sentinel");

    let machine_result = remove_owned_portable_link(&user_file, &machine_links, Some(&install));
    let user_result = remove_owned_portable_link(&machine_file, &user_links, Some(&install));

    assert!(
        machine_result
            .expect_err("user link outside the machine root should be rejected")
            .to_string()
            .contains("outside the links directory")
    );
    assert!(
        user_result
            .expect_err("machine link outside the user root should be rejected")
            .to_string()
            .contains("outside the links directory")
    );
    assert_eq!(
        fs::read_to_string(&machine_file).expect("machine file remains"),
        "machine file"
    );
    assert_eq!(fs::read_to_string(&user_file).expect("user file remains"), "user file");
}

#[test]
fn remove_owned_portable_link_returns_false_for_missing_link() {
    let temp = TemporaryDirectory::new();
    let links = temp.create_directory("machine-links");
    let install = temp.create_directory("package");

    assert!(
        !remove_owned_portable_link(&links.join("missing.exe"), &links, Some(&install))
            .expect("missing link is a no-op"),
        "a missing link should not be reported as removed"
    );
    assert_eq!(fs::read_dir(&links).expect("read links directory").count(), 0);
}

#[test]
fn shared_directory_is_not_treated_as_a_link_target() {
    let temp = TemporaryDirectory::new();
    let location = temp.create_directory("shared");
    let mut entry = ExistingPortableEntry {
        install_location: Some(location.clone()),
        install_directory_created: Some(0),
        portable_target_full_path: Some(location.clone()),
        portable_symlink_full_path: None,
        files: None,
        directories: None,
        subkey_name: "Example.Tool".to_owned(),
    };

    assert_eq!(
        portable_link_target_boundary(Some(&entry), Some(&location)).expect("resolve shared boundary"),
        None
    );
    entry.portable_target_full_path = Some(PathBuf::from("tool.exe"));
    assert_eq!(
        portable_link_target_boundary(Some(&entry), Some(&location)).expect("reject relative target"),
        None
    );
    let target = location.join("tool.exe");
    entry.portable_target_full_path = Some(target.clone());
    assert_eq!(
        portable_link_target_boundary(Some(&entry), Some(&location)).expect("resolve target boundary"),
        Some(target)
    );
    entry.install_directory_created = Some(1);
    assert_eq!(
        portable_link_target_boundary(Some(&entry), Some(&location)).expect("resolve owned boundary"),
        Some(location)
    );
}

#[test]
fn upgrade_to_same_directory_with_unicode_case_preserves_new_files() {
    let temp = TemporaryDirectory::new();
    let location = temp.create_directory("Ünicode");
    let alternate_case = temp.path.join("ünicode");
    let installed_file = location.join("tool.exe");
    fs::write(&installed_file, "new package").expect("write installed file");
    assert!(
        is_owned_portable_directory(true, Some(&location), &alternate_case).expect("compare installation locations")
    );
    let entry = ExistingPortableEntry {
        install_location: Some(location),
        install_directory_created: Some(1),
        portable_target_full_path: Some(installed_file.clone()),
        portable_symlink_full_path: None,
        files: None,
        directories: None,
        subkey_name: "Example.Tool".to_owned(),
    };

    let mut warnings = Vec::new();
    cleanup_previous_portable_install(Some(&entry), &alternate_case, &mut warnings);

    assert!(warnings.is_empty(), "unexpected cleanup warnings: {warnings:?}");
    assert_eq!(
        fs::read_to_string(&installed_file).expect("new package must remain installed"),
        "new package"
    );
}

#[test]
fn old_alias_and_scoped_sweep_preserve_other_packages_and_scopes() {
    let temp = TemporaryDirectory::new();
    let shared = temp.create_directory("shared");
    let machine_links = temp.create_directory("machine-links");
    let user_links = temp.create_directory("user-links");
    let first_target = shared.join("first.exe");
    let second_target = shared.join("second.exe");
    fs::write(&first_target, "first").expect("write first package target");
    fs::write(&second_target, "second").expect("write second package target");
    let old_alias = machine_links.join("old.exe");
    let new_alias = machine_links.join("new.exe");
    let other_alias = machine_links.join("other.exe");
    let user_alias = user_links.join("user.exe");
    for (link, target) in [
        (&old_alias, &first_target),
        (&new_alias, &first_target),
        (&other_alias, &second_target),
        (&user_alias, &first_target),
    ] {
        if !create_symlink_or_skip(link, target, false) {
            return;
        }
    }

    assert!(remove_owned_portable_link(&old_alias, &machine_links, Some(&first_target)).expect("remove stale alias"));
    assert!(!old_alias.exists());
    assert!(new_alias.exists());
    assert!(remove_owned_portable_link(&other_alias, &machine_links, Some(&first_target)).is_err());

    let redirected_root = temp.path.join("redirected-links");
    if !create_symlink_or_skip(&redirected_root, &user_links, true) {
        return;
    }
    let mut warnings = Vec::new();
    remove_portable_links_in_directory(None, Some(&first_target), &redirected_root, &mut warnings);
    assert_eq!(warnings.len(), 1, "a reparse-point links root must be rejected");
    assert!(user_alias.exists());

    warnings.clear();
    remove_portable_links_in_directory(None, Some(&first_target), &machine_links, &mut warnings);
    assert!(warnings.is_empty(), "unexpected sweep warnings: {warnings:?}");
    assert!(!new_alias.exists());
    assert!(other_alias.exists());
    assert!(user_alias.exists());
    assert_eq!(fs::read_to_string(&first_target).expect("read first target"), "first");
    assert_eq!(
        fs::read_to_string(&second_target).expect("read second target"),
        "second"
    );
}

fn create_symlink_or_skip(link: &Path, target: &Path, directory: bool) -> bool {
    let result = if directory {
        std::os::windows::fs::symlink_dir(target, link)
    } else {
        std::os::windows::fs::symlink_file(target, link)
    };
    match result {
        Ok(()) => true,
        Err(error) if error.raw_os_error() == Some(1314) => false,
        Err(error) => panic!("could not create fixture symlink {}: {error}", link.display()),
    }
}

fn expand_test_variables(value: &str) -> String {
    value
        .replace("%SystemRoot%", r"C:\Windows")
        .replace("%USERPROFILE%", r"C:\Users\Example")
        .replace("%ProgramFiles%", r"C:\Program Files")
}

struct TemporaryDirectory {
    path: PathBuf,
}

impl TemporaryDirectory {
    fn new() -> Self {
        static NEXT_ID: AtomicU64 = AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "pinget-portable-regression-{}-{}",
            std::process::id(),
            NEXT_ID.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).expect("create unique portable regression directory");
        Self { path }
    }

    fn create_directory(&self, name: &str) -> PathBuf {
        let path = self.path.join(name);
        fs::create_dir(&path).expect("create portable regression subdirectory");
        path
    }
}

impl Drop for TemporaryDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.path);
    }
}

#[test]
fn determine_portable_alias_uses_nested_file_stem_without_alias_or_command() {
    let installer = Installer {
        nested_installer_files: vec![NestedInstallerFile {
            relative_file_path: r"tools\bin\runner.exe".to_owned(),
            portable_command_alias: None,
        }],
        ..Installer::default()
    };

    assert_eq!(
        determine_portable_alias(&installer).as_deref(),
        Some("runner"),
        "nested file name should supply the missing alias"
    );
}

#[test]
fn determine_portable_alias_prefers_nested_file_stem_over_unrelated_command() {
    let installer = Installer {
        nested_installer_files: vec![NestedInstallerFile {
            relative_file_path: "tools/bin/runner.v2.exe".to_owned(),
            portable_command_alias: None,
        }],
        commands: vec!["other-command".to_owned()],
        ..Installer::default()
    };

    assert_eq!(
        determine_portable_alias(&installer).as_deref(),
        Some("runner.v2"),
        "the nested executable, not an unrelated command, names the link"
    );
}

#[test]
fn determine_portable_alias_uses_explicit_alias_before_file_stem() {
    let installer = Installer {
        nested_installer_files: vec![NestedInstallerFile {
            relative_file_path: "tools/bin/runner.exe".to_owned(),
            portable_command_alias: Some("run-tool".to_owned()),
        }],
        commands: vec!["other-command".to_owned()],
        ..Installer::default()
    };

    assert_eq!(
        determine_portable_alias(&installer).as_deref(),
        Some("run-tool"),
        "the explicit alias should override the filename and command"
    );
}

#[test]
fn determine_portable_alias_uses_first_target_when_later_file_has_alias() {
    let installer = Installer {
        nested_installer_files: vec![
            NestedInstallerFile {
                relative_file_path: "tools/first.exe".to_owned(),
                portable_command_alias: None,
            },
            NestedInstallerFile {
                relative_file_path: "tools/second.exe".to_owned(),
                portable_command_alias: Some("second-alias".to_owned()),
            },
        ],
        commands: vec!["other-command".to_owned()],
        ..Installer::default()
    };

    assert_eq!(
        determine_portable_alias(&installer).as_deref(),
        Some("first"),
        "the alias should correspond to the first recorded portable target"
    );
}

#[test]
fn determine_portable_alias_uses_command_for_standalone_portable() {
    let installer = Installer {
        commands: vec!["standalone-tool".to_owned()],
        ..Installer::default()
    };

    assert_eq!(
        determine_portable_alias(&installer).as_deref(),
        Some("standalone-tool"),
        "standalone packages still use their command"
    );
}
