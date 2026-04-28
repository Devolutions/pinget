#![cfg(windows)]
#![allow(clippy::multiple_unsafe_ops_per_block)]

use std::ffi::c_void;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::sync::atomic::{AtomicU32, Ordering};
use std::{ptr, slice};

use anyhow::{Context, Result};
use pinget_core::{
    InstallRequest, InstallerMode, ListQuery, PackageQuery, PinType, RepairRequest, Repository, SourceKind,
    UninstallRequest,
};
use serde_json::{Value as JsonValue, json};
use windows_sys::Win32::Foundation::{
    CLASS_E_CLASSNOTAVAILABLE, CLASS_E_NOAGGREGATION, E_FAIL, E_INVALIDARG, E_NOINTERFACE, E_POINTER, S_FALSE, S_OK,
    SysAllocStringLen, SysStringLen,
};
use windows_sys::core::{BSTR, GUID, HRESULT};

const VERSION: &str = env!("CARGO_PKG_VERSION");

const IID_IUNKNOWN: GUID = guid(
    0x00000000,
    0x0000,
    0x0000,
    [0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46],
);
const IID_ICLASS_FACTORY: GUID = guid(
    0x00000001,
    0x0000,
    0x0000,
    [0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46],
);

// Devolutions.Pinget.Deployment.PackageManager
const CLSID_PACKAGE_MANAGER: GUID = guid(
    0x8F3C6294,
    0x2BB9,
    0x4F8C,
    [0x87, 0xA8, 0x4D, 0x5C, 0x0A, 0x7B, 0xA1, 0x10],
);

// Devolutions.Pinget.Deployment.IPackageManager
const IID_IPACKAGE_MANAGER: GUID = guid(
    0x347FA969,
    0xB65E,
    0x4282,
    [0x9E, 0x51, 0x2F, 0x89, 0x2E, 0x11, 0xA3, 0x22],
);

static ACTIVE_OBJECTS: AtomicU32 = AtomicU32::new(0);
static SERVER_LOCKS: AtomicU32 = AtomicU32::new(0);

const fn guid(data1: u32, data2: u16, data3: u16, data4: [u8; 8]) -> GUID {
    GUID {
        data1,
        data2,
        data3,
        data4,
    }
}

fn guid_eq(left: &GUID, right: &GUID) -> bool {
    left.data1 == right.data1 && left.data2 == right.data2 && left.data3 == right.data3 && left.data4 == right.data4
}

#[repr(C)]
struct UnknownVTable {
    query_interface: unsafe extern "system" fn(*mut c_void, *const GUID, *mut *mut c_void) -> HRESULT,
    add_ref: unsafe extern "system" fn(*mut c_void) -> u32,
    release: unsafe extern "system" fn(*mut c_void) -> u32,
}

#[repr(C)]
struct PackageManagerVTable {
    unknown: UnknownVTable,
    get_version: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
    get_default_app_root: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
    list_sources_json: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
    add_source: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, BSTR, BSTR, BSTR, i32, i32) -> HRESULT,
    remove_source: unsafe extern "system" fn(*mut PackageManagerObject, BSTR) -> HRESULT,
    reset_source: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, i32) -> HRESULT,
    edit_source_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR) -> HRESULT,
    update_sources_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    get_user_settings_json: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
    set_user_settings_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, i32, *mut BSTR) -> HRESULT,
    test_user_settings_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, i32, *mut i32) -> HRESULT,
    get_admin_settings_json: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
    set_admin_setting: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, i32) -> HRESULT,
    reset_admin_setting: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, i32) -> HRESULT,
    ensure_settings_files: unsafe extern "system" fn(*mut PackageManagerObject) -> HRESULT,
    search_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    search_manifests_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    list_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    search_versions_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    show_versions_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    show_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    warm_cache_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    list_pins_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    add_pin: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, BSTR, BSTR, BSTR) -> HRESULT,
    remove_pin: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, BSTR, *mut i32) -> HRESULT,
    reset_pins: unsafe extern "system" fn(*mut PackageManagerObject, BSTR) -> HRESULT,
    download_installer_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, BSTR, *mut BSTR) -> HRESULT,
    install_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    uninstall_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    repair_json: unsafe extern "system" fn(*mut PackageManagerObject, BSTR, *mut BSTR) -> HRESULT,
    get_last_error: unsafe extern "system" fn(*mut PackageManagerObject, *mut BSTR) -> HRESULT,
}

#[repr(C)]
struct ClassFactoryVTable {
    unknown: UnknownVTable,
    create_instance:
        unsafe extern "system" fn(*mut ClassFactoryObject, *mut c_void, *const GUID, *mut *mut c_void) -> HRESULT,
    lock_server: unsafe extern "system" fn(*mut ClassFactoryObject, i32) -> HRESULT,
}

#[repr(C)]
struct PackageManagerObject {
    vtbl: *const PackageManagerVTable,
    ref_count: AtomicU32,
    repository: Mutex<Repository>,
    last_error: Mutex<Option<String>>,
}

