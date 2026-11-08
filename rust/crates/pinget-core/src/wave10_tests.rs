//! Wave 10 (installed packages, correlation, pins, name normalization) regression tests.

use std::io::{BufRead as _, BufReader, Read as _, Write as _};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering as AtomicOrdering};
use std::thread::{self, JoinHandle};

use super::name_normalization::{Architecture, normalize_name, normalize_publisher};
use super::*;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let root = std::env::temp_dir().join(format!(
        "pinget-rs-wave10-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ));
    let _ = fs::remove_dir_all(&root);
    fs::create_dir_all(&root).expect("create temp root");
    root
}

fn source(name: &str, kind: SourceKind, arg: String) -> SourceRecord {
    SourceRecord {
        name: name.to_owned(),
        kind,
        arg,
        identifier: name.to_owned(),
        trust_level: "Trusted".to_owned(),
        explicit: false,
        priority: 0,
        last_update: None,
        source_version: None,
        etag: None,
        last_modified: None,
    }
}

fn open_repository(app_root: &Path, sources: Vec<SourceRecord>) -> Repository {
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.to_path_buf())).expect("open repository");
    repository.store.sources = sources;
    repository.save_store().expect("save test sources");
    repository
}

// ── R10-2: pinning.db carries WinGet's metadata, pin table and index ──

#[test]
fn add_pin_creates_winget_pinning_schema() {
    let root = temp_root("pin-schema");
    let repository = open_repository(&root, Vec::new());
    repository
        .add_pin("Contoso.Tool", "1.0.0", "winget", PinType::Pinning)
        .expect("add pin");

    let conn = Repository::open_sqlite_connection(pins_db_path(&root)).expect("open pin db");
    let metadata = query_rows(
        &conn,
        "SELECT name, value FROM metadata ORDER BY name",
        Vec::new(),
        |row| Ok((row_string(row, 0)?, row_string(row, 1)?)),
    )
    .expect("read metadata");
    assert_eq!(
        metadata,
        vec![
            ("majorVersion".to_owned(), "1".to_owned()),
            ("minorVersion".to_owned(), "0".to_owned())
        ]
    );

    let indexes = query_rows(
        &conn,
        "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'pin'",
        Vec::new(),
        |row| row_string(row, 0),
    )
    .expect("read indexes");
    assert!(indexes.iter().any(|name| name == "pin_index"), "{indexes:?}");

    let columns = query_rows(&conn, "PRAGMA table_info(pin)", Vec::new(), |row| row_string(row, 1)).expect("columns");
    assert_eq!(columns, vec!["package_id", "source_id", "type", "version"]);
    drop(conn);

    // INSERT OR REPLACE keyed by the unique (package_id, source_id) index.
    repository
        .add_pin("Contoso.Tool", "*", "winget", PinType::Blocking)
        .expect("replace pin");
    let pins = repository.list_pins(None).expect("list pins");
    assert_eq!(pins.len(), 1);
    assert_eq!(pins[0].pin_type, PinType::Blocking);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn add_pin_upgrades_legacy_pinget_pin_table_with_metadata() {
    let root = temp_root("pin-legacy");
    let repository = open_repository(&root, Vec::new());
    {
        let conn = Repository::open_sqlite_connection(pins_db_path(&root)).expect("open pin db");
        execute_batch_sql(
            &conn,
            "CREATE TABLE pin (
                package_id TEXT NOT NULL,
                version TEXT NOT NULL DEFAULT '*',
                source_id TEXT NOT NULL DEFAULT '',
                type INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (package_id, source_id)
            );
            INSERT INTO pin (package_id, version, source_id, type) VALUES ('Old.Pin', '*', 'winget', 4);",
        )
        .expect("seed legacy table");
    }

    repository
        .add_pin("Contoso.Tool", "1.0.0", "winget", PinType::Pinning)
        .expect("add pin");

    let pins = repository.list_pins(None).expect("list pins");
    assert_eq!(pins.len(), 2);
    let conn = Repository::open_sqlite_connection(pins_db_path(&root)).expect("open pin db");
    let major = query_rows(
        &conn,
        "SELECT value FROM metadata WHERE name = 'majorVersion'",
        Vec::new(),
        |row| row_string(row, 0),
    )
    .expect("read metadata");
    assert_eq!(major, vec!["1".to_owned()]);
    drop(conn);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn add_pin_writes_into_winget_created_pinning_db() {
    let root = temp_root("pin-winget-db");
    let path = pins_db_path(&root);
    {
        let db = pollster::block_on(
            turso::Builder::new_local(&path.to_string_lossy())
                .experimental_without_rowid(true)
                .build(),
        )
        .expect("build");
        let conn = db.connect().expect("connect");
        execute_batch_sql(
            &conn,
            "CREATE TABLE [metadata]([name] TEXT PRIMARY KEY NOT NULL,[value] TEXT NOT NULL) WITHOUT ROWID;
             INSERT INTO [metadata] VALUES ('majorVersion', '1');
             INSERT INTO [metadata] VALUES ('minorVersion', '0');
             CREATE TABLE pin (package_id TEXT NOT NULL, source_id TEXT NOT NULL, type INT64 NOT NULL, version TEXT NOT NULL);
             CREATE UNIQUE INDEX pin_index ON pin (package_id, source_id);
             INSERT INTO pin VALUES ('Contoso.A', 'winget', 4, '*');",
        )
        .expect("seed");
        conn.cacheflush().expect("flush");
    }
    let repository = open_repository(&root, Vec::new());

    repository
        .add_pin("Contoso.B", "*", "winget", PinType::Blocking)
        .expect("add pin to a WinGet-created database");

    let mut ids = repository
        .list_pins(None)
        .expect("list pins")
        .into_iter()
        .map(|pin| pin.package_id)
        .collect::<Vec<_>>();
    ids.sort();
    assert_eq!(ids, vec!["Contoso.A", "Contoso.B"]);
    let _ = fs::remove_dir_all(root);
}

// ── R10-13: pin source filters are case-insensitive ──

#[test]
fn pin_source_filters_ignore_case() {
    let root = temp_root("pin-case");
    let repository = open_repository(&root, Vec::new());
    repository
        .add_pin("Contoso.Tool", "1.0.0", "winget", PinType::Pinning)
        .expect("add pin");
    repository
        .add_pin("Other.Tool", "1.0.0", "msstore", PinType::Pinning)
        .expect("add pin");

    let listed = repository.list_pins(Some("WinGet")).expect("list pins");
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].package_id, "Contoso.Tool");

    assert!(
        repository
            .remove_pin("contoso.tool", Some("WINGET"))
            .expect("remove pin")
    );
    repository
        .add_pin("Contoso.Tool", "1.0.0", "winget", PinType::Pinning)
        .expect("re-add pin");

    repository.reset_pins(Some("WINGET")).expect("reset pins");
    let remaining = repository.list_pins(None).expect("list pins");
    assert_eq!(remaining.len(), 1);
    assert_eq!(remaining[0].source_id, "msstore");
    let _ = fs::remove_dir_all(root);
}

