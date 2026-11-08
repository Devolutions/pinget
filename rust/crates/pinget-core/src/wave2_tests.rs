//! Wave 2 (package sources) regression tests.

use std::io::{Read as _, Write as _};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering as AtomicOrdering};
use std::thread::{self, JoinHandle};

use super::*;

const REST_INFORMATION_JSON: &str = r#"{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}"#;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "pinget-rs-wave2-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ))
}

/// Minimal HTTP/1.1 stub: answers each path from a fixed route table, or with the default.
struct StubServer {
    addr: SocketAddr,
    running: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl StubServer {
    fn start(routes: Vec<(&str, u16, &str)>, default_status: u16) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind stub server");
        let addr = listener.local_addr().expect("stub server address");
        let routes: HashMap<String, (u16, String)> = routes
            .into_iter()
            .map(|(path, status, body)| (path.to_owned(), (status, body.to_owned())))
            .collect();
        let running = Arc::new(AtomicBool::new(true));
        let thread_running = Arc::clone(&running);
        let handle = thread::spawn(move || {
            while thread_running.load(AtomicOrdering::SeqCst) {
                let Ok((mut stream, _)) = listener.accept() else {
                    continue;
                };
                let mut buffer = [0u8; 4096];
                let read = stream.read(&mut buffer).unwrap_or(0);
                let request = String::from_utf8_lossy(&buffer[..read]);
                let path = request
                    .lines()
                    .next()
                    .and_then(|line| line.split_whitespace().nth(1))
                    .unwrap_or("/")
                    .to_owned();
                let (status, body) = routes.get(&path).cloned().unwrap_or((default_status, String::new()));
                let response = format!(
                    "HTTP/1.1 {status} Stub\r\nContent-Length: {}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = stream.write_all(response.as_bytes());
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

impl Drop for StubServer {
    fn drop(&mut self) {
        self.running.store(false, AtomicOrdering::SeqCst);
        let _ = TcpStream::connect(self.addr);
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

fn source(name: &str, kind: SourceKind, arg: String, explicit: bool) -> SourceRecord {
    SourceRecord {
        name: name.to_owned(),
        kind,
        arg,
        identifier: name.to_owned(),
        trust_level: "Trusted".to_owned(),
        explicit,
        priority: 0,
        last_update: None,
        source_version: None,
        etag: None,
        last_modified: None,
    }
}

fn open_private_repository(app_root: &Path, sources: Vec<SourceRecord>) -> Repository {
    fs::create_dir_all(app_root).expect("create app root");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.to_path_buf())).expect("open repository");
    repository.store.sources = sources;
    repository.save_store().expect("save test sources");
    repository
}

// ── S1 / S5: REST search request shape ──

#[test]
fn rest_single_query_puts_identity_fields_in_inclusions() {
    let query = PackageQuery {
        query: Some("Contoso.App".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&query, &RestInformation::default(), SearchSemantics::Single).expect("body");

    let inclusions = body["Inclusions"].as_array().expect("inclusions");
    let fields: Vec<_> = inclusions
        .iter()
        .map(|item| item["PackageMatchField"].as_str().unwrap_or_default())
        .collect();
    assert_eq!(fields, ["PackageIdentifier", "PackageName", "Moniker"]);
    // Wave 9 (R9-5): single lookups are CaseInsensitive unless --exact, like WinGet.
    assert!(
        inclusions
            .iter()
            .all(|item| item["RequestMatch"]["MatchType"] == "CaseInsensitive"
                && item["RequestMatch"]["KeyWord"] == "Contoso.App")
    );
    assert!(
        body.get("Filters").is_none(),
        "identity fields must not be ANDed as Filters"
    );
    assert!(body.get("Query").is_none());
}

#[test]
fn rest_single_query_keeps_required_market_as_the_only_filter() {
    let query = PackageQuery {
        query: Some("Contoso.App".to_owned()),
        ..PackageQuery::default()
    };
    let info = RestInformation {
        required_package_match_fields: vec!["Market".to_owned()],
        ..RestInformation::default()
    };
    let body = build_rest_search_body(&query, &info, SearchSemantics::Single).expect("body");

    assert_eq!(body["Inclusions"].as_array().expect("inclusions").len(), 3);
    let filters = body["Filters"].as_array().expect("filters");
    assert_eq!(filters.len(), 1);
    assert_eq!(filters[0]["PackageMatchField"], "Market");
}

#[test]
fn rest_single_id_filter_uses_case_insensitive_match() {
    let query = PackageQuery {
        id: Some("Contoso.App".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&query, &RestInformation::default(), SearchSemantics::Single).expect("body");
    let filters = body["Filters"].as_array().expect("filters");
    assert_eq!(filters[0]["PackageMatchField"], "PackageIdentifier");
    // Wave 9 (R9-5): CaseInsensitive unless --exact, like WinGet.
    assert_eq!(filters[0]["RequestMatch"]["MatchType"], "CaseInsensitive");

    let many = build_rest_search_body(&query, &RestInformation::default(), SearchSemantics::Many).expect("body");
    assert_eq!(many["Filters"][0]["RequestMatch"]["MatchType"], "Substring");
}

// ── S2: direct-mode refresh must not write the source store ──

#[test]
fn preindexed_refresh_in_system_winget_direct_mode_does_not_save_store() {
    let server = StubServer::start(Vec::new(), 304);
    let app_root = temp_root("direct-refresh");
    let mut repository = open_private_repository(
        &app_root,
        vec![source("direct", SourceKind::PreIndexed, server.url(""), false)],
    );
    let index_path = repository.preindexed_index_path(&repository.store.sources[0].clone());
    fs::create_dir_all(index_path.parent().expect("index parent")).expect("create state dir");
    fs::write(&index_path, b"existing index").expect("write index");
    let store_file = store_path(&app_root);
    fs::remove_file(&store_file).expect("remove store file");
    repository.source_mode = EffectiveSourceMode::SystemWingetDirect;

    let detail = repository
        .refresh_preindexed_source(0, true)
        .expect("refresh succeeds without saving WinGet's source list");

    assert!(detail.contains("already up to date"), "{detail}");
    assert!(
        !store_file.exists(),
        "direct mode must not write a private source store"
    );
    let _ = fs::remove_dir_all(app_root);
}

// ── S3: REST information cache lifetime ──

#[test]
fn rest_information_cache_expiry_clamps_huge_max_age() {
    let now = Utc::now();
    assert_eq!(
        rest_information_cache_expiry(now, u64::MAX),
        now + Duration::seconds(86_400)
    );
    assert_eq!(
        rest_information_cache_expiry(now, i64::MAX as u64),
        now + Duration::seconds(86_400)
    );
    assert_eq!(rest_information_cache_expiry(now, 120), now + Duration::seconds(120));
    assert_eq!(rest_information_cache_expiry(now, 0), now);
}

#[test]
fn cache_control_max_age_parses_or_defaults_to_sixty_seconds() {
    assert_eq!(parse_cache_control_max_age(Some("public, max-age=3600")), 3600);
    assert_eq!(
        parse_cache_control_max_age(Some("max-age=18446744073709551615")),
        u64::MAX
    );
    assert_eq!(parse_cache_control_max_age(Some("max-age=99999999999999999999999")), 60);
    assert_eq!(parse_cache_control_max_age(Some("no-cache")), 60);
    assert_eq!(parse_cache_control_max_age(None), 60);
}

// ── S4: sources.json key format shared with C# ──

/// Exactly what the C# SourceStoreContext writes (camelCase, `eTag`, 7-digit fractions).
const CSHARP_SOURCES_JSON: &str = r#"{
  "sources": [
    {
      "name": "contoso",
      "kind": "Rest",
      "arg": "https://contoso.example/api",
      "identifier": "Contoso.Rest",
      "trustLevel": "Trusted",
      "explicit": true,
      "priority": 3,
      "lastUpdate": "2024-05-01T12:34:56.1234567Z",
      "sourceVersion": "1.10.0",
      "eTag": "\"abc\"",
      "lastModified": "Wed, 01 May 2024 12:34:56 GMT"
    }
  ]
}"#;

/// What earlier Rust builds wrote (snake_case keys).
const LEGACY_RUST_SOURCES_JSON: &str = r#"{
  "sources": [
    {
      "name": "contoso",
      "kind": "Rest",
      "arg": "https://contoso.example/api",
      "identifier": "Contoso.Rest",
      "trust_level": "Trusted",
      "explicit": true,
      "priority": 3,
      "last_update": "2024-05-01T12:34:56.123456789Z",
      "source_version": "1.10.0",
      "etag": "\"abc\"",
      "last_modified": "Wed, 01 May 2024 12:34:56 GMT"
    }
  ]
}"#;

fn assert_contoso_source(record: &SourceRecord) {
    assert_eq!(record.name, "contoso");
    assert_eq!(record.kind, SourceKind::Rest);
    assert_eq!(record.identifier, "Contoso.Rest");
    assert_eq!(record.trust_level, "Trusted");
    assert!(record.explicit);
    assert_eq!(record.priority, 3);
    assert!(record.last_update.is_some());
    assert_eq!(record.source_version.as_deref(), Some("1.10.0"));
    assert_eq!(record.etag.as_deref(), Some("\"abc\""));
    assert_eq!(record.last_modified.as_deref(), Some("Wed, 01 May 2024 12:34:56 GMT"));
}

#[test]
fn source_store_reads_csharp_camel_case_format() {
    let app_root = temp_root("csharp-store");
    fs::create_dir_all(&app_root).expect("create app root");
    fs::write(store_path(&app_root), CSHARP_SOURCES_JSON).expect("write store");

    let store = load_store(&app_root).expect("load C# store");

    assert_contoso_source(&store.sources[0]);
    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn source_store_still_reads_legacy_rust_snake_case_format() {
    let store: SourceStore = serde_json::from_str(LEGACY_RUST_SOURCES_JSON).expect("parse legacy store");
    assert_contoso_source(&store.sources[0]);
}

#[test]
fn source_store_writes_csharp_compatible_keys() {
    let store: SourceStore = serde_json::from_str(LEGACY_RUST_SOURCES_JSON).expect("parse legacy store");
    let value = serde_json::to_value(&store).expect("serialize store");
    let record = value["sources"][0].as_object().expect("record object");

    for key in [
        "name",
        "kind",
        "arg",
        "identifier",
        "trustLevel",
        "explicit",
        "priority",
        "lastUpdate",
        "sourceVersion",
        "eTag",
        "lastModified",
    ] {
        assert!(record.contains_key(key), "missing {key} in {record:?}");
    }
    assert!(!record.contains_key("trust_level"));
    assert_eq!(record["lastUpdate"], "2024-05-01T12:34:56.123456789Z");

    let round_tripped: SourceStore = serde_json::from_value(value).expect("read back");
    assert_contoso_source(&round_tripped.sources[0]);
}

// ── S6 / S13: source update ──

#[test]
fn update_sources_reports_failures_and_saves_successful_sources() {
    let server = StubServer::start(
        vec![
            ("/good/information", 200, REST_INFORMATION_JSON),
            ("/bad/information", 500, ""),
        ],
        404,
    );
    let app_root = temp_root("update-partial");
    let mut repository = open_private_repository(
        &app_root,
        vec![
            source("bad", SourceKind::Rest, server.url("/bad"), false),
            source("good", SourceKind::Rest, server.url("/good"), false),
        ],
    );

    let results = repository
        .update_sources(None)
        .expect("per-source failures are reported in the results");

    assert_eq!(results.len(), 2);
    assert_eq!(results[0].name, "bad");
    assert!(!results[0].success);
    assert!(!results[0].detail.is_empty());
    assert_eq!(results[1].name, "good");
    assert!(results[1].success, "{}", results[1].detail);

    let saved = load_store(&app_root).expect("reload store");
    let good = saved.sources.iter().find(|item| item.name == "good").expect("good");
    assert!(good.last_update.is_some(), "the successful source must be saved");
    assert_eq!(good.source_version.as_deref(), Some("1.10.0"));
    let bad = saved.sources.iter().find(|item| item.name == "bad").expect("bad");
    assert!(bad.last_update.is_none());
    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn update_sources_without_name_includes_explicit_sources() {
    let server = StubServer::start(vec![("/explicit/information", 200, REST_INFORMATION_JSON)], 404);
    let app_root = temp_root("update-explicit");
    let mut repository = open_private_repository(
        &app_root,
        vec![source("explicit-src", SourceKind::Rest, server.url("/explicit"), true)],
    );

    let results = repository.update_sources(None).expect("update all");

    assert_eq!(results.len(), 1);
    assert_eq!(results[0].name, "explicit-src");
    assert!(results[0].success, "{}", results[0].detail);
    // Search resolution still skips explicit sources.
    assert!(repository.resolve_source_indexes(None).expect("resolve").is_empty());
    let _ = fs::remove_dir_all(app_root);
}

// ── S10: LIKE wildcards in user input ──

#[test]
fn match_parameter_escapes_like_wildcards() {
    assert_eq!(match_parameter("100%_a\\b", true), "100\\%\\_a\\\\b");
    assert_eq!(match_parameter("100%", false), "%100\\%%");
    assert_eq!(match_parameter("plain", false), "%plain%");
}

fn seed_database(label: &str, sql: &str) -> (PathBuf, Connection) {
    let root = temp_root(label);
    fs::create_dir_all(&root).expect("create db root");
    let connection = Repository::open_sqlite_connection(root.join("index.db")).expect("open db");
    execute_batch_sql(&connection, sql).expect("seed db");
    (root, connection)
}

const V1_SCHEMA: &str = "CREATE TABLE ids (id TEXT);
     CREATE TABLE names (name TEXT);
     CREATE TABLE monikers (moniker TEXT);
     CREATE TABLE versions (version TEXT);
     CREATE TABLE channels (channel TEXT);
     CREATE TABLE manifest (id INT64, name INT64, moniker INT64, version INT64, channel INT64, pathpart INT64);
     CREATE TABLE tags (tag TEXT);
     CREATE TABLE tags_map (tag INT64, manifest INT64);
     CREATE TABLE commands (command TEXT);
     CREATE TABLE commands_map (command INT64, manifest INT64);
     INSERT INTO ids(rowid, id) VALUES (1, 'Contoso_App'), (2, 'ContosoXApp');
     INSERT INTO names(rowid, name) VALUES (1, 'Get 100% Done'), (2, 'Get 1000 Things');
     INSERT INTO versions(rowid, version) VALUES (1, '1.0.0');
     INSERT INTO channels(rowid, channel) VALUES (1, '');
     INSERT INTO manifest(id, name, moniker, version, channel, pathpart) VALUES (1, 1, NULL, 1, 1, 0), (2, 2, NULL, 1, 1, 0);";

const V2_SCHEMA: &str = "CREATE TABLE packages (id TEXT, name TEXT, moniker TEXT, latest_version TEXT, hash BLOB);
     CREATE TABLE tags2 (tag TEXT);
     CREATE TABLE tags2_map (tag INT64, package INT64);
     CREATE TABLE commands2 (command TEXT);
     CREATE TABLE commands2_map (command INT64, package INT64);
     INSERT INTO packages(rowid, id, name, moniker, latest_version, hash) VALUES
         (1, 'Contoso_App', 'Get 100% Done', NULL, '1.0.0', X'00AB'),
         (2, 'ContosoXApp', 'Get 1000 Things', NULL, '1.0.0', X'00AC');";

fn exact_id_query() -> PackageQuery {
    PackageQuery {
        id: Some("Contoso_App".to_owned()),
        exact: true,
        ..PackageQuery::default()
    }
}

fn percent_query() -> PackageQuery {
    PackageQuery {
        query: Some("100%".to_owned()),
        ..PackageQuery::default()
    }
}

#[test]
fn v1_search_treats_percent_and_underscore_literally() {
    let (root, connection) = seed_database("like-v1", V1_SCHEMA);

    let (rows, _) = query_v1_matches(&connection, &exact_id_query(), SearchSemantics::Single).expect("v1 id");
    let ids: Vec<_> = rows.iter().map(|row| row.id.as_str()).collect();
    assert_eq!(ids, ["Contoso_App"]);

    let (rows, _) = query_v1_matches(&connection, &percent_query(), SearchSemantics::Many).expect("v1 query");
    let names: Vec<_> = rows.iter().map(|row| row.name.as_str()).collect();
    assert_eq!(names, ["Get 100% Done"]);

    drop(connection);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn v2_search_treats_percent_and_underscore_literally() {
    let (root, connection) = seed_database("like-v2", V2_SCHEMA);

    let (rows, _) = query_v2_matches(&connection, &exact_id_query(), SearchSemantics::Single).expect("v2 id");
    let ids: Vec<_> = rows.iter().map(|row| row.id.as_str()).collect();
    assert_eq!(ids, ["Contoso_App"]);

    let (rows, _) = query_v2_matches(&connection, &percent_query(), SearchSemantics::Many).expect("v2 query");
    let names: Vec<_> = rows.iter().map(|row| row.name.as_str()).collect();
    assert_eq!(names, ["Get 100% Done"]);

    drop(connection);
    let _ = fs::remove_dir_all(root);
}

// ── S11 / S14 parity: case-insensitive names, versions and channels ──

#[test]
fn remove_source_matches_name_case_insensitively() {
    let app_root = temp_root("remove-case");
    let mut repository = open_private_repository(
        &app_root,
        vec![source(
            "Contoso",
            SourceKind::Rest,
            "https://contoso.example".to_owned(),
            false,
        )],
    );

    repository.remove_source("CONTOSO").expect("remove");

    assert!(repository.list_sources().is_empty());
    assert!(load_store(&app_root).expect("reload").sources.is_empty());
    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn version_selection_ignores_version_and_channel_case() {
    let v1 = [V1VersionRow {
        version: "1.0.0-BETA".to_owned(),
        channel: "Preview".to_owned(),
        pathpart_id: 0,
        manifest_hash: None,
    }];
    let selected = select_v1_version(&v1, Some("1.0.0-beta"), Some("preview"), "Test.Package", "test").expect("v1");
    assert_eq!(selected.version, "1.0.0-BETA");
    let selected = select_v1_version(&v1, None, Some("PREVIEW"), "Test.Package", "test").expect("v1 channel");
    assert_eq!(selected.channel, "Preview");

    let rest = [VersionKey {
        version: "2.0.0-RC".to_owned(),
        channel: "Beta".to_owned(),
    }];
    let selected = select_rest_version(&rest, Some("2.0.0-rc"), Some("beta"), "Test.Package", "test").expect("rest");
    assert_eq!(selected.version, "2.0.0-RC");
}