#[repr(C)]
struct ClassFactoryObject {
    vtbl: *const ClassFactoryVTable,
    ref_count: AtomicU32,
}

impl PackageManagerObject {
    fn new() -> Result<Box<Self>> {
        let repository = Repository::open().context("failed to open Pinget repository")?;
        ACTIVE_OBJECTS.fetch_add(1, Ordering::AcqRel);
        Ok(Box::new(Self {
            vtbl: &PACKAGE_MANAGER_VTBL,
            ref_count: AtomicU32::new(1),
            repository: Mutex::new(repository),
            last_error: Mutex::new(None),
        }))
    }
}

impl Drop for PackageManagerObject {
    fn drop(&mut self) {
        ACTIVE_OBJECTS.fetch_sub(1, Ordering::AcqRel);
    }
}

impl ClassFactoryObject {
    fn new() -> Self {
        ACTIVE_OBJECTS.fetch_add(1, Ordering::AcqRel);
        Self {
            vtbl: &CLASS_FACTORY_VTBL,
            ref_count: AtomicU32::new(1),
        }
    }
}

impl Drop for ClassFactoryObject {
    fn drop(&mut self) {
        ACTIVE_OBJECTS.fetch_sub(1, Ordering::AcqRel);
    }
}

static PACKAGE_MANAGER_VTBL: PackageManagerVTable = PackageManagerVTable {
    unknown: UnknownVTable {
        query_interface: package_manager_query_interface,
        add_ref: package_manager_add_ref,
        release: package_manager_release,
    },
    get_version: package_manager_get_version,
    get_default_app_root: package_manager_get_default_app_root,
    list_sources_json: package_manager_list_sources_json,
    add_source: package_manager_add_source,
    remove_source: package_manager_remove_source,
    reset_source: package_manager_reset_source,
    edit_source_json: package_manager_edit_source_json,
    update_sources_json: package_manager_update_sources_json,
    get_user_settings_json: package_manager_get_user_settings_json,
    set_user_settings_json: package_manager_set_user_settings_json,
    test_user_settings_json: package_manager_test_user_settings_json,
    get_admin_settings_json: package_manager_get_admin_settings_json,
    set_admin_setting: package_manager_set_admin_setting,
    reset_admin_setting: package_manager_reset_admin_setting,
    ensure_settings_files: package_manager_ensure_settings_files,
    search_json: package_manager_search_json,
    search_manifests_json: package_manager_search_manifests_json,
    list_json: package_manager_list_json,
    search_versions_json: package_manager_search_versions_json,
    show_versions_json: package_manager_show_versions_json,
    show_json: package_manager_show_json,
    warm_cache_json: package_manager_warm_cache_json,
    list_pins_json: package_manager_list_pins_json,
    add_pin: package_manager_add_pin,
    remove_pin: package_manager_remove_pin,
    reset_pins: package_manager_reset_pins,
    download_installer_json: package_manager_download_installer_json,
    install_json: package_manager_install_json,
    uninstall_json: package_manager_uninstall_json,
    repair_json: package_manager_repair_json,
    get_last_error: package_manager_get_last_error,
};

static CLASS_FACTORY_VTBL: ClassFactoryVTable = ClassFactoryVTable {
    unknown: UnknownVTable {
        query_interface: class_factory_query_interface,
        add_ref: class_factory_add_ref,
        release: class_factory_release,
    },
    create_instance: class_factory_create_instance,
    lock_server: class_factory_lock_server,
};

unsafe extern "system" fn package_manager_query_interface(
    this: *mut c_void,
    riid: *const GUID,
    object: *mut *mut c_void,
) -> HRESULT {
    if object.is_null() {
        return E_POINTER;
    }

    // SAFETY: The out pointer was checked above and is owned by the caller for this call.
    unsafe {
        *object = ptr::null_mut();
    }

    if riid.is_null() {
        return E_POINTER;
    }

    // SAFETY: riid is non-null and points to a GUID provided by the COM caller.
    let requested = unsafe { *riid };
    if !guid_eq(&requested, &IID_IUNKNOWN) && !guid_eq(&requested, &IID_IPACKAGE_MANAGER) {
        return E_NOINTERFACE;
    }

    // SAFETY: this is a valid PackageManagerObject pointer for calls routed through this vtable.
    unsafe {
        package_manager_add_ref(this);
        *object = this;
    }

    S_OK
}

unsafe extern "system" fn package_manager_add_ref(this: *mut c_void) -> u32 {
    // SAFETY: COM routes this call only for live PackageManagerObject instances.
    let object = unsafe { &*(this.cast::<PackageManagerObject>()) };
    object.ref_count.fetch_add(1, Ordering::AcqRel) + 1
}

unsafe extern "system" fn package_manager_release(this: *mut c_void) -> u32 {
    // SAFETY: COM routes this call only for live PackageManagerObject instances.
    let object = unsafe { &*(this.cast::<PackageManagerObject>()) };
    let count = object.ref_count.fetch_sub(1, Ordering::AcqRel) - 1;
    if count == 0 {
        // SAFETY: The final Release owns the original Box allocated by Box::into_raw.
        unsafe {
            drop(Box::from_raw(this.cast::<PackageManagerObject>()));
        }
    }

    count
}

