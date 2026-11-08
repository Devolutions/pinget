//! Wave 9 (sources and HTTP, round 2) regression tests.

use std::io::{Read as _, Write as _};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering as AtomicOrdering};
use std::thread::{self, JoinHandle};

use super::*;

const REST_INFORMATION_JSON: &str = r#"{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}"#;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "pinget-rs-wave9-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ))
}

struct Route {
    path: &'static str,
    status: u16,
    content_type: &'static str,
    body: String,
}

fn route(path: &'static str, status: u16, content_type: &'static str, body: &str) -> Route {
    Route {
        path,
        status,
        content_type,
        body: body.to_owned(),
    }
}

/// Minimal HTTP/1.1 stub: answers each path (query string ignored) from a fixed route
/// table, else 404 text/plain, and records the request paths it saw.
struct StubServer {
    addr: SocketAddr,
    running: Arc<AtomicBool>,
    requests: Arc<Mutex<Vec<String>>>,
    handle: Option<JoinHandle<()>>,
}

impl StubServer {
    fn start(routes: Vec<Route>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind stub server");
        let addr = listener.local_addr().expect("stub server address");
        let running = Arc::new(AtomicBool::new(true));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let thread_running = Arc::clone(&running);
        let thread_requests = Arc::clone(&requests);
        let handle = thread::spawn(move || {
            while thread_running.load(AtomicOrdering::SeqCst) {
                let Ok((mut stream, _)) = listener.accept() else {
                    continue;
                };
                let request = read_request(&mut stream);
                let target = request
                    .lines()
                    .next()
                    .and_then(|line| line.split_whitespace().nth(1))
                    .unwrap_or("/")
                    .to_owned();
                thread_requests.lock().expect("requests").push(target.clone());
                let path = target.split('?').next().unwrap_or_default();
                let (status, content_type, body) = routes
                    .iter()
                    .find(|route| route.path == path)
                    .map(|route| (route.status, route.content_type, route.body.clone()))
                    .unwrap_or((404, "text/plain", String::new()));
                let response = format!(
                    "HTTP/1.1 {status} Stub\r\nContent-Length: {}\r\nContent-Type: {content_type}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = stream.write_all(response.as_bytes());
            }
        });
        Self {
            addr,
            running,
            requests,
            handle: Some(handle),
        }
    }

    fn url(&self, path: &str) -> String {
        format!("http://{}{path}", self.addr)
    }

    fn requests(&self) -> Vec<String> {
        self.requests.lock().expect("requests").clone()
    }
}

/// Reads the request head and any `Content-Length` body, so the stub never closes the
/// connection while a POST body is still being sent.
fn read_request(stream: &mut TcpStream) -> String {
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(5)));
    let mut received = Vec::new();
    let mut buffer = [0u8; 4096];
    loop {
        let text = String::from_utf8_lossy(&received).into_owned();
        if let Some(head_end) = text.find("\r\n\r\n") {
            let content_length = text[..head_end]
                .lines()
                .filter_map(|line| line.split_once(':'))
                .find(|(name, _)| name.trim().eq_ignore_ascii_case("content-length"))
                .and_then(|(_, value)| value.trim().parse::<usize>().ok())
                .unwrap_or(0);
            if received.len() >= head_end + 4 + content_length {
                return text;
            }
        }
        match stream.read(&mut buffer) {
            Ok(0) | Err(_) => return String::from_utf8_lossy(&received).into_owned(),
            Ok(read) => received.extend_from_slice(&buffer[..read]),
        }
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

/// Test sources point at loopback `http://` stubs, which `source add` refuses, so they
/// are written straight into the store.
fn open_private_repository(app_root: &Path, sources: Vec<SourceRecord>) -> Repository {
    fs::create_dir_all(app_root).expect("create app root");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.to_path_buf())).expect("open repository");
    repository.store.sources = sources;
    repository.save_store().expect("save test sources");
    repository
}

// ── R9-1: index-supplied relative paths must stay inside the source cache ──

