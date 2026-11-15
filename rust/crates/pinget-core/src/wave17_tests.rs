//! Wave 17 (installed packages, ARP, correlation, name normalization) regression tests.

use super::name_normalization::{Architecture, normalize_name, normalize_publisher};
use super::*;

fn installed(name: &str, local_id: &str) -> InstalledPackage {
    InstalledPackage {
        name: name.to_owned(),
        local_id: local_id.to_owned(),
        installed_version: "1.0.0".to_owned(),
        publisher: None,
        scope: None,
        installer_category: None,
        install_location: None,
        winget_package_identifier: None,
        winget_source_identifier: None,
        package_family_names: Vec::new(),
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
        correlated: None,
        installed_version_canonical: false,
        correlated_requires_explicit_upgrade: false,
        correlated_lacks_compatible_installer: false,
    }
}

fn catalog_match(id: &str, name: &str) -> SearchMatch {
    SearchMatch {
        source_name: "winget".to_owned(),
        source_kind: SourceKind::PreIndexed,
        id: id.to_owned(),
        name: name.to_owned(),
        moniker: None,
        version: Some("2.0.0".to_owned()),
        channel: None,
        match_criteria: None,
    }
}

fn memory_db(sql: &str) -> Connection {
    let connection = Repository::open_sqlite_connection(PathBuf::from(":memory:")).expect("open in-memory db");
    execute_batch_sql(&connection, sql).expect("seed schema");
    connection
}

// ── W17-1: the SAP Business Objects short-circuit still case-folds ──

#[test]
fn sap_business_objects_names_are_case_folded() {
    // winget folds every name before normalizing it, so the catalog stores
    // the SAP short-circuit value lowercase.
    assert_eq!(
        normalize_name("BOE.WebTier-14.0.4-CORE-64").name,
        "boe.webtier-14.0.4-core-64"
    );
}

// ── W17-2: NFKC normalization before the regexes ──

#[cfg(windows)]
#[test]
fn compatibility_characters_are_nfkc_normalized() {
    // `™` decomposes to `TM` and full-width letters to ASCII under NFKC.
    assert_eq!(normalize_name("Contoso™ Studio").name, "contosotmstudio");
    assert_eq!(normalize_name("Ｃｏｎｔｏｓｏ Studio").name, "contosostudio");
    assert_eq!(normalize_publisher("Ｃｏｎｔｏｓｏ Ltd"), "contoso");
}

// ── W17-3: full case folding (ß → ss, ς → σ) ──

#[test]
fn normalized_values_use_full_case_folding() {
    assert_eq!(normalize_name("Straße Navigator").name, "strassenavigator");
    assert_eq!(normalize_publisher("Großmann GmbH"), "grossmann");
    assert_eq!(normalize_name("ΚΟΣΜΟΣ").name, "κοσμοσ");
}

// ── W17-4: the `@@` suffix is searched from the fourth UTF-16 unit ──

#[test]
fn at_at_suffix_is_searched_from_the_fourth_character() {
    // winget: `find(L"@@", 3)` — an early `@@` does not hide a later one...
    assert_eq!(normalize_name("AB@@CD@@Internal").name, "abcd");
    // ...and positions count UTF-16 units, not UTF-8 bytes.
    assert_eq!(normalize_name("Äb@@cd").name, "äbcd");
}

// ── W17-5: architecture-qualified normalized-name correlation first ──

#[test]
fn normalized_identity_prefers_architecture_qualified_name() {
    // The index stores both `contosotool` and `contosotool(X64)` /
    // `contosotool(X86)` for ARP DisplayNames carrying an architecture.
    let connection = memory_db(
        "CREATE TABLE norm_names2 (norm_name TEXT, package INT64);\n\
         CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);\n\
         INSERT INTO norm_names2 VALUES ('contosotool', 100), ('contosotool(X64)', 100), \
                                        ('contosotool', 200), ('contosotool(X86)', 200);\n\
         INSERT INTO norm_publishers2 VALUES ('contoso', 100), ('contoso', 200);",
    );

    let x64 = normalize_name("Contoso Tool (x64)");
    assert_eq!(x64.architecture, Architecture::X64);
    assert_eq!(
        lookup_installed_normalized_identity(&connection, &x64, "contoso").expect("query"),
        Some(100)
    );
    let x86 = normalize_name("Contoso Tool (x86)");
    assert_eq!(
        lookup_installed_normalized_identity(&connection, &x86, "contoso").expect("query"),
        Some(200)
    );
    // Without an architecture the plain name stays ambiguous.
    let plain = normalize_name("Contoso Tool");
    assert_eq!(
        lookup_installed_normalized_identity(&connection, &plain, "contoso").expect("query"),
        None
    );
}