unsafe extern "system" fn package_manager_get_version(_this: *mut PackageManagerObject, value: *mut BSTR) -> HRESULT {
    write_bstr(value, VERSION)
}

unsafe extern "system" fn package_manager_get_default_app_root(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    write_bstr(value, &guard.app_root().display().to_string())
}

unsafe extern "system" fn package_manager_list_sources_json(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    let sources = guard
        .list_sources()
        .into_iter()
        .map(|source| {
            serde_json::json!({
                "name": source.name,
                "argument": source.arg,
                "type": source_kind_name(source.kind),
                "trustLevel": source.trust_level,
                "explicit": source.explicit,
                "priority": source.priority,
                "identifier": source.identifier,
                "lastUpdate": source.last_update.map(|value| value.to_rfc3339()),
                "sourceVersion": source.source_version,
            })
        })
        .collect::<Vec<_>>();

    match serde_json::to_string(&sources) {
        Ok(json) => write_bstr(value, &json),
        Err(_) => E_FAIL,
    }
}

unsafe extern "system" fn package_manager_add_source(
    this: *mut PackageManagerObject,
    name: BSTR,
    argument: BSTR,
    source_type: BSTR,
    trust_level: BSTR,
    explicit: i32,
    priority: i32,
) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    let name = match required_bstr(name) {
        Ok(value) => value,
        Err(hr) => return hr,
    };
    let argument = match required_bstr(argument) {
        Ok(value) => value,
        Err(hr) => return hr,
    };
    let source_type = match optional_bstr(source_type) {
        Ok(value) => value,
        Err(hr) => return hr,
    };
    let trust_level = match optional_bstr(trust_level) {
        Ok(value) => value,
        Err(hr) => return hr,
    };
    let source_kind = match parse_source_kind(source_type.as_deref()) {
        Some(value) => value,
        None => return E_INVALIDARG,
    };

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let mut guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    match guard.add_source_with_metadata(
        &name,
        &argument,
        source_kind,
        trust_level.as_deref(),
        explicit != 0,
        priority,
    ) {
        Ok(()) => S_OK,
        Err(_) => E_FAIL,
    }
}

unsafe extern "system" fn package_manager_remove_source(this: *mut PackageManagerObject, name: BSTR) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    let name = match required_bstr(name) {
        Ok(value) => value,
        Err(hr) => return hr,
    };

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let mut guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    match guard.remove_source(&name) {
        Ok(()) => S_OK,
        Err(_) => E_FAIL,
    }
}

unsafe extern "system" fn package_manager_reset_source(
    this: *mut PackageManagerObject,
    name: BSTR,
    all: i32,
) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    let name = match optional_bstr(name) {
        Ok(value) => value,
        Err(hr) => return hr,
    };

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let mut guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    let result = if all != 0 {
        guard.reset_sources()
    } else if let Some(name) = name.as_deref() {
        guard.reset_source(name)
    } else {
        return E_INVALIDARG;
    };

    match result {
        Ok(()) => S_OK,
        Err(_) => E_FAIL,
    }
}

unsafe extern "system" fn package_manager_edit_source_json(this: *mut PackageManagerObject, request: BSTR) -> HRESULT {
    with_repository_mut(this, |repository| {
        let request = json_from_bstr(request)?;
        let name = required_json_string(&request, &["name"])?;
        let explicit = optional_json_bool(&request, &["explicit", "explicitSource"]);
        let trust_level = optional_json_string(&request, &["trust_level", "trustLevel"]);
        repository.edit_source(name, explicit, trust_level.as_deref())
    })
}

unsafe extern "system" fn package_manager_update_sources_json(
    this: *mut PackageManagerObject,
    source_name: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let source_name = optional_bstr(source_name).map_err(|_| anyhow::anyhow!("invalid source name"))?;
        repository.update_sources(source_name.as_deref())
    })
}

unsafe extern "system" fn package_manager_get_user_settings_json(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| repository.get_user_settings())
}

unsafe extern "system" fn package_manager_set_user_settings_json(
    this: *mut PackageManagerObject,
    settings: BSTR,
    merge: i32,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let settings = json_from_bstr(settings)?;
        repository.set_user_settings(&settings, merge != 0)
    })
}

unsafe extern "system" fn package_manager_test_user_settings_json(
    this: *mut PackageManagerObject,
    expected: BSTR,
    ignore_not_set: i32,
    matched: *mut i32,
) -> HRESULT {
    if matched.is_null() {
        return E_POINTER;
    }

    with_repository_mut(this, |repository| {
        let expected = json_from_bstr(expected)?;
        let result = repository.test_user_settings(&expected, ignore_not_set != 0)?;
        // SAFETY: matched was checked non-null and points to caller-owned writable storage.
        unsafe {
            *matched = i32::from(result);
        }
        Ok(())
    })
}

unsafe extern "system" fn package_manager_get_admin_settings_json(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| repository.get_admin_settings())
}

