//! Regression tests for the COM surface defect fixes (wave 22).

use windows_sys::Win32::Foundation::SysFreeString;

use super::*;

/// A package manager object over a private (non-system-WinGet) repository in a fresh temp app root.
struct TestObject {
    raw: *mut PackageManagerObject,
    app_root: PathBuf,
}

impl TestObject {
    fn new() -> Self {
        let app_root = std::env::temp_dir().join(format!(
            "pinget-com-wave22-{}-{}",
            std::process::id(),
            NEXT_ROOT.fetch_add(1, Ordering::AcqRel)
        ));
        let repository =
            Repository::open_with_options(pinget_core::RepositoryOptions::new(&app_root)).expect("open repository");
        ACTIVE_OBJECTS.fetch_add(1, Ordering::AcqRel);
        let object = Box::new(PackageManagerObject {
            vtbl: &PACKAGE_MANAGER_VTBL,
            ref_count: AtomicU32::new(1),
            repository: Mutex::new(repository),
            last_error: Mutex::new(None),
        });
        Self {
            raw: Box::into_raw(object),
            app_root,
        }
    }

    fn last_error(&self) -> String {
        let mut value: BSTR = ptr::null_mut();
        // SAFETY: raw is a live object and value is writable storage for one BSTR.
        let hr = unsafe { package_manager_get_last_error(self.raw, &raw mut value) };
        assert_eq!(hr, S_OK);
        take_bstr(value)
    }

    fn source_names(&self) -> Vec<String> {
        let mut value: BSTR = ptr::null_mut();
        // SAFETY: raw is a live object and value is writable storage for one BSTR.
        let hr = unsafe { package_manager_list_sources_json(self.raw, &raw mut value) };
        assert_eq!(hr, S_OK);
        let sources: JsonValue = serde_json::from_str(&take_bstr(value)).expect("source JSON");
        sources
            .as_array()
            .expect("source array")
            .iter()
            .filter_map(|source| source["name"].as_str().map(ToOwned::to_owned))
            .collect()
    }
}

impl Drop for TestObject {
    fn drop(&mut self) {
        // SAFETY: raw owns the single reference created in new().
        unsafe {
            package_manager_release(self.raw.cast::<c_void>());
        }
        let _ = std::fs::remove_dir_all(&self.app_root);
    }
}

static NEXT_ROOT: AtomicU32 = AtomicU32::new(0);

fn bstr(text: &str) -> BSTR {
    let utf16 = text.encode_utf16().collect::<Vec<_>>();
    let len = u32::try_from(utf16.len()).expect("length");
    // SAFETY: utf16 is valid for len code units; SysAllocStringLen copies them.
    unsafe { SysAllocStringLen(utf16.as_ptr(), len) }
}

fn take_bstr(value: BSTR) -> String {
    let text = optional_bstr(value).expect("valid BSTR").unwrap_or_default();
    // SAFETY: value was allocated by SysAllocStringLen (or is null) and is freed exactly once.
    unsafe { SysFreeString(value) };
    text
}

fn fail_with_stale_error(object: &TestObject) {
    let mut value: BSTR = ptr::null_mut();
    let request = bstr("{ not json");
    // SAFETY: raw is a live object; request is a valid BSTR; value is writable.
    let hr = unsafe { package_manager_install_json(object.raw, request, &raw mut value) };
    // SAFETY: request was allocated above.
    unsafe { SysFreeString(request) };
    assert_eq!(hr, E_FAIL);
    assert!(!object.last_error().is_empty());
}

// ── R22-1: the source methods report their own error through GetLastError ──

#[test]
fn remove_source_failure_reports_its_own_error_not_a_stale_one() {
    let object = TestObject::new();
    fail_with_stale_error(&object);

    let name = bstr("Pinget.Missing.Source");
    // SAFETY: raw is a live object and name is a valid BSTR.
    let hr = unsafe { package_manager_remove_source(object.raw, name) };
    // SAFETY: name was allocated above.
    unsafe { SysFreeString(name) };

    assert_eq!(hr, E_FAIL);
    assert!(
        object.last_error().contains("Pinget.Missing.Source"),
        "GetLastError must describe the RemoveSource failure, got: {}",
        object.last_error()
    );
}

#[test]
fn add_source_failure_reports_its_own_error() {
    let object = TestObject::new();
    fail_with_stale_error(&object);

    let name = bstr("Insecure");
    let argument = bstr("http://example.test/feed");
    let kind = bstr("Microsoft.Rest");
    // SAFETY: raw is a live object and every BSTR argument is valid or null.
    let hr = unsafe { package_manager_add_source(object.raw, name, argument, kind, ptr::null_mut(), 0, 0) };
    // SAFETY: the BSTRs were allocated above.
    unsafe {
        SysFreeString(name);
        SysFreeString(argument);
        SysFreeString(kind);
    }

    assert_eq!(hr, E_FAIL);
    assert!(
        object.last_error().contains("not secure"),
        "got: {}",
        object.last_error()
    );
}

#[test]
fn successful_source_call_clears_a_stale_error() {
    let object = TestObject::new();
    fail_with_stale_error(&object);

    let name = bstr("Extra");
    let argument = bstr("https://example.test/feed");
    let kind = bstr("Microsoft.Rest");
    // SAFETY: raw is a live object and every BSTR argument is valid or null.
    let hr = unsafe { package_manager_add_source(object.raw, name, argument, kind, ptr::null_mut(), 0, 0) };
    // SAFETY: the BSTRs were allocated above.
    unsafe {
        SysFreeString(name);
        SysFreeString(argument);
        SysFreeString(kind);
    }

    assert_eq!(hr, S_OK);
    assert_eq!(object.last_error(), "");
}

#[test]
fn reset_source_without_name_or_all_explains_the_invalid_argument() {
    let object = TestObject::new();
    fail_with_stale_error(&object);

    // SAFETY: raw is a live object; a null BSTR is a valid empty name.
    let hr = unsafe { package_manager_reset_source(object.raw, ptr::null_mut(), 0) };

    assert_eq!(hr, E_INVALIDARG);
    assert!(
        object.last_error().contains("source name"),
        "got: {}",
        object.last_error()
    );
}

// ── R22-2: a named reset only resets that source, even when reset-all is also passed ──

#[test]
fn reset_source_by_name_takes_precedence_over_all() {
    let object = TestObject::new();
    let name = bstr("Extra");
    let argument = bstr("https://example.test/feed");
    let kind = bstr("Microsoft.Rest");
    // SAFETY: raw is a live object and every BSTR argument is valid or null.
    let hr = unsafe { package_manager_add_source(object.raw, name, argument, kind, ptr::null_mut(), 0, 0) };
    // SAFETY: the BSTRs were allocated above.
    unsafe {
        SysFreeString(name);
        SysFreeString(argument);
        SysFreeString(kind);
    }
    assert_eq!(hr, S_OK);
    assert!(object.source_names().iter().any(|source| source == "Extra"));

    let winget = bstr("winget");
    // SAFETY: raw is a live object and winget is a valid BSTR.
    let hr = unsafe { package_manager_reset_source(object.raw, winget, 1) };
    // SAFETY: winget was allocated above.
    unsafe { SysFreeString(winget) };

    assert_eq!(hr, S_OK);
    assert!(
        object.source_names().iter().any(|source| source == "Extra"),
        "resetting the named 'winget' source must not reset every other source"
    );
}
