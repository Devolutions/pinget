//! Wave 14 (pre-indexed sources, source store, settings) regression tests.

use std::io::{Read as _, Write as _};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering as AtomicOrdering};
use std::thread::{self, JoinHandle};

use super::*;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "pinget-rs-wave14-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ))
}

fn source(name: &str, kind: SourceKind, arg: &str, identifier: &str) -> SourceRecord {
    SourceRecord {
        name: name.to_owned(),
        kind,
        arg: arg.to_owned(),
        identifier: identifier.to_owned(),
        trust_level: "Trusted".to_owned(),
        explicit: false,
        priority: 0,
        last_update: None,
        source_version: None,
        etag: None,
        last_modified: None,
        publisher: None,
        trusted_ca: Vec::new(),
    }
}

fn lock_runner() -> std::sync::MutexGuard<'static, ()> {
    SYSTEM_WINGET_RUNNER_TEST_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Swaps the global WinGet command runner and restores the previous one on drop.
struct RunnerSwap {
    previous: WingetSourceCommandRunner,
    _guard: std::sync::MutexGuard<'static, ()>,
}

impl RunnerSwap {
    fn new(runner: WingetSourceCommandRunner) -> Self {
        let guard = lock_runner();
        let mut current = SYSTEM_WINGET_SOURCE_COMMAND_RUNNER.write().expect("runner lock");
        let previous = *current;
        *current = runner;
        Self {
            previous,
            _guard: guard,
        }
    }
}

impl Drop for RunnerSwap {
    fn drop(&mut self) {
        *SYSTEM_WINGET_SOURCE_COMMAND_RUNNER.write().expect("runner lock") = self.previous;
    }
}

const CORP_EXPORT_LINE: &str = r#"{"Arg":"https://packages.contoso.test/api","Data":"","Explicit":false,"Identifier":"Contoso.Corp","Name":"corp","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}"#;

fn ok_result(stdout: &str) -> Result<WingetSourceCommandResult> {
    Ok(WingetSourceCommandResult {
        exit_code: 0,
        stdout: stdout.to_owned(),
        stderr: String::new(),
    })
}

fn corp_export_runner(_args: &[String]) -> Result<WingetSourceCommandResult> {
    ok_result(&format!("{CORP_EXPORT_LINE}\n"))
}

// ── W14-1: `winget source export` with no sources prints a message, not JSON ──

#[test]
fn system_winget_export_without_sources_is_an_empty_list() {
    // ExportSourceList prints the (localized) SourceListNoSources string when nothing is configured.
    let sources = parse_system_winget_source_export("There are no sources configured.\r\n").expect("empty export");
    assert!(sources.is_empty());

    // A JSON line mixed with something else is still rejected.
    assert!(parse_system_winget_source_export(&format!("{CORP_EXPORT_LINE}\nnot a source\n")).is_err());
}

// ── W14-3: mirror metadata is not carried onto a source whose argument changed ──

#[test]
fn mirror_metadata_is_dropped_when_the_source_argument_changed() {
    let mut previous = source("corp", SourceKind::Rest, "https://old.contoso.test/api", "Contoso.Corp");
    previous.etag = Some("\"old-etag\"".to_owned());
    previous.source_version = Some("1.4.0".to_owned());
    previous.last_update = Some(Utc::now());
    let prior = SourceStore {
        sources: vec![previous],
    };

    let mut moved = SourceStore {
        sources: vec![source(
            "corp",
            SourceKind::Rest,
            "https://new.contoso.test/api",
            "Contoso.Corp",
        )],
    };
    merge_source_cache_metadata(&mut moved, &prior);
    let corp = &moved.sources[0];
    assert_eq!(corp.etag, None);
    assert_eq!(corp.source_version, None);
    assert_eq!(corp.last_update, None);

    // The same source (a trailing slash aside) keeps its metadata.
    let mut same = SourceStore {
        sources: vec![source(
            "corp",
            SourceKind::Rest,
            "https://old.contoso.test/api/",
            "contoso.corp",
        )],
    };
    merge_source_cache_metadata(&mut same, &prior);
    assert_eq!(same.sources[0].etag.as_deref(), Some("\"old-etag\""));
}

// ── W14-4: an unreadable mirror cache is re-exported instead of failing forever ──