unsafe extern "system" fn package_manager_set_admin_setting(
    this: *mut PackageManagerObject,
    name: BSTR,
    enabled: i32,
) -> HRESULT {
    with_repository_mut(this, |repository| {
        let name = required_bstr(name).map_err(|_| anyhow::anyhow!("setting name is required"))?;
        repository.set_admin_setting(&name, enabled != 0)
    })
}

unsafe extern "system" fn package_manager_reset_admin_setting(
    this: *mut PackageManagerObject,
    name: BSTR,
    reset_all: i32,
) -> HRESULT {
    with_repository_mut(this, |repository| {
        let name = optional_bstr(name).map_err(|_| anyhow::anyhow!("invalid setting name"))?;
        repository.reset_admin_setting(name.as_deref(), reset_all != 0)
    })
}

unsafe extern "system" fn package_manager_ensure_settings_files(this: *mut PackageManagerObject) -> HRESULT {
    with_repository_mut(this, |repository| repository.ensure_settings_files())
}

unsafe extern "system" fn package_manager_search_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        repository.search(&query)
    })
}

unsafe extern "system" fn package_manager_search_manifests_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        repository.search_manifests(&query)
    })
}

unsafe extern "system" fn package_manager_list_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = list_query_from_json(&json_from_bstr(query)?)?;
        repository.list(&query)
    })
}

unsafe extern "system" fn package_manager_search_versions_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        repository.search_versions(&query)
    })
}

unsafe extern "system" fn package_manager_show_versions_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        repository.show_versions(&query)
    })
}

unsafe extern "system" fn package_manager_show_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json_value(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        let show = repository.show(&query)?;
        let structured_document = show.structured_document();
        let mut result = serde_json::to_value(&show)?;
        if let JsonValue::Object(object) = &mut result {
            object.insert("structured_document".to_owned(), structured_document);
        }
        Ok(result)
    })
}

unsafe extern "system" fn package_manager_warm_cache_json(
    this: *mut PackageManagerObject,
    query: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let query = package_query_from_json(&json_from_bstr(query)?)?;
        repository.warm_cache(&query)
    })
}

unsafe extern "system" fn package_manager_list_pins_json(
    this: *mut PackageManagerObject,
    source_id: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let source_id = optional_bstr(source_id).map_err(|_| anyhow::anyhow!("invalid source id"))?;
        repository.list_pins(source_id.as_deref())
    })
}

unsafe extern "system" fn package_manager_add_pin(
    this: *mut PackageManagerObject,
    package_id: BSTR,
    version: BSTR,
    source_id: BSTR,
    pin_type: BSTR,
) -> HRESULT {
    with_repository_mut(this, |repository| {
        let package_id = required_bstr(package_id).map_err(|_| anyhow::anyhow!("package id is required"))?;
        let version = required_bstr(version).map_err(|_| anyhow::anyhow!("version is required"))?;
        let source_id = required_bstr(source_id).map_err(|_| anyhow::anyhow!("source id is required"))?;
        let pin_type = required_bstr(pin_type).map_err(|_| anyhow::anyhow!("pin type is required"))?;
        repository.add_pin(&package_id, &version, &source_id, parse_pin_type(&pin_type)?)
    })
}

unsafe extern "system" fn package_manager_remove_pin(
    this: *mut PackageManagerObject,
    package_id: BSTR,
    source_id: BSTR,
    removed: *mut i32,
) -> HRESULT {
    if removed.is_null() {
        return E_POINTER;
    }

    with_repository_mut(this, |repository| {
        let package_id = required_bstr(package_id).map_err(|_| anyhow::anyhow!("package id is required"))?;
        let source_id = optional_bstr(source_id).map_err(|_| anyhow::anyhow!("invalid source id"))?;
        let result = repository.remove_pin(&package_id, source_id.as_deref())?;
        // SAFETY: removed was checked non-null and points to caller-owned writable storage.
        unsafe {
            *removed = i32::from(result);
        }
        Ok(())
    })
}

unsafe extern "system" fn package_manager_reset_pins(this: *mut PackageManagerObject, source_id: BSTR) -> HRESULT {
    with_repository_mut(this, |repository| {
        let source_id = optional_bstr(source_id).map_err(|_| anyhow::anyhow!("invalid source id"))?;
        repository.reset_pins(source_id.as_deref())
    })
}

unsafe extern "system" fn package_manager_download_installer_json(
    this: *mut PackageManagerObject,
    request: BSTR,
    download_dir: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json_value(this, value, |repository| {
        let request = install_request_from_json(&json_from_bstr(request)?)?;
        let download_dir =
            required_bstr(download_dir).map_err(|_| anyhow::anyhow!("download directory is required"))?;
        let (manifest, installer_path) =
            repository.download_installer_for_request(&request, Path::new(&download_dir))?;
        Ok(json!({
            "manifest": manifest,
            "installer_path": installer_path,
        }))
    })
}

unsafe extern "system" fn package_manager_install_json(
    this: *mut PackageManagerObject,
    request: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let request = install_request_from_json(&json_from_bstr(request)?)?;
        repository.install_request(&request)
    })
}