// ── R10-4: --include-pinned relaxes only Pinning pins in the list filter ──

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

fn pin(pin_type: PinType, version: &str) -> PinRecord {
    PinRecord {
        package_id: "Contoso.Tool".to_owned(),
        version: version.to_owned(),
        source_id: "winget".to_owned(),
        pin_type,
    }
}

#[test]
fn include_pinned_list_filter_still_applies_blocking_and_gating() {
    let item = pinned_item("2.0.0");
    assert!(!upgrade_blocked_by_pin(&item, &[pin(PinType::Pinning, "1.0.0")], true));
    assert!(upgrade_blocked_by_pin(&item, &[pin(PinType::Pinning, "1.0.0")], false));
    assert!(upgrade_blocked_by_pin(&item, &[pin(PinType::Blocking, "*")], true));
    assert!(upgrade_blocked_by_pin(&item, &[pin(PinType::Gating, "1.*")], true));
    assert!(!upgrade_blocked_by_pin(&item, &[pin(PinType::Gating, "2.*")], true));
}

// ── R10-5: ARP version fallbacks ──

#[test]
fn arp_version_follows_winget_determine_version_order() {
    let display = ArpVersionValues {
        display_version: Some("3.1.4".to_owned()),
        version_major: Some(5),
        version_minor: Some(2),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&display), "3.1.4");

    let major_minor = ArpVersionValues {
        version_major: Some(5),
        version_minor: Some(2),
        major_version: Some(7),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&major_minor), "5.2");

    let only_minor = ArpVersionValues {
        version_minor: Some(9),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&only_minor), "0.9");

    let zero_pair_then_alt = ArpVersionValues {
        version_major: Some(0),
        version_minor: Some(0),
        major_version: Some(7),
        minor_version: Some(1),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&zero_pair_then_alt), "7.1");

    let packed = ArpVersionValues {
        version: Some(0x0A02_1234),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&packed), "10.2.4660");

    assert_eq!(determine_arp_version(&ArpVersionValues::default()), "Unknown");
    let zero_version = ArpVersionValues {
        version: Some(0),
        display_version: Some(String::new()),
        ..ArpVersionValues::default()
    };
    assert_eq!(determine_arp_version(&zero_version), "Unknown");
}