#[test]
fn corrupt_system_winget_mirror_store_is_re_exported() {
    let app_root = temp_root("corrupt-mirror");
    fs::create_dir_all(&app_root).expect("app root");
    fs::write(
        system_winget_mirror_store_path(&app_root),
        b"{\"sources\": [ {\"name\": ",
    )
    .expect("corrupt mirror");

    let result = {
        let _runner = RunnerSwap::new(corp_export_runner);
        load_effective_source_store(&app_root, SourceMode::SystemWingetMirror)
    };
    let reloaded = load_system_winget_mirror_store(&app_root);
    let _ = fs::remove_dir_all(&app_root);

    let (mode, store) = result.expect("mirror store");
    assert_eq!(mode, EffectiveSourceMode::SystemWingetMirror);
    assert_eq!(
        store
            .sources
            .iter()
            .map(|source| source.name.as_str())
            .collect::<Vec<_>>(),
        ["corp"]
    );
    assert!(reloaded.expect("rewritten mirror").is_some());
}

// ── W14-5: direct-mode update by identifier names the source for winget ──

static DIRECT_UPDATE_CALLS: Mutex<Vec<Vec<String>>> = Mutex::new(Vec::new());

fn direct_update_runner(args: &[String]) -> Result<WingetSourceCommandResult> {
    DIRECT_UPDATE_CALLS.lock().expect("calls").push(args.to_vec());
    if args.get(1).map(String::as_str) == Some("export") {
        return corp_export_runner(args);
    }

    // `winget source update --name X` only knows source names.
    let name = args
        .iter()
        .position(|arg| arg == "--name")
        .and_then(|index| args.get(index + 1));
    match name {
        Some(name) if !name.eq_ignore_ascii_case("corp") => Ok(WingetSourceCommandResult {
            exit_code: 1,
            stdout: String::new(),
            stderr: format!("Did not find a source named: {name}"),
        }),
        _ => ok_result("Updating source: corp...\nDone"),
    }
}

#[test]
fn direct_mode_update_by_identifier_passes_the_source_name_to_winget() {
    let app_root = temp_root("direct-update");
    let result = (|| -> Result<Vec<SourceUpdateResult>> {
        let _runner = RunnerSwap::new(direct_update_runner);
        DIRECT_UPDATE_CALLS.lock().expect("calls").clear();
        let mut repository = Repository::open_with_options(RepositoryOptions::new(app_root.clone()))?;
        repository.source_mode = EffectiveSourceMode::SystemWingetDirect;
        repository.update_sources(Some("Contoso.Corp"))
    })();
    let calls = DIRECT_UPDATE_CALLS.lock().expect("calls").clone();
    let _ = fs::remove_dir_all(&app_root);

    let results = result.expect("update by identifier");
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].name, "corp");
    let update = calls
        .iter()
        .find(|call| call.get(1).map(String::as_str) == Some("update"))
        .expect("update call");
    assert!(
        update.windows(2).any(|pair| pair[0] == "--name" && pair[1] == "corp"),
        "{update:?}"
    );
}

// ── W14-7: a broken source2.msix falls back to source.msix ──

struct BinaryRoute {
    path: &'static str,
    status: u16,
    body: Vec<u8>,
}