unsafe extern "system" fn package_manager_uninstall_json(
    this: *mut PackageManagerObject,
    request: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let request = uninstall_request_from_json(&json_from_bstr(request)?)?;
        repository.uninstall_request(&request)
    })
}

unsafe extern "system" fn package_manager_repair_json(
    this: *mut PackageManagerObject,
    request: BSTR,
    value: *mut BSTR,
) -> HRESULT {
    with_repository_json(this, value, |repository| {
        let request = repair_request_from_json(&json_from_bstr(request)?)?;
        repository.repair(&request)
    })
}

unsafe extern "system" fn package_manager_get_last_error(this: *mut PackageManagerObject, value: *mut BSTR) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    let guard = match object.last_error.lock() {
        Ok(guard) => guard,
        Err(_) => return E_FAIL,
    };

    write_bstr(value, guard.as_deref().unwrap_or_default())
}

unsafe extern "system" fn class_factory_query_interface(
    this: *mut c_void,
    riid: *const GUID,
    object: *mut *mut c_void,
) -> HRESULT {
    if object.is_null() {
        return E_POINTER;
    }

    // SAFETY: The out pointer was checked above and is owned by the caller for this call.
    unsafe {
        *object = ptr::null_mut();
    }

    if riid.is_null() {
        return E_POINTER;
    }

    // SAFETY: riid is non-null and points to a GUID provided by the COM caller.
    let requested = unsafe { *riid };
    if !guid_eq(&requested, &IID_IUNKNOWN) && !guid_eq(&requested, &IID_ICLASS_FACTORY) {
        return E_NOINTERFACE;
    }

    // SAFETY: this is a valid ClassFactoryObject pointer for calls routed through this vtable.
    unsafe {
        class_factory_add_ref(this);
        *object = this;
    }

    S_OK
}

unsafe extern "system" fn class_factory_add_ref(this: *mut c_void) -> u32 {
    // SAFETY: COM routes this call only for live ClassFactoryObject instances.
    let object = unsafe { &*(this.cast::<ClassFactoryObject>()) };
    object.ref_count.fetch_add(1, Ordering::AcqRel) + 1
}

unsafe extern "system" fn class_factory_release(this: *mut c_void) -> u32 {
    // SAFETY: COM routes this call only for live ClassFactoryObject instances.
    let object = unsafe { &*(this.cast::<ClassFactoryObject>()) };
    let count = object.ref_count.fetch_sub(1, Ordering::AcqRel) - 1;
    if count == 0 {
        // SAFETY: The final Release owns the original Box allocated by Box::into_raw.
        unsafe {
            drop(Box::from_raw(this.cast::<ClassFactoryObject>()));
        }
    }

    count
}

unsafe extern "system" fn class_factory_create_instance(
    _this: *mut ClassFactoryObject,
    outer: *mut c_void,
    riid: *const GUID,
    object: *mut *mut c_void,
) -> HRESULT {
    if !outer.is_null() {
        return CLASS_E_NOAGGREGATION;
    }

    // SAFETY: create_package_manager validates all caller-provided pointers.
    unsafe { create_package_manager(riid, object) }
}

unsafe extern "system" fn class_factory_lock_server(_this: *mut ClassFactoryObject, lock: i32) -> HRESULT {
    if lock != 0 {
        SERVER_LOCKS.fetch_add(1, Ordering::AcqRel);
    } else {
        SERVER_LOCKS.fetch_sub(1, Ordering::AcqRel);
    }

    S_OK
}

fn with_repository_mut(this: *mut PackageManagerObject, action: impl FnOnce(&mut Repository) -> Result<()>) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    clear_last_error(object);
    let mut guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => {
            set_last_error(object, "Pinget repository lock was poisoned.");
            return E_FAIL;
        }
    };

    match action(&mut guard) {
        Ok(()) => S_OK,
        Err(error) => {
            set_last_error(object, error.to_string());
            E_FAIL
        }
    }
}

fn with_repository_json<T: serde::Serialize>(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
    action: impl FnOnce(&mut Repository) -> Result<T>,
) -> HRESULT {
    with_repository_json_value(this, value, |repository| Ok(serde_json::to_value(action(repository)?)?))
}

fn with_repository_json_value(
    this: *mut PackageManagerObject,
    value: *mut BSTR,
    action: impl FnOnce(&mut Repository) -> Result<JsonValue>,
) -> HRESULT {
    if this.is_null() {
        return E_POINTER;
    }

    // SAFETY: this is checked non-null and is a PackageManagerObject for this vtable method.
    let object = unsafe { &*this };
    clear_last_error(object);
    let mut guard = match object.repository.lock() {
        Ok(guard) => guard,
        Err(_) => {
            set_last_error(object, "Pinget repository lock was poisoned.");
            return E_FAIL;
        }
    };

    let result = match action(&mut guard).and_then(|value| Ok(serde_json::to_string(&value)?)) {
        Ok(json) => json,
        Err(error) => {
            set_last_error(object, error.to_string());
            return E_FAIL;
        }
    };

    write_bstr(value, &result)
}