// ── R10-6: explicit-upgrade flags come from the upgrade target's manifest ──

fn version_entry(version: &str) -> PackageVersionDataEntry {
    PackageVersionDataEntry {
        version: version.to_owned(),
        manifest_relative_path: format!("manifests/c/Contoso/Tool/{version}/Contoso.Tool.yaml"),
        manifest_hash: String::new(),
        arp_min_version: None,
        arp_max_version: None,
    }
}

#[test]
fn upgrade_target_entry_matches_correlated_version_else_highest() {
    let entries = vec![version_entry("1.0.0"), version_entry("2.10.0"), version_entry("2.9.0")];

    let highest = upgrade_target_version_entry(&entries, None).expect("entry");
    assert_eq!(highest.version, "2.10.0");

    let matched = upgrade_target_version_entry(&entries, Some("2.9")).expect("entry");
    assert_eq!(matched.version, "2.9.0");

    let unmatched = upgrade_target_version_entry(&entries, Some("3.0.0")).expect("entry");
    assert_eq!(unmatched.version, "2.10.0");

    assert!(upgrade_target_version_entry(&[], Some("1.0.0")).is_none());
}

// ── R10-8 / R10-10: name normalization parity with WinGet ──

#[test]
fn normalizer_strips_arm_architectures() {
    let arm64 = normalize_name("Contoso Tool ARM64");
    assert_eq!(arm64.name, "contosotool");
    assert_eq!(arm64.architecture, Architecture::Arm64);

    let aarch64 = normalize_name("Contoso Tool (AArch64)");
    assert_eq!(aarch64.name, "contosotool");
    assert_eq!(aarch64.architecture, Architecture::Arm64);

    let arm32 = normalize_name("Contoso Tool arm32 Edition");
    assert_eq!(arm32.name, "contosotool");
    assert_eq!(arm32.architecture, Architecture::Arm);

    // x64 still wins over the ARM checks, as in WinGet's order.
    assert_eq!(normalize_name("Contoso Tool x64").architecture, Architecture::X64);
    // A bare "ARM" is not an architecture token.
    assert_eq!(normalize_name("Contoso Arm Tool").architecture, Architecture::Unknown);
}

#[test]
fn normalizer_keeps_software_token() {
    assert_eq!(normalize_publisher("Acme Software Inc"), "acmesoftware");
    assert_eq!(normalize_publisher("SweetScape Software"), "sweetscapesoftware");
    assert_eq!(normalize_name("Acme Software Suite").name, "acmesoftwaresuite");
}

// ── R10-12: Windows directory comes from %SystemRoot% ──