#[test]
fn normalized_identity_falls_back_to_plain_name_without_architecture_rows() {
    let connection = memory_db(
        "CREATE TABLE norm_names2 (norm_name TEXT, package INT64);\n\
         CREATE TABLE norm_publishers2 (norm_publisher TEXT, package INT64);\n\
         INSERT INTO norm_names2 VALUES ('contosotool', 100);\n\
         INSERT INTO norm_publishers2 VALUES ('contoso', 100);",
    );
    let x64 = normalize_name("Contoso Tool (x64)");
    assert_eq!(
        lookup_installed_normalized_identity(&connection, &x64, "contoso").expect("query"),
        Some(100)
    );
}

// ── W17-6: installed.db without every identity table pair ──

#[test]
fn installed_db_lookup_skips_missing_identity_tables() {
    // A tracking DB from a schema before 1.6 has no `upgradecodes` tables.
    let connection = memory_db(
        "CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL);\n\
         CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT NOT NULL);\n\
         CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64 NOT NULL, name INT64 NOT NULL);\n\
         CREATE TABLE productcodes (rowid INTEGER PRIMARY KEY, productcode TEXT NOT NULL);\n\
         CREATE TABLE productcodes_map (manifest INT64 NOT NULL, productcode INT64 NOT NULL);\n\
         INSERT INTO ids VALUES (1, 'Contoso.Tool');\n\
         INSERT INTO names VALUES (1, 'Contoso Tool');\n\
         INSERT INTO manifest VALUES (10, 1, 1);\n\
         INSERT INTO productcodes VALUES (20, '{11111111-2222-3333-4444-555555555555}');\n\
         INSERT INTO productcodes_map VALUES (10, 20);",
    );
    assert!(installed_db_identity_tables_present(&connection).expect("tables"));

    let mut package = installed(
        "Contoso Tool",
        r"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}",
    );
    package.package_family_names = vec!["Contoso.Tool_abcdefghijklm".to_owned()];
    package.upgrade_codes = vec!["{99999999-8888-7777-6666-555555555555}".to_owned()];
    package.product_codes = vec!["{11111111-2222-3333-4444-555555555555}".to_owned()];

    let (id, _, matched_by) = lookup_installed_db_identity_match(&connection, &package)
        .expect("missing tables must not fail the lookup")
        .expect("product code match");
    assert_eq!(id, "Contoso.Tool");
    assert_eq!(matched_by, "ProductCode");
}

// ── W17-7: list queries match system reference strings exactly ──

#[test]
fn list_query_matches_package_family_name_and_codes() {
    let mut msix = installed("Contoso Terminal", r"MSIX\Contoso.Terminal_1.2.3.0_x64__abcdefghijklm");
    msix.package_family_names = vec!["Contoso.Terminal_abcdefghijklm".to_owned()];
    let query = ListQuery {
        query: Some("contoso.terminal_abcdefghijklm".to_owned()),
        ..ListQuery::default()
    };
    assert!(list_package_matches(&msix, &query));

    let mut msi = installed(
        "Contoso Agent",
        r"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}",
    );
    msi.product_codes = vec!["{11111111-2222-3333-4444-555555555555}".to_owned()];
    msi.upgrade_codes = vec!["{99999999-8888-7777-6666-555555555555}".to_owned()];
    let exact_product_code = ListQuery {
        query: Some("{11111111-2222-3333-4444-555555555555}".to_owned()),
        exact: true,
        ..ListQuery::default()
    };
    assert!(list_package_matches(&msi, &exact_product_code));
    let upgrade_code = ListQuery {
        query: Some("{99999999-8888-7777-6666-555555555555}".to_owned()),
        ..ListQuery::default()
    };
    assert!(list_package_matches(&msi, &upgrade_code));
}

// ── W17-8: `--name` also matches the correlated catalog name ──