fn clear_last_error(object: &PackageManagerObject) {
    if let Ok(mut last_error) = object.last_error.lock() {
        *last_error = None;
    }
}

fn set_last_error(object: &PackageManagerObject, error: impl Into<String>) {
    if let Ok(mut last_error) = object.last_error.lock() {
        *last_error = Some(error.into());
    }
}

fn source_kind_name(kind: SourceKind) -> &'static str {
    match kind {
        SourceKind::PreIndexed => "Microsoft.PreIndexed.Package",
        SourceKind::Rest => "Microsoft.Rest",
    }
}

fn parse_source_kind(value: Option<&str>) -> Option<SourceKind> {
    match value {
        None => Some(SourceKind::Rest),
        Some(value) if value.eq_ignore_ascii_case("rest") || value.eq_ignore_ascii_case("Microsoft.Rest") => {
            Some(SourceKind::Rest)
        }
        Some(value)
            if value.eq_ignore_ascii_case("preindexed")
                || value.eq_ignore_ascii_case("pre-indexed")
                || value.eq_ignore_ascii_case("Microsoft.PreIndexed.Package") =>
        {
            Some(SourceKind::PreIndexed)
        }
        _ => None,
    }
}

fn required_bstr(value: BSTR) -> Result<String, HRESULT> {
    let value = optional_bstr(value)?;
    match value {
        Some(value) if !value.trim().is_empty() => Ok(value),
        _ => Err(E_INVALIDARG),
    }
}

fn optional_bstr(value: BSTR) -> Result<Option<String>, HRESULT> {
    if value.is_null() {
        return Ok(None);
    }

    // SAFETY: value is a BSTR pointer provided by the COM caller.
    let len = unsafe { SysStringLen(value) };
    let Ok(len) = usize::try_from(len) else {
        return Err(E_INVALIDARG);
    };

    // SAFETY: value points to a BSTR containing len UTF-16 code units.
    let slice = unsafe { slice::from_raw_parts(value, len) };
    let text = match String::from_utf16(slice) {
        Ok(value) => value,
        Err(_) => return Err(E_INVALIDARG),
    };

    if text.trim().is_empty() {
        Ok(None)
    } else {
        Ok(Some(text))
    }
}

fn json_from_bstr(value: BSTR) -> Result<JsonValue> {
    let text = required_bstr(value).map_err(|_| anyhow::anyhow!("JSON input is required"))?;
    Ok(serde_json::from_str(&text)?)
}

fn package_query_from_json(value: &JsonValue) -> Result<PackageQuery> {
    Ok(PackageQuery {
        query: optional_json_string(value, &["query"]),
        id: optional_json_string(value, &["id"]),
        name: optional_json_string(value, &["name"]),
        moniker: optional_json_string(value, &["moniker"]),
        tag: optional_json_string(value, &["tag"]),
        command: optional_json_string(value, &["command"]),
        source: optional_json_string(value, &["source"]),
        count: optional_json_usize(value, &["count"]),
        exact: optional_json_bool(value, &["exact"]).unwrap_or(false),
        version: optional_json_string(value, &["version"]),
        channel: optional_json_string(value, &["channel"]),
        locale: optional_json_string(value, &["locale"]),
        installer_type: optional_json_string(value, &["installer_type", "installerType"]),
        installer_architecture: optional_json_string(value, &["installer_architecture", "installerArchitecture"]),
        platform: optional_json_string(value, &["platform"]),
        os_version: optional_json_string(value, &["os_version", "osVersion"]),
        install_scope: optional_json_string(value, &["install_scope", "installScope"]),
    })
}

fn list_query_from_json(value: &JsonValue) -> Result<ListQuery> {
    Ok(ListQuery {
        query: optional_json_string(value, &["query"]),
        id: optional_json_string(value, &["id"]),
        name: optional_json_string(value, &["name"]),
        moniker: optional_json_string(value, &["moniker"]),
        tag: optional_json_string(value, &["tag"]),
        command: optional_json_string(value, &["command"]),
        product_code: optional_json_string(value, &["product_code", "productCode"]),
        version: optional_json_string(value, &["version"]),
        source: optional_json_string(value, &["source"]),
        count: optional_json_usize(value, &["count"]),
        exact: optional_json_bool(value, &["exact"]).unwrap_or(false),
        install_scope: optional_json_string(value, &["install_scope", "installScope"]),
        upgrade_only: optional_json_bool(value, &["upgrade_only", "upgradeOnly"]).unwrap_or(false),
        include_unknown: optional_json_bool(value, &["include_unknown", "includeUnknown"]).unwrap_or(false),
        include_pinned: optional_json_bool(value, &["include_pinned", "includePinned"]).unwrap_or(false),
    })
}