#[test]
fn windows_directory_test_uses_the_system_root() {
    assert!(is_path_under_windows_directory(
        r"D:\Windows\SystemApps\Microsoft.Windows.Search",
        r"D:\Windows"
    ));
    assert!(is_path_under_windows_directory(r"c:\winnt\system32\x", r"C:\WINNT\"));
    assert!(!is_path_under_windows_directory(
        r"C:\Windows\SystemApps\x",
        r"D:\Windows"
    ));
    assert!(!is_path_under_windows_directory(
        r"C:\WindowsApps\Contoso",
        r"C:\Windows"
    ));
    assert!(!is_path_under_windows_directory(r"C:\Windows\x", ""));
}

#[cfg(windows)]
#[test]
fn windows_directory_is_detected() {
    let windows_dir = windows_directory().expect("windows directory");
    assert!(is_windows_system_path(&format!(r"{windows_dir}\SystemApps\Contoso")));
    assert!(!is_windows_system_path(r"Z:\NotWindows\Contoso"));
}

// ── R10-3: installed.db REST correlation resolves the available version ──

/// Minimal HTTP/1.1 stub that reads each full request (headers and body) before answering.
struct StubServer {
    addr: SocketAddr,
    running: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl StubServer {
    fn start(routes: Vec<(&str, &str)>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind stub server");
        let addr = listener.local_addr().expect("stub server address");
        let routes: HashMap<String, String> = routes
            .into_iter()
            .map(|(path, body)| (path.to_owned(), body.to_owned()))
            .collect();
        let running = Arc::new(AtomicBool::new(true));
        let thread_running = Arc::clone(&running);
        let handle = thread::spawn(move || {
            while thread_running.load(AtomicOrdering::SeqCst) {
                let Ok((stream, _)) = listener.accept() else {
                    continue;
                };
                let Some(path) = read_request_path(&stream) else {
                    continue;
                };
                let (status, body) = match routes.get(&path) {
                    Some(body) => (200, body.clone()),
                    None => (404, String::new()),
                };
                let response = format!(
                    "HTTP/1.1 {status} Stub\r\nContent-Length: {}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let mut stream = stream;
                let _ = stream.write_all(response.as_bytes());
                let _ = stream.flush();
            }
        });
        Self {
            addr,
            running,
            handle: Some(handle),
        }
    }

    fn url(&self, path: &str) -> String {
        format!("http://{}{path}", self.addr)
    }
}

fn read_request_path(stream: &TcpStream) -> Option<String> {
    let mut reader = BufReader::new(stream);
    let mut request_line = String::new();
    reader.read_line(&mut request_line).ok()?;
    let path = request_line.split_whitespace().nth(1)?.to_owned();
    let mut content_length = 0usize;
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).ok()? == 0 {
            break;
        }
        let line = line.trim_end();
        if line.is_empty() {
            break;
        }
        if let Some((name, value)) = line.split_once(':')
            && name.trim().eq_ignore_ascii_case("content-length")
        {
            content_length = value.trim().parse().unwrap_or(0);
        }
    }
    let mut body = vec![0u8; content_length];
    reader.read_exact(&mut body).ok()?;
    Some(path)
}

impl Drop for StubServer {
    fn drop(&mut self) {
        self.running.store(false, AtomicOrdering::SeqCst);
        let _ = TcpStream::connect(self.addr);
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

const REST_INFORMATION_JSON: &str = r#"{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}"#;
const REST_SEARCH_JSON: &str = r#"{"Data":[{"PackageIdentifier":"Contoso.Store","PackageName":"Contoso Store App","Publisher":"Contoso","Versions":[{"PackageVersion":"1.0.0"},{"PackageVersion":"2.5.0"}]}]}"#;

fn rest_correlated_package(source_name: &str) -> InstalledPackage {
    InstalledPackage {
        name: "Contoso Store App".to_owned(),
        local_id: r"MSIX\Contoso.Store_1.0.0.0_x64__abc".to_owned(),
        installed_version: "1.0.0".to_owned(),
        publisher: None,
        scope: None,
        installer_category: Some("msix".to_owned()),
        install_location: None,
        winget_package_identifier: None,
        winget_source_identifier: None,
        package_family_names: vec!["Contoso.Store_abc".to_owned()],
        product_codes: Vec::new(),
        upgrade_codes: Vec::new(),
        correlated: Some(SearchMatch {
            source_name: source_name.to_owned(),
            source_kind: SourceKind::Rest,
            id: "Contoso.Store".to_owned(),
            name: "Contoso Store App".to_owned(),
            moniker: None,
            version: None,
            channel: None,
            match_criteria: Some("PackageFamilyName".to_owned()),
        }),
        installed_version_canonical: false,
        correlated_requires_explicit_upgrade: false,
        correlated_lacks_compatible_installer: false,
    }
}

#[test]
fn installed_db_rest_correlation_gets_latest_available_version() {
    let server = StubServer::start(vec![
        ("/api/information", REST_INFORMATION_JSON),
        ("/api/manifestSearch", REST_SEARCH_JSON),
    ]);
    let root = temp_root("rest-version");
    let mut repository = open_repository(&root, vec![source("store", SourceKind::Rest, server.url("/api"))]);
    let mut installed = vec![rest_correlated_package("Store")];

    let warnings = repository.resolve_rest_correlated_versions(&mut installed);

    assert!(warnings.is_empty(), "{warnings:?}");
    let correlated = installed[0].correlated.as_ref().expect("correlated");
    assert_eq!(correlated.version.as_deref(), Some("2.5.0"));
    assert!(installed_package_has_upgrade(&installed[0]));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn installed_db_rest_correlation_reports_unreachable_source() {
    let server = StubServer::start(vec![("/api/information", REST_INFORMATION_JSON)]);
    let root = temp_root("rest-version-fail");
    let mut repository = open_repository(&root, vec![source("store", SourceKind::Rest, server.url("/api"))]);
    let mut installed = vec![rest_correlated_package("store"), rest_correlated_package("store")];

    let warnings = repository.resolve_rest_correlated_versions(&mut installed);

    assert_eq!(warnings.len(), 1, "one warning per failing source: {warnings:?}");
    assert!(installed[0].correlated.as_ref().expect("correlated").version.is_none());
    let _ = fs::remove_dir_all(root);
}