#[test]
fn list_name_filter_matches_correlated_catalog_name() {
    let mut package = installed("Contoso Browser (x64 en-US)", r"ARP\Machine\X64\Contoso Browser");
    package.correlated = Some(catalog_match("Contoso.Browser", "Contoso Browser"));
    let query = ListQuery {
        name: Some("Contoso Browser".to_owned()),
        exact: true,
        ..ListQuery::default()
    };
    assert!(list_package_matches(&package, &query));

    let other = ListQuery {
        name: Some("Fabrikam".to_owned()),
        ..ListQuery::default()
    };
    assert!(!list_package_matches(&package, &other));
}

// ── W17-9 / W17-10: registry enumeration parity with WinGet ──

#[cfg(windows)]
struct SandboxKey {
    path: String,
}

#[cfg(windows)]
impl SandboxKey {
    fn new(label: &str) -> Self {
        use std::time::{SystemTime, UNIX_EPOCH};
        let path = format!(
            r"Software\PingetWave17Tests\{label}_{}_{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).expect("time").as_nanos()
        );
        RegKey::predef(HKEY_CURRENT_USER)
            .create_subkey(&path)
            .expect("create sandbox key");
        Self { path }
    }

    fn create(&self, relative: &str) -> RegKey {
        RegKey::predef(HKEY_CURRENT_USER)
            .create_subkey(format!(r"{}\{relative}", self.path))
            .expect("create sandbox subkey")
            .0
    }

    fn root(&self) -> RegKey {
        RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey_with_flags(&self.path, KEY_READ)
            .expect("open sandbox key")
    }
}

#[cfg(windows)]
impl Drop for SandboxKey {
    fn drop(&mut self) {
        let _ = RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(&self.path);
    }
}

#[cfg(windows)]
#[test]
fn msix_package_without_display_name_uses_package_name() {
    let sandbox = SandboxKey::new("msix");
    let full_name = "Contoso.NoDisplayName_1.2.3.0_x64__abcdefghijklm";
    let package = sandbox.create(&format!(r"{APPMODEL_PACKAGES_PATH}\{full_name}"));
    package
        .set_value(
            "PackageRootFolder",
            &format!(r"C:\Program Files\WindowsApps\{full_name}"),
        )
        .expect("root folder");
    drop(package);

    let mut packages = Vec::new();
    let mut seen = BTreeSet::new();
    collect_appmodel_packages(&mut packages, &mut seen, sandbox.root(), "User", KEY_READ).expect("collect");

    assert_eq!(packages.len(), 1, "the package must still be listed");
    assert_eq!(packages[0].name, "Contoso.NoDisplayName");
    assert_eq!(packages[0].installed_version, "1.2.3.0");
    assert_eq!(
        packages[0].package_family_names,
        vec!["Contoso.NoDisplayName_abcdefghijklm"]
    );
}

#[cfg(windows)]
#[test]
fn arp_entries_with_parent_key_name_are_listed() {
    let sandbox = SandboxKey::new("arp");
    let uninstall = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    let update = sandbox.create(&format!(r"{uninstall}\ContosoUpdate"));
    update
        .set_value("DisplayName", &"Update for Contoso Suite (KB1234567)".to_owned())
        .expect("name");
    update.set_value("DisplayVersion", &"1.0".to_owned()).expect("version");
    update
        .set_value("ParentKeyName", &"ContosoSuite".to_owned())
        .expect("parent");
    drop(update);

    let hidden = sandbox.create(&format!(r"{uninstall}\ContosoComponent"));
    hidden
        .set_value("DisplayName", &"Contoso Component".to_owned())
        .expect("name");
    hidden.set_value("SystemComponent", &1u32).expect("system component");
    drop(hidden);

    let mut packages = Vec::new();
    let mut seen = BTreeSet::new();
    collect_uninstall_view(
        &mut packages,
        &mut seen,
        &HashMap::new(),
        sandbox.root(),
        "User",
        "X64",
        KEY_READ,
    )
    .expect("collect");

    let names = packages.iter().map(|package| package.name.as_str()).collect::<Vec<_>>();
    assert_eq!(names, vec!["Update for Contoso Suite (KB1234567)"]);
    assert_eq!(packages[0].local_id, r"ARP\User\X64\ContosoUpdate");
}