fn install_request_from_json(value: &JsonValue) -> Result<InstallRequest> {
    let query = nested_json(value, &["query"])
        .map(package_query_from_json)
        .transpose()?
        .unwrap_or_default();
    let mut request = InstallRequest::new(query);
    request.manifest_path = optional_path(value, &["manifest_path", "manifestPath"]);
    request.mode = installer_mode_from_json(value, &["mode"])?;
    request.log_path = optional_path(value, &["log_path", "logPath"]);
    request.custom = optional_json_string(value, &["custom"]);
    request.override_args = optional_json_string(value, &["override_args", "overrideArgs", "override"]);
    request.install_location = optional_json_string(value, &["install_location", "installLocation"]);
    request.skip_dependencies = optional_json_bool(value, &["skip_dependencies", "skipDependencies"]).unwrap_or(false);
    request.dependencies_only = optional_json_bool(value, &["dependencies_only", "dependenciesOnly"]).unwrap_or(false);
    request.accept_package_agreements =
        optional_json_bool(value, &["accept_package_agreements", "acceptPackageAgreements"]).unwrap_or(false);
    request.force = optional_json_bool(value, &["force"]).unwrap_or(false);
    request.rename = optional_json_string(value, &["rename"]);
    request.uninstall_previous =
        optional_json_bool(value, &["uninstall_previous", "uninstallPrevious"]).unwrap_or(false);
    request.ignore_security_hash =
        optional_json_bool(value, &["ignore_security_hash", "ignoreSecurityHash"]).unwrap_or(false);
    request.dependency_source = optional_json_string(value, &["dependency_source", "dependencySource"]);
    request.no_upgrade = optional_json_bool(value, &["no_upgrade", "noUpgrade"]).unwrap_or(false);
    Ok(request)
}

fn uninstall_request_from_json(value: &JsonValue) -> Result<UninstallRequest> {
    let query = nested_json(value, &["query"])
        .map(package_query_from_json)
        .transpose()?
        .unwrap_or_default();
    let mut request = UninstallRequest::new(query);
    request.manifest_path = optional_path(value, &["manifest_path", "manifestPath"]);
    request.product_code = optional_json_string(value, &["product_code", "productCode"]);
    request.mode = installer_mode_from_json(value, &["mode"])?;
    request.all_versions = optional_json_bool(value, &["all_versions", "allVersions"]).unwrap_or(false);
    request.force = optional_json_bool(value, &["force"]).unwrap_or(false);
    request.purge = optional_json_bool(value, &["purge"]).unwrap_or(false);
    request.preserve = optional_json_bool(value, &["preserve"]).unwrap_or(false);
    request.log_path = optional_path(value, &["log_path", "logPath"]);
    Ok(request)
}

fn repair_request_from_json(value: &JsonValue) -> Result<RepairRequest> {
    let query = nested_json(value, &["query"])
        .map(package_query_from_json)
        .transpose()?
        .unwrap_or_default();
    let mut request = RepairRequest::new(query);
    request.manifest_path = optional_path(value, &["manifest_path", "manifestPath"]);
    request.product_code = optional_json_string(value, &["product_code", "productCode"]);
    request.mode = installer_mode_from_json(value, &["mode"])?;
    request.log_path = optional_path(value, &["log_path", "logPath"]);
    request.accept_package_agreements =
        optional_json_bool(value, &["accept_package_agreements", "acceptPackageAgreements"]).unwrap_or(false);
    request.force = optional_json_bool(value, &["force"]).unwrap_or(false);
    request.ignore_security_hash =
        optional_json_bool(value, &["ignore_security_hash", "ignoreSecurityHash"]).unwrap_or(false);
    Ok(request)
}

fn installer_mode_from_json(value: &JsonValue, names: &[&str]) -> Result<InstallerMode> {
    match optional_json_string(value, names).as_deref() {
        None | Some("") | Some("Default") | Some("SilentWithProgress") => Ok(InstallerMode::SilentWithProgress),
        Some(value) if value.eq_ignore_ascii_case("Silent") => Ok(InstallerMode::Silent),
        Some(value) if value.eq_ignore_ascii_case("Interactive") => Ok(InstallerMode::Interactive),
        Some(value) if value.eq_ignore_ascii_case("SilentWithProgress") => Ok(InstallerMode::SilentWithProgress),
        Some(value) => Err(anyhow::anyhow!("unsupported installer mode '{value}'")),
    }
}

fn parse_pin_type(value: &str) -> Result<PinType> {
    if value.eq_ignore_ascii_case("Pinning") {
        Ok(PinType::Pinning)
    } else if value.eq_ignore_ascii_case("Blocking") {
        Ok(PinType::Blocking)
    } else if value.eq_ignore_ascii_case("Gating") {
        Ok(PinType::Gating)
    } else {
        Err(anyhow::anyhow!("unsupported pin type '{value}'"))
    }
}

fn required_json_string<'a>(value: &'a JsonValue, names: &[&str]) -> Result<&'a str> {
    names
        .iter()
        .find_map(|name| value.get(*name).and_then(JsonValue::as_str))
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| anyhow::anyhow!("required JSON string is missing"))
}

fn optional_json_string(value: &JsonValue, names: &[&str]) -> Option<String> {
    names
        .iter()
        .find_map(|name| value.get(*name).and_then(JsonValue::as_str))
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
}

