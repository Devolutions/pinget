//! Wave 15 (REST sources and HTTP) regression tests.

use std::io::{Read as _, Write as _};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering as AtomicOrdering};
use std::thread::{self, JoinHandle};

use super::*;

const REST_INFORMATION_JSON: &str = r#"{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}"#;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "pinget-rs-wave15-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ))
}

struct Reply {
    status: u16,
    headers: String,
    body: String,
}

fn json_reply(body: &str) -> Reply {
    Reply {
        status: 200,
        headers: "Content-Type: application/json\r\n".to_owned(),
        body: body.to_owned(),
    }
}

type Handler = Box<dyn Fn(&str, &str) -> Option<Reply> + Send + Sync>;

/// Minimal HTTP/1.1 stub: the handler gets the request path (query string removed) and the
/// whole request text, and answers or falls through to 404. Every request text is recorded.
struct StubServer {
    addr: SocketAddr,
    running: Arc<AtomicBool>,
    requests: Arc<Mutex<Vec<String>>>,
    handle: Option<JoinHandle<()>>,
}

impl StubServer {
    fn start(handler: Handler) -> Self {
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
                if request.is_empty() {
                    continue;
                }
                thread_requests.lock().expect("requests").push(request.clone());
                let target = request
                    .lines()
                    .next()
                    .and_then(|line| line.split_whitespace().nth(1))
                    .unwrap_or("/");
                let path = target.split('?').next().unwrap_or_default();
                let reply = handler(path, &request).unwrap_or_else(|| Reply {
                    status: 404,
                    headers: "Content-Type: text/plain\r\n".to_owned(),
                    body: String::new(),
                });
                let response = format!(
                    "HTTP/1.1 {} Stub\r\nContent-Length: {}\r\n{}Connection: close\r\n\r\n{}",
                    reply.status,
                    reply.body.len(),
                    reply.headers,
                    reply.body
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

    fn requests_to(&self, path: &str) -> Vec<String> {
        self.requests
            .lock()
            .expect("requests")
            .iter()
            .filter(|request| {
                request
                    .lines()
                    .next()
                    .and_then(|line| line.split_whitespace().nth(1))
                    .is_some_and(|target| target.split('?').next() == Some(path))
            })
            .cloned()
            .collect()
    }
}

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

fn request_header<'a>(request: &'a str, name: &str) -> Option<&'a str> {
    let head = request.split("\r\n\r\n").next().unwrap_or_default();
    head.lines()
        .skip(1)
        .filter_map(|line| line.split_once(':'))
        .find(|(header, _)| header.trim().eq_ignore_ascii_case(name))
        .map(|(_, value)| value.trim())
}

fn rest_source(arg: String) -> SourceRecord {
    SourceRecord {
        name: "rest".to_owned(),
        kind: SourceKind::Rest,
        arg,
        identifier: "rest".to_owned(),
        trust_level: "Trusted".to_owned(),
        explicit: false,
        priority: 0,
        last_update: None,
        source_version: None,
        etag: None,
        last_modified: None,
    }
}

fn rest_repository(label: &str, handler: Handler) -> (StubServer, PathBuf, Repository) {
    let server = StubServer::start(handler);
    let app_root = temp_root(label);
    fs::create_dir_all(&app_root).expect("create app root");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.clone())).expect("open repository");
    repository.store.sources = vec![rest_source(server.url("/api"))];
    repository.save_store().expect("save test sources");
    (server, app_root, repository)
}

fn package_json(id: &str, version: &str) -> String {
    format!(
        r#"{{"PackageIdentifier":"{id}","PackageName":"{id}","Publisher":"Contoso","Versions":[{{"PackageVersion":"{version}"}}]}}"#
    )
}

fn query(text: &str) -> PackageQuery {
    PackageQuery {
        query: Some(text.to_owned()),
        ..PackageQuery::default()
    }
}