const HOSTILE_RELATIVE_PATHS: &[&str] = &[
    r"..\..\..\..\..\..\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.cmd",
    "../escape.txt",
    "manifests/../../escape.txt",
    "manifests/a/..",
    r"C:\Windows\Temp\x.cmd",
    "C:x.cmd",
    r"\\server\share\x.cmd",
    "//server/share/x.cmd",
    "/etc/passwd",
    r"\rooted\x.cmd",
    "",
    "manifests//x.yaml",
    "manifests/./x.yaml",
    "manifests/ ../x.yaml",
    "manifests/.../x.yaml",
    "manifests/x.yaml:stream",
];

#[test]
fn source_relative_paths_reject_traversal_rooted_drive_and_unc() {
    for path in HOSTILE_RELATIVE_PATHS {
        assert!(
            validated_source_relative_segments(path).is_err(),
            "'{path}' must be rejected"
        );
    }

    assert_eq!(
        validated_source_relative_segments(r"manifests\c\Contoso\App\1.0.0\abcd.yaml").expect("safe"),
        ["manifests", "c", "Contoso", "App", "1.0.0", "abcd.yaml"]
    );
    assert_eq!(
        validated_source_relative_segments("packages/Contoso.App/0123abcd/versionData.mszyml").expect("safe"),
        ["packages", "Contoso.App", "0123abcd", "versionData.mszyml"]
    );
}

#[test]
fn package_version_data_path_from_hostile_index_id_is_rejected() {
    let relative = package_version_data_relative_path(r"..\..\..\evil", "0123456789abcdef");
    assert!(validated_source_relative_segments(&relative).is_err());
}