fn optional_json_bool(value: &JsonValue, names: &[&str]) -> Option<bool> {
    names
        .iter()
        .find_map(|name| value.get(*name).and_then(JsonValue::as_bool))
}

fn optional_json_usize(value: &JsonValue, names: &[&str]) -> Option<usize> {
    names
        .iter()
        .find_map(|name| value.get(*name).and_then(JsonValue::as_u64))
        .and_then(|value| usize::try_from(value).ok())
}

fn nested_json<'a>(value: &'a JsonValue, names: &[&str]) -> Option<&'a JsonValue> {
    names.iter().find_map(|name| value.get(*name))
}

fn optional_path(value: &JsonValue, names: &[&str]) -> Option<PathBuf> {
    optional_json_string(value, names).map(PathBuf::from)
}

fn write_bstr(value: *mut BSTR, text: &str) -> HRESULT {
    if value.is_null() {
        return E_POINTER;
    }

    let utf16 = text.encode_utf16().collect::<Vec<_>>();
    let Ok(len) = u32::try_from(utf16.len()) else {
        return E_FAIL;
    };

    // SAFETY: The UTF-16 buffer is valid for len code units and SysAllocStringLen copies it.
    let bstr = unsafe { SysAllocStringLen(utf16.as_ptr(), len) };
    if bstr.is_null() && len != 0 {
        return E_FAIL;
    }

    // SAFETY: The out pointer was checked above and receives ownership of the BSTR for the caller to free.
    unsafe {
        *value = bstr;
    }

    S_OK
}

unsafe fn create_package_manager(riid: *const GUID, object: *mut *mut c_void) -> HRESULT {
    if object.is_null() {
        return E_POINTER;
    }

    // SAFETY: The out pointer was checked above and is owned by the caller for this call.
    unsafe {
        *object = ptr::null_mut();
    }

    let package_manager = match PackageManagerObject::new() {
        Ok(package_manager) => package_manager,
        Err(_) => return E_FAIL,
    };

    let raw = Box::into_raw(package_manager);
    // SAFETY: raw is a newly allocated PackageManagerObject with a valid vtable.
    let hr = unsafe { package_manager_query_interface(raw.cast::<c_void>(), riid, object) };
    // SAFETY: Release balances the initial reference held by raw; successful QueryInterface has added caller's ref.
    unsafe {
        package_manager_release(raw.cast::<c_void>());
    }

    hr
}

/// Creates a Pinget package manager COM object for direct, registration-free activation.
///
/// # Safety
///
/// `riid` must be either null only when the function is expected to fail with `E_POINTER`, or point to a valid
/// interface GUID for the duration of the call. `object` must point to writable storage for one COM interface pointer.
/// On success, the caller owns one reference and must release it with `IUnknown::Release`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn PingetCreatePackageManager(riid: *const GUID, object: *mut *mut c_void) -> HRESULT {
    // SAFETY: create_package_manager validates all caller-provided pointers.
    unsafe { create_package_manager(riid, object) }
}

/// Returns the class factory for the Pinget package manager class.
///
/// # Safety
///
/// `clsid` and `riid` must be either null only when the function is expected to fail with `E_POINTER`, or point to
/// valid GUID values for the duration of the call. `object` must point to writable storage for one COM interface
/// pointer. On success, the caller owns one reference and must release it with `IUnknown::Release`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllGetClassObject(
    clsid: *const GUID,
    riid: *const GUID,
    object: *mut *mut c_void,
) -> HRESULT {
    if clsid.is_null() || object.is_null() {
        return E_POINTER;
    }

    // SAFETY: clsid is non-null and points to a GUID provided by the COM caller.
    let requested_clsid = unsafe { *clsid };
    if !guid_eq(&requested_clsid, &CLSID_PACKAGE_MANAGER) {
        // SAFETY: object is non-null and should be cleared on failure.
        unsafe {
            *object = ptr::null_mut();
        }
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    let factory = Box::new(ClassFactoryObject::new());
    let raw = Box::into_raw(factory);
    // SAFETY: raw is a newly allocated ClassFactoryObject with a valid vtable.
    let hr = unsafe { class_factory_query_interface(raw.cast::<c_void>(), riid, object) };
    // SAFETY: Release balances the initial reference held by raw; successful QueryInterface has added caller's ref.
    unsafe {
        class_factory_release(raw.cast::<c_void>());
    }

    hr
}

#[unsafe(no_mangle)]
pub extern "system" fn DllCanUnloadNow() -> HRESULT {
    if ACTIVE_OBJECTS.load(Ordering::Acquire) == 0 && SERVER_LOCKS.load(Ordering::Acquire) == 0 {
        S_OK
    } else {
        S_FALSE
    }
}

#[unsafe(no_mangle)]
pub extern "system" fn PingetPackageManagerClsid() -> GUID {
    CLSID_PACKAGE_MANAGER
}

#[unsafe(no_mangle)]
pub extern "system" fn PingetPackageManagerIid() -> GUID {
    IID_IPACKAGE_MANAGER
}