struct StubServer {
    addr: SocketAddr,
    running: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl StubServer {
    fn start(routes: Vec<BinaryRoute>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind stub server");
        let addr = listener.local_addr().expect("stub server address");
        let running = Arc::new(AtomicBool::new(true));
        let thread_running = Arc::clone(&running);
        let handle = thread::spawn(move || {
            while thread_running.load(AtomicOrdering::SeqCst) {
                let Ok((mut stream, _)) = listener.accept() else {
                    continue;
                };
                let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(5)));
                let mut received = Vec::new();
                let mut buffer = [0u8; 4096];
                while !String::from_utf8_lossy(&received).contains("\r\n\r\n") {
                    match stream.read(&mut buffer) {
                        Ok(0) | Err(_) => break,
                        Ok(read) => received.extend_from_slice(&buffer[..read]),
                    }
                }
                let request = String::from_utf8_lossy(&received).into_owned();
                let mut request_line = request.lines().next().unwrap_or_default().split_whitespace();
                let method = request_line.next().unwrap_or("GET").to_owned();
                let path = request_line.next().unwrap_or("/").to_owned();
                let (status, body) = routes
                    .iter()
                    .find(|route| route.path == path)
                    .map(|route| (route.status, route.body.clone()))
                    .unwrap_or((404, Vec::new()));
                let mut response = format!(
                    "HTTP/1.1 {status} Stub\r\nContent-Length: {}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n",
                    body.len()
                )
                .into_bytes();
                if method != "HEAD" {
                    response.extend_from_slice(&body);
                }
                let _ = stream.write_all(&response);
            }
        });
        Self {
            addr,
            running,
            handle: Some(handle),
        }
    }

    fn url(&self) -> String {
        format!("http://{}", self.addr)
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

fn zip_with(entries: &[(&str, &[u8])]) -> Vec<u8> {
    let mut bytes = Vec::new();
    {
        let mut archive = zip::ZipWriter::new(Cursor::new(&mut bytes));
        for (name, content) in entries {
            archive
                .start_file(*name, zip::write::SimpleFileOptions::default())
                .expect("zip entry");
            archive.write_all(content).expect("zip content");
        }
        archive.finish().expect("zip finish");
    }
    bytes
}

#[test]
fn preindexed_update_falls_back_to_source_msix_when_source2_has_no_index() {
    let server = StubServer::start(vec![
        BinaryRoute {
            path: "/source2.msix",
            status: 200,
            body: zip_with(&[("AppxManifest.xml", b"<Package/>")]),
        },
        BinaryRoute {
            path: "/source.msix",
            status: 200,
            body: zip_with(&[("Public/index.db", b"v1 index bytes")]),
        },
    ]);
    let app_root = temp_root("source2-fallback");
    fs::create_dir_all(&app_root).expect("app root");
    let mut repository =
        Repository::open_with_options(RepositoryOptions::new(app_root.clone())).expect("open repository");
    repository.store.sources = vec![source("corp", SourceKind::PreIndexed, &server.url(), "Contoso.Corp")];

    let results = repository.update_sources(Some("corp")).expect("update");
    let index = fs::read(repository.preindexed_index_path(&repository.store.sources[0]));
    drop(repository);
    let _ = fs::remove_dir_all(&app_root);

    assert_eq!(results.len(), 1);
    assert!(results[0].success, "{}", results[0].detail);
    assert!(results[0].detail.contains("source.msix"), "{}", results[0].detail);
    assert_eq!(index.expect("index"), b"v1 index bytes");
}

// ── W14-8: a pathparts cycle in a V1 index is an error, not an endless loop ──

#[test]
fn v1_relative_path_with_a_pathparts_cycle_fails() {
    let root = temp_root("pathparts-cycle");
    fs::create_dir_all(&root).expect("root");
    let db_path = root.join("index.db");
    let (sender, receiver) = std::sync::mpsc::channel();
    thread::spawn(move || {
        let result = (|| -> Result<String> {
            let connection = Repository::open_sqlite_connection(db_path)?;
            execute_batch_sql(
                &connection,
                "CREATE TABLE pathparts (parent INT64, pathpart TEXT);
                 INSERT INTO pathparts(rowid, parent, pathpart) VALUES (1, 2, 'a'), (2, 1, 'b');",
            )?;
            resolve_v1_relative_path(&connection, 1)
        })();
        let _ = sender.send(result.map_err(|error| format!("{error:#}")));
    });

    let outcome = receiver.recv_timeout(std::time::Duration::from_secs(20));
    let _ = fs::remove_dir_all(&root);
    let result = outcome.expect("resolving a pathparts cycle must terminate");
    let error = result.expect_err("a pathparts cycle must be rejected");
    assert!(error.contains("cycle"), "{error}");
}

// ── W14-9: packaged user_sources keeps the Identifier key WinGet reads ──

#[test]
fn packaged_sources_yaml_writes_the_identifier_key() {
    let store = SourceStore {
        sources: vec![source(
            "corp",
            SourceKind::Rest,
            "https://packages.contoso.test/api",
            "Contoso.Rest",
        )],
    };

    let yaml = render_packaged_sources_yaml(&store);
    let entries = parse_packaged_yaml_entries(Some(&yaml));
    let corp = entries
        .iter()
        .find(|entry| entry.get("Name") == Some(&Some("corp".to_owned())))
        .expect("corp entry");
    assert_eq!(corp.get("Identifier"), Some(&Some("Contoso.Rest".to_owned())), "{yaml}");
}

// ── W14-10: packaged sources are ordered by priority, otherwise as listed ──

#[test]
fn packaged_sources_are_ordered_by_descending_priority() {
    let user_sources = "Sources:
  - Name: zeta
    Type: Microsoft.Rest
    Arg: https://zeta.contoso.test/api
    Data: Zeta
    Priority: 0
    IsTombstone: false
  - Name: high
    Type: Microsoft.Rest
    Arg: https://high.contoso.test/api
    Data: High
    Priority: 5
    IsTombstone: false
";

    let store = parse_packaged_source_store(Some(user_sources), None).expect("store");
    let names: Vec<_> = store.sources.iter().map(|source| source.name.as_str()).collect();
    assert_eq!(names, ["high", "winget", "msstore", "zeta"]);
}