#[test]
fn cached_source_file_with_hostile_path_writes_nothing_outside_the_cache() {
    let server = StubServer::start(vec![
        route("/escape.txt", 200, "text/plain", "attacker bytes"),
        route("/manifests/escape.txt", 200, "text/plain", "attacker bytes"),
    ]);
    let app_root = temp_root("cache-traversal");
    let repository = open_private_repository(&app_root, vec![source("evil", SourceKind::PreIndexed, server.url(""))]);
    let evil = repository.store.sources[0].clone();

    for relative in [
        "../../../escape.txt",
        r"..\..\..\escape.txt",
        "manifests/../../../../escape.txt",
    ] {
        let error = repository
            .get_cached_source_file("V1_M", &evil, relative, None)
            .expect_err("hostile path must be rejected");
        assert!(format!("{error:#}").contains("not a safe relative path"), "{error:#}");
    }
    assert!(!cache_root(&app_root).join("escape.txt").exists());
    assert!(!app_root.join("escape.txt").exists());
    assert!(
        server.requests().is_empty(),
        "nothing is downloaded for a rejected path"
    );

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn cached_source_file_with_safe_path_is_written_under_the_bucket() {
    let server = StubServer::start(vec![route(
        "/manifests/c/Contoso/App/1.0.0/a.yaml",
        200,
        "text/plain",
        "PackageIdentifier: Contoso.App",
    )]);
    let app_root = temp_root("cache-safe");
    let repository = open_private_repository(&app_root, vec![source("good", SourceKind::PreIndexed, server.url(""))]);
    let good = repository.store.sources[0].clone();

    let file = repository
        .get_cached_source_file("V1_M", &good, r"manifests\c\Contoso\App\1.0.0\a.yaml", None)
        .expect("safe path");
    assert!(file.path.starts_with(temp_cache_path(&app_root, "V1_M", "good")));
    assert_eq!(fs::read(&file.path).expect("cached"), b"PackageIdentifier: Contoso.App");
    assert_eq!(server.requests(), ["/manifests/c/Contoso/App/1.0.0/a.yaml"]);

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn dot_only_identifiers_do_not_name_the_parent_directory() {
    assert_eq!(sanitize_path_segment(".."), "_");
    assert_eq!(sanitize_path_segment("."), "_");
    assert_eq!(sanitize_path_segment(""), "_");
    assert_eq!(sanitize_path_segment("Contoso.Rest"), "Contoso.Rest");
    assert_eq!(sanitize_path_segment("a/../b"), "a_.._b");
}

// ── R9-2: source add accepts only secure remote URLs ──

#[test]
fn source_argument_must_be_secure_and_remote() {
    let not_secure = validate_source_argument("http://corp/api").expect_err("http");
    assert!(not_secure.to_string().contains("not secure"), "{not_secure}");

    for arg in ["ftp://corp/api", r"C:\feeds\corp", "corp", "file:///C:/feeds/corp", ""] {
        let error = validate_source_argument(arg).expect_err(arg);
        assert!(error.to_string().contains("not remote"), "{arg}: {error}");
    }

    assert!(validate_source_argument("https://").is_err());
    validate_source_argument("https://corp.example/api").expect("https");
    validate_source_argument("HTTPS://Corp.Example/api").expect("scheme is case-insensitive");
}

#[test]
fn add_source_rejects_plain_http_and_keeps_the_store_unchanged() {
    let app_root = temp_root("add-http");
    let mut repository = open_private_repository(&app_root, Vec::new());

    for kind in [SourceKind::Rest, SourceKind::PreIndexed] {
        let error = repository
            .add_source_with_metadata("corp", "http://corp/api", kind, None, false, 0)
            .expect_err("http source");
        assert!(error.to_string().contains("not secure"), "{error}");
    }
    assert!(repository.list_sources().is_empty());
    assert!(load_store(&app_root).expect("reload").sources.is_empty());

    repository
        .add_source("corp", "https://corp.example/api", SourceKind::Rest)
        .expect("https source");
    assert_eq!(repository.list_sources().len(), 1);

    let _ = fs::remove_dir_all(app_root);
}

// ── R9-3: REST 204 and 404 {code,message} are empty results ──

fn rest_repository(label: &str, search: Route) -> (StubServer, PathBuf, Repository) {
    let server = StubServer::start(vec![
        route("/api/information", 200, "application/json", REST_INFORMATION_JSON),
        search,
    ]);
    let app_root = temp_root(label);
    let repository = open_private_repository(&app_root, vec![source("rest", SourceKind::Rest, server.url("/api"))]);
    (server, app_root, repository)
}

fn nomatch_query() -> PackageQuery {
    PackageQuery {
        query: Some("nomatch".to_owned()),
        ..PackageQuery::default()
    }
}

#[test]
fn rest_search_no_content_is_an_empty_result() {
    let (_server, app_root, mut repository) =
        rest_repository("rest-204", route("/api/manifestSearch", 204, "text/plain", ""));

    let result = repository
        .search_rest(0, &nomatch_query(), SearchSemantics::Many)
        .expect("204 is no matches");
    assert!(result.matches.is_empty());
    assert!(!result.truncated);

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn rest_search_not_found_with_code_and_message_is_an_empty_result() {
    let (_server, app_root, mut repository) = rest_repository(
        "rest-404-json",
        route(
            "/api/manifestSearch",
            404,
            "application/json; charset=utf-8",
            r#"{"code":"NotFound","message":"No packages found"}"#,
        ),
    );

    let result = repository
        .search_rest(0, &nomatch_query(), SearchSemantics::Single)
        .expect("404 {code,message} is no matches");
    assert!(result.matches.is_empty());

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn rest_search_other_not_found_responses_stay_errors() {
    for (label, content_type, body) in [
        ("rest-404-text", "text/plain", "missing"),
        ("rest-404-json-no-code", "application/json", r#"{"message":"gone"}"#),
    ] {
        let (_server, app_root, mut repository) =
            rest_repository(label, route("/api/manifestSearch", 404, content_type, body));
        assert!(
            repository
                .search_rest(0, &nomatch_query(), SearchSemantics::Many)
                .is_err(),
            "{label} must stay an error"
        );
        let _ = fs::remove_dir_all(app_root);
    }
}

#[test]
fn rest_manifest_no_content_reports_a_missing_manifest() {
    let (_server, app_root, mut repository) = rest_repository(
        "rest-manifest-204",
        route("/api/packageManifests/Contoso.App", 204, "text/plain", ""),
    );
    let rest = repository.store.sources[0].clone();

    let error = repository
        .fetch_rest_manifest(&rest, "Contoso.App", "1.0.0", "")
        .expect_err("no manifest");
    assert!(error.to_string().contains("No manifest found"), "{error}");

    let _ = fs::remove_dir_all(app_root);
}

// ── R9-5: single REST lookups are CaseInsensitive unless --exact ──

fn match_types(body: &JsonValue, key: &str) -> Vec<(String, String)> {
    body[key]
        .as_array()
        .map(|items| {
            items
                .iter()
                .map(|item| {
                    (
                        item["PackageMatchField"].as_str().unwrap_or_default().to_owned(),
                        item["RequestMatch"]["MatchType"]
                            .as_str()
                            .unwrap_or_default()
                            .to_owned(),
                    )
                })
                .collect()
        })
        .unwrap_or_default()
}

fn pairs(items: &[(&str, &str)]) -> Vec<(String, String)> {
    items
        .iter()
        .map(|(field, match_type)| ((*field).to_owned(), (*match_type).to_owned()))
        .collect()
}

#[test]
fn rest_single_lookups_use_case_insensitive_match() {
    let info = RestInformation {
        required_package_match_fields: vec!["Market".to_owned()],
        ..RestInformation::default()
    };
    let query = PackageQuery {
        query: Some("contoso.tool".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&query, &info, SearchSemantics::Single).expect("body");
    assert_eq!(
        match_types(&body, "Inclusions"),
        pairs(&[
            ("PackageIdentifier", "CaseInsensitive"),
            ("PackageName", "CaseInsensitive"),
            ("Moniker", "CaseInsensitive"),
        ])
    );
    assert_eq!(match_types(&body, "Filters"), pairs(&[("Market", "Exact")]));

    let filters = PackageQuery {
        id: Some("contoso.tool".to_owned()),
        tag: Some("cli".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&filters, &RestInformation::default(), SearchSemantics::Single).expect("body");
    assert_eq!(
        match_types(&body, "Filters"),
        pairs(&[("PackageIdentifier", "CaseInsensitive"), ("Tag", "CaseInsensitive")])
    );
}

#[test]
fn rest_exact_and_many_lookups_keep_their_match_types() {
    let exact = PackageQuery {
        query: Some("Contoso.Tool".to_owned()),
        exact: true,
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&exact, &RestInformation::default(), SearchSemantics::Single).expect("body");
    assert!(
        match_types(&body, "Inclusions")
            .iter()
            .all(|(_, match_type)| match_type == "Exact")
    );

    let many = PackageQuery {
        id: Some("Contoso".to_owned()),
        tag: Some("cli".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&many, &RestInformation::default(), SearchSemantics::Many).expect("body");
    assert_eq!(
        match_types(&body, "Filters"),
        pairs(&[("PackageIdentifier", "Substring"), ("Tag", "Exact")])
    );
}

// ── R9-7: REST manifests are not cached; remove/reset clear source file caches ──

#[test]
fn rest_manifest_is_refetched_rather_than_served_from_a_stale_cache() {
    let first = StubServer::start(vec![
        route("/api/information", 200, "application/json", REST_INFORMATION_JSON),
        route(
            "/api/packageManifests/Contoso.App",
            200,
            "application/json",
            r#"{"Data":"first"}"#,
        ),
    ]);
    let second = StubServer::start(vec![
        route("/api/information", 200, "application/json", REST_INFORMATION_JSON),
        route(
            "/api/packageManifests/Contoso.App",
            200,
            "application/json",
            r#"{"Data":"second"}"#,
        ),
    ]);
    let app_root = temp_root("rest-manifest-fresh");
    let mut repository = open_private_repository(&app_root, vec![source("rest", SourceKind::Rest, first.url("/api"))]);
    let mut rest = repository.store.sources[0].clone();

    let bytes = repository
        .fetch_rest_manifest(&rest, "Contoso.App", "1.0.0", "")
        .expect("first fetch");
    assert_eq!(bytes, br#"{"Data":"first"}"#);

    // The server republishes 1.0.0 (here: the same source now answers differently).
    rest.arg = second.url("/api");
    repository.store.sources[0].arg = rest.arg.clone();
    let bytes = repository
        .fetch_rest_manifest(&rest, "Contoso.App", "1.0.0", "")
        .expect("second fetch");
    assert_eq!(bytes, br#"{"Data":"second"}"#);
    assert!(!cache_root(&app_root).join("REST_M").exists());

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn remove_and_reset_source_clear_per_identifier_file_caches() {
    let app_root = temp_root("cache-cleanup");
    let mut repository = open_private_repository(
        &app_root,
        vec![
            source("corp", SourceKind::PreIndexed, "https://corp.example/cache".to_owned()),
            source(
                "other",
                SourceKind::PreIndexed,
                "https://other.example/cache".to_owned(),
            ),
        ],
    );
    let seed = |identifier: &str| {
        for bucket in SOURCE_FILE_CACHE_BUCKETS {
            let dir = temp_cache_path(&app_root, bucket, identifier);
            fs::create_dir_all(&dir).expect("cache dir");
            fs::write(dir.join("stale"), b"stale").expect("cache file");
        }
    };
    seed("corp");
    seed("other");

    repository.reset_source("corp").expect("reset");
    for bucket in SOURCE_FILE_CACHE_BUCKETS {
        assert!(!temp_cache_path(&app_root, bucket, "corp").exists(), "{bucket}");
        assert!(temp_cache_path(&app_root, bucket, "other").exists(), "{bucket}");
    }

    seed("corp");
    repository.remove_source("corp").expect("remove");
    for bucket in SOURCE_FILE_CACHE_BUCKETS {
        assert!(!temp_cache_path(&app_root, bucket, "corp").exists(), "{bucket}");
        assert!(temp_cache_path(&app_root, bucket, "other").exists(), "{bucket}");
    }

    let _ = fs::remove_dir_all(app_root);
}

// ── R9-8: packaged layout remembers removed default sources ──

#[test]
fn packaged_sources_yaml_tombstones_removed_default_sources() {
    let mut store = SourceStore::default();
    store.sources.retain(|source| source.name != "msstore");

    let yaml = render_packaged_sources_yaml(&store);
    assert!(yaml.contains("IsTombstone: true"), "{yaml}");

    let reloaded = parse_packaged_source_store(Some(&yaml), None).expect("store");
    let names: Vec<_> = reloaded.sources.iter().map(|source| source.name.as_str()).collect();
    assert_eq!(names, ["winget"]);

    let defaults = render_packaged_sources_yaml(&SourceStore::default());
    assert!(!defaults.contains("IsTombstone: true"), "{defaults}");
}

// ── R9-9: V1 search limits packages, not manifest rows ──

const V1_MANY_VERSIONS_SCHEMA: &str = "CREATE TABLE ids (id TEXT);
     CREATE TABLE names (name TEXT);
     CREATE TABLE monikers (moniker TEXT);
     CREATE TABLE versions (version TEXT);
     CREATE TABLE channels (channel TEXT);
     CREATE TABLE manifest (id INT64, name INT64, moniker INT64, version INT64, channel INT64, pathpart INT64);
     CREATE TABLE tags (tag TEXT);
     CREATE TABLE tags_map (tag INT64, manifest INT64);
     CREATE TABLE commands (command TEXT);
     CREATE TABLE commands_map (command INT64, manifest INT64);
     INSERT INTO ids(rowid, id) VALUES (1, 'Contoso.App'), (2, 'Other.App');
     INSERT INTO names(rowid, name) VALUES (1, 'Contoso App'), (2, 'Contoso.App');
     INSERT INTO versions(rowid, version) VALUES (1, '1.0.0'), (2, '2.0.0'), (3, '3.0.0');
     INSERT INTO channels(rowid, channel) VALUES (1, '');
     INSERT INTO manifest(id, name, moniker, version, channel, pathpart) VALUES
         (1, 1, NULL, 1, 1, 0), (1, 1, NULL, 2, 1, 0), (1, 1, NULL, 3, 1, 0), (2, 2, NULL, 1, 1, 0);";

#[test]
fn v1_search_limit_counts_packages_not_versions() {
    let root = temp_root("v1-limit");
    fs::create_dir_all(&root).expect("create db root");
    let connection = Repository::open_sqlite_connection(root.join("index.db")).expect("open db");
    execute_batch_sql(&connection, V1_MANY_VERSIONS_SCHEMA).expect("seed db");

    // `contoso.app` matches Contoso.App by id (3 versions) and Other.App by name.
    let query = PackageQuery {
        query: Some("Contoso.App".to_owned()),
        count: Some(2),
        ..PackageQuery::default()
    };
    let (rows, truncated) = query_v1_matches(&connection, &query, SearchSemantics::Single).expect("v1 search");
    let mut ids: Vec<_> = rows.iter().map(|row| row.id.as_str()).collect();
    ids.sort_unstable();
    assert_eq!(ids, ["Contoso.App", "Other.App"]);
    assert!(!truncated, "two packages fit a limit of two");
    let contoso = rows.iter().find(|row| row.id == "Contoso.App").expect("contoso");
    assert_eq!(contoso.version, "3.0.0");

    let one = PackageQuery {
        count: Some(1),
        ..query
    };
    let (rows, truncated) = query_v1_matches(&connection, &one, SearchSemantics::Single).expect("v1 search");
    assert_eq!(rows.len(), 1);
    assert!(truncated);

    drop(connection);
    let _ = fs::remove_dir_all(root);
}

// ── R9-11: packaged YAML scalars round-trip ──

#[test]
fn packaged_yaml_scalars_round_trip_quotes_and_comment_markers() {
    let mut corp = source("#corp", SourceKind::Rest, "https://corp.example/it's api".to_owned());
    corp.identifier = "Corp: \"Rest\"".to_owned();
    corp.etag = Some("\"0x8DCB1A2B3C4D5E6\"".to_owned());
    corp.last_modified = Some("Tue, 01 Jan 2030 00:00:00 GMT".to_owned());
    corp.source_version = Some("1.10".to_owned());
    let store = SourceStore {
        sources: vec![corp.clone()],
    };

    let sources_yaml = render_packaged_sources_yaml(&store);
    let metadata_yaml = render_packaged_metadata_yaml(&store);
    let reloaded = parse_packaged_source_store(Some(&sources_yaml), Some(&metadata_yaml)).expect("store");
    let reloaded = reloaded
        .sources
        .iter()
        .find(|source| source.name == "#corp")
        .expect("#corp survives as a name");

    assert_eq!(reloaded.arg, corp.arg);
    assert_eq!(reloaded.identifier, corp.identifier);
    assert_eq!(reloaded.etag, corp.etag);
    assert_eq!(reloaded.last_modified, corp.last_modified);
    assert_eq!(reloaded.source_version, corp.source_version);
}

#[test]
fn unquote_yaml_scalar_handles_single_and_double_quoted_escapes() {
    assert_eq!(unquote_yaml_scalar("'it''s'"), "it's");
    assert_eq!(unquote_yaml_scalar(r#"'"0x8DC"'"#), "\"0x8DC\"");
    assert_eq!(unquote_yaml_scalar(r#""a \"b\" \\ c""#), r#"a "b" \ c"#);
    assert_eq!(unquote_yaml_scalar(r#""tab\there \u00e9""#), "tab\there \u{e9}");
    assert_eq!(unquote_yaml_scalar("plain"), "plain");
    assert_eq!(unquote_yaml_scalar("''"), "");
}

// ── R9-15: package ids are percent-encoded in the manifest URL ──

#[test]
fn rest_manifest_url_percent_encodes_the_package_id() {
    assert_eq!(
        rest_package_manifest_url("https://corp.example/api/", "Contoso.C#Tool"),
        "https://corp.example/api/packageManifests/Contoso.C%23Tool"
    );
    assert_eq!(
        rest_package_manifest_url("https://corp.example/api", "A b/c?d%e+f"),
        "https://corp.example/api/packageManifests/A%20b%2Fc%3Fd%25e%2Bf"
    );
    assert_eq!(
        rest_package_manifest_url("https://corp.example/api", "Contoso.App_1-2~3"),
        "https://corp.example/api/packageManifests/Contoso.App_1-2~3"
    );
}

#[test]
fn rest_manifest_request_targets_the_encoded_package_id() {
    let (server, app_root, mut repository) = rest_repository(
        "rest-manifest-encoded",
        route(
            "/api/packageManifests/Contoso.C%23Tool",
            200,
            "application/json",
            r#"{"Data":"ok"}"#,
        ),
    );
    let rest = repository.store.sources[0].clone();

    let bytes = repository
        .fetch_rest_manifest(&rest, "Contoso.C#Tool", "1.0.0", "")
        .expect("manifest");
    assert_eq!(bytes, br#"{"Data":"ok"}"#);
    assert!(
        server
            .requests()
            .iter()
            .any(|request| request.starts_with("/api/packageManifests/Contoso.C%23Tool?"))
    );

    let _ = fs::remove_dir_all(app_root);
}