fn info_with(unsupported: &[&str], required: &[&str]) -> RestInformation {
    RestInformation {
        server_supported_versions: vec!["1.10.0".to_owned()],
        unsupported_package_match_fields: unsupported.iter().map(|value| (*value).to_owned()).collect(),
        required_package_match_fields: required.iter().map(|value| (*value).to_owned()).collect(),
        ..RestInformation::default()
    }
}

fn fields(body: &JsonValue, key: &str) -> Vec<String> {
    body[key]
        .as_array()
        .map(|items| {
            items
                .iter()
                .map(|item| item["PackageMatchField"].as_str().unwrap_or_default().to_owned())
                .collect()
        })
        .unwrap_or_default()
}

// ── W15-1: manifestSearch pages through ContinuationToken ──

#[test]
fn rest_search_follows_continuation_tokens() {
    let (server, app_root, mut repository) = rest_repository(
        "paging",
        Box::new(|path, request| match path {
            "/api/information" => Some(json_reply(REST_INFORMATION_JSON)),
            "/api/manifestSearch" => Some(match request_header(request, "ContinuationToken") {
                None => json_reply(&format!(
                    r#"{{"Data":[{}],"ContinuationToken":"page-2"}}"#,
                    package_json("Contoso.One", "1.0.0")
                )),
                Some("page-2") => json_reply(&format!(r#"{{"Data":[{}]}}"#, package_json("Contoso.Two", "2.0.0"))),
                Some(_) => json_reply(r#"{"Data":[]}"#),
            }),
            _ => None,
        }),
    );

    let result = repository
        .search_rest(0, &query("contoso"), SearchSemantics::Many)
        .expect("paged search");
    let ids = result
        .matches
        .iter()
        .map(|item| item.display.id.as_str())
        .collect::<Vec<_>>();
    assert_eq!(ids, ["Contoso.One", "Contoso.Two"]);
    assert!(!result.truncated);
    let searches = server.requests_to("/api/manifestSearch");
    assert_eq!(searches.len(), 2);
    assert_eq!(request_header(&searches[1], "ContinuationToken"), Some("page-2"));

    let _ = fs::remove_dir_all(app_root);
}

#[test]
fn rest_search_rejects_a_repeated_continuation_token() {
    let (_server, app_root, mut repository) = rest_repository(
        "paging-loop",
        Box::new(|path, _| match path {
            "/api/information" => Some(json_reply(REST_INFORMATION_JSON)),
            "/api/manifestSearch" => Some(json_reply(&format!(
                r#"{{"Data":[{}],"ContinuationToken":"same"}}"#,
                package_json("Contoso.Loop", "1.0.0")
            ))),
            _ => None,
        }),
    );

    let error = repository
        .search_rest(0, &query("contoso"), SearchSemantics::Many)
        .expect_err("a server repeating its token must not loop forever");
    assert!(error.to_string().contains("repeated continuation token"), "{error:#}");

    let _ = fs::remove_dir_all(app_root);
}

// ── W15-2: unsupported package match fields are dropped from Inclusions ──

#[test]
fn rest_single_search_drops_unsupported_inclusions() {
    let body =
        build_rest_search_body(&query("vscode"), &info_with(&["moniker"], &[]), SearchSemantics::Single).expect("body");
    assert_eq!(fields(&body, "Inclusions"), ["PackageIdentifier", "PackageName"]);
}

#[test]
fn rest_single_search_with_no_supported_inclusion_is_empty_without_a_request() {
    let information = r#"{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"UnsupportedPackageMatchFields":["PackageIdentifier","PackageName","Moniker"]}}"#;
    let (server, app_root, mut repository) = rest_repository(
        "no-inclusions",
        Box::new(move |path, _| match path {
            "/api/information" => Some(json_reply(information)),
            "/api/manifestSearch" => Some(json_reply(&format!(
                r#"{{"Data":[{}]}}"#,
                package_json("Contoso.App", "1.0.0")
            ))),
            _ => None,
        }),
    );

    let result = repository
        .search_rest(0, &query("contoso"), SearchSemantics::Single)
        .expect("empty result");
    assert!(result.matches.is_empty());
    assert!(server.requests_to("/api/manifestSearch").is_empty());

    let _ = fs::remove_dir_all(app_root);
}

// ── W15-3: unsupported filters and missing required fields fail the search ──

#[test]
fn rest_search_with_an_unsupported_filter_is_an_error() {
    let tagged = PackageQuery {
        tag: Some("cli".to_owned()),
        ..PackageQuery::default()
    };
    let error = build_rest_search_body(&tagged, &info_with(&["Tag"], &[]), SearchSemantics::Many)
        .expect_err("an ignored tag filter would return untagged packages");
    assert!(error.to_string().contains("'Tag'"), "{error:#}");

    // Other unsupported fields that the request does not use are fine.
    build_rest_search_body(&query("contoso"), &info_with(&["Tag"], &[]), SearchSemantics::Many).expect("query only");
}

#[test]
fn rest_search_missing_a_required_field_is_an_error() {
    let error = build_rest_search_body(
        &query("contoso"),
        &info_with(&[], &["PackageIdentifier"]),
        SearchSemantics::Many,
    )
    .expect_err("required field missing");
    assert!(error.to_string().contains("PackageIdentifier"), "{error:#}");

    let by_id = PackageQuery {
        id: Some("Contoso.App".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(
        &by_id,
        &info_with(&[], &["packageidentifier", "Market"]),
        SearchSemantics::Many,
    )
    .expect("required field present");
    assert_eq!(fields(&body, "Filters"), ["PackageIdentifier", "Market"]);
}

// ── W15-4: an empty search response that names required/unsupported fields is an error ──

#[test]
fn rest_empty_search_response_with_unsupported_fields_is_an_error() {
    let (_server, app_root, mut repository) = rest_repository(
        "unsupported-response",
        Box::new(|path, _| match path {
            "/api/information" => Some(json_reply(REST_INFORMATION_JSON)),
            "/api/manifestSearch" => Some(json_reply(r#"{"Data":[],"UnsupportedPackageMatchFields":["Moniker"]}"#)),
            _ => None,
        }),
    );

    let error = repository
        .search_rest(0, &query("contoso"), SearchSemantics::Single)
        .expect_err("the server rejected the request shape");
    assert!(error.to_string().contains("Moniker"), "{error:#}");

    let _ = fs::remove_dir_all(app_root);
}

// ── W15-5: null optional arrays in /information ──

#[test]
fn rest_information_tolerates_null_and_non_string_arrays() {
    let info = serde_json::from_value::<RestInformation>(serde_json::json!({
        "SourceIdentifier": "Test.Rest",
        "ServerSupportedVersions": ["1.10.0", 7],
        "RequiredPackageMatchFields": null,
        "UnsupportedPackageMatchFields": null,
        "RequiredQueryParameters": ["Market", null],
        "UnsupportedQueryParameters": null,
    }))
    .expect("null optional arrays are empty, like WinGet");
    assert_eq!(info.server_supported_versions, ["1.10.0"]);
    assert!(info.required_package_match_fields.is_empty());
    assert!(info.unsupported_package_match_fields.is_empty());
    assert_eq!(info.required_query_parameters, ["Market"]);
    assert!(info.unsupported_query_parameters.is_empty());
}

// ── W15-6: search response identifier and version are trimmed ──

#[test]
fn rest_search_trims_identifier_and_version() {
    let (_server, app_root, mut repository) = rest_repository(
        "trim-search",
        Box::new(|path, _| match path {
            "/api/information" => Some(json_reply(REST_INFORMATION_JSON)),
            "/api/manifestSearch" => Some(json_reply(&format!(
                r#"{{"Data":[{}]}}"#,
                package_json(" Contoso.App ", " 1.2.3 ")
            ))),
            _ => None,
        }),
    );

    let result = repository
        .search_rest(0, &query("contoso"), SearchSemantics::Many)
        .expect("search");
    let found = &result.matches[0];
    assert_eq!(found.display.id, "Contoso.App");
    assert_eq!(found.display.version.as_deref(), Some("1.2.3"));
    let MatchLocator::Rest {
        package_id, versions, ..
    } = &found.locator
    else {
        panic!("REST locator expected");
    };
    assert_eq!(package_id, "Contoso.App");
    assert_eq!(versions[0].version, "1.2.3");

    let _ = fs::remove_dir_all(app_root);
}

// ── W15-7: manifest response version is trimmed before matching ──

fn manifest_json(version: &str, version_switches: &str, installer_switches: &str) -> String {
    format!(
        r#"{{"Data":{{"PackageIdentifier":"Contoso.App","Versions":[{{"PackageVersion":"{version}","DefaultLocale":{{"PackageLocale":"en-US","PackageName":"Contoso App","Publisher":"Contoso","ShortDescription":"App"}},{version_switches}"Installers":[{{"Architecture":"x64","InstallerType":"exe","InstallerUrl":"https://example.com/app.exe","InstallerSha256":"00",{installer_switches}"Scope":"user"}}]}}]}}}}"#
    )
}

#[test]
fn rest_manifest_version_is_trimmed_before_matching() {
    let bytes = manifest_json("1.2.3 ", "", "");
    let (manifest, _) =
        parse_rest_manifest(bytes.as_bytes(), "Contoso.App", "1.2.3", "").expect("trimmed version matches");
    assert_eq!(manifest.version, "1.2.3");
}

// ── W15-8: empty installer switches are absent ──

#[test]
fn rest_manifest_empty_switches_do_not_hide_defaults() {
    let bytes = manifest_json(
        "1.2.3",
        r#""InstallerSwitches":{"Silent":"/quiet"},"#,
        r#""InstallerSwitches":{"Silent":"","Custom":""},"#,
    );
    let (manifest, _) = parse_rest_manifest(bytes.as_bytes(), "Contoso.App", "1.2.3", "").expect("manifest");
    let switches = &manifest.installers[0].switches;
    assert_eq!(switches.silent.as_deref(), Some("/quiet"));
    assert_eq!(switches.custom, None);

    let bytes = manifest_json(
        "1.2.3",
        "",
        r#""InstallerSwitches":{"Silent":"","SilentWithProgress":"/passive"},"#,
    );
    let (manifest, _) = parse_rest_manifest(bytes.as_bytes(), "Contoso.App", "1.2.3", "").expect("manifest");
    let switches = &manifest.installers[0].switches;
    assert_eq!(switches.silent, None);
    assert_eq!(switches.silent_with_progress.as_deref(), Some("/passive"));
}

// ── W15-9: /information Cache-Control no-store/no-cache and directive casing ──

#[test]
fn cache_control_max_age_is_case_insensitive() {
    assert_eq!(parse_cache_control_max_age(Some("Public, Max-Age=42")), 42);
    assert_eq!(parse_cache_control_max_age(Some("public, max-age = 77")), 77);
    assert!(cache_control_forbids_caching(Some("public, No-Store")));
    assert!(cache_control_forbids_caching(Some("no-cache, max-age=600")));
    assert!(!cache_control_forbids_caching(Some("public, max-age=600")));
    assert!(!cache_control_forbids_caching(None));
}

fn information_requests(cache_control: &'static str) -> usize {
    let (server, app_root, mut repository) = rest_repository(
        "no-store",
        Box::new(move |path, _| match path {
            "/api/information" => Some(Reply {
                status: 200,
                headers: format!("Content-Type: application/json\r\nCache-Control: {cache_control}\r\n"),
                body: REST_INFORMATION_JSON.to_owned(),
            }),
            _ => None,
        }),
    );

    repository.load_rest_information(0).expect("first load");
    repository.load_rest_information(0).expect("second load");
    let count = server.requests_to("/api/information").len();
    let _ = fs::remove_dir_all(app_root);
    count
}

#[test]
fn rest_information_with_no_store_is_not_cached() {
    assert_eq!(information_requests("public, max-age=600"), 1);
    assert_eq!(information_requests("no-store"), 2);
    assert_eq!(information_requests("no-cache, max-age=600"), 2);
}
