//! Wave 16 (search, version comparison, installer selection) regression tests.

use std::sync::atomic::{AtomicU64, Ordering as AtomicOrdering};

use super::*;

fn temp_root(label: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "pinget-rs-wave16-{label}-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, AtomicOrdering::SeqCst)
    ))
}

fn seeded_connection(label: &str, sql: &str) -> (Connection, PathBuf) {
    let root = temp_root(label);
    fs::create_dir_all(&root).expect("create db root");
    let connection = Repository::open_sqlite_connection(root.join("index.db")).expect("open db");
    execute_batch_sql(&connection, sql).expect("seed db");
    (connection, root)
}

const V2_SCHEMA: &str = "CREATE TABLE packages (rowid INTEGER PRIMARY KEY, id TEXT, name TEXT, moniker TEXT, latest_version TEXT, hash BLOB);
     CREATE TABLE tags2 (rowid INTEGER PRIMARY KEY, tag TEXT);
     CREATE TABLE tags2_map (tag INT64, package INT64);
     CREATE TABLE commands2 (rowid INTEGER PRIMARY KEY, command TEXT);
     CREATE TABLE commands2_map (command INT64, package INT64);
     INSERT INTO packages VALUES
         (1, 'Contoso.Terminal', 'Contoso Terminal', NULL, '1.0.0', X'00AB'),
         (2, 'Contoso.Editor', 'Contoso Editor', NULL, '1.0.0', X'00AC'),
         (3, 'Fabrikam.Terminal', 'Fabrikam Terminal', NULL, '1.0.0', X'00AD');
     INSERT INTO tags2 VALUES (1, 'powertoys'), (2, 'editor');
     INSERT INTO tags2_map VALUES (1, 1), (2, 2), (1, 3);
     INSERT INTO commands2 VALUES (1, 'contoso-term');
     INSERT INTO commands2_map VALUES (1, 1);";

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
     INSERT INTO ids(rowid, id) VALUES (1, 'Contoso.Terminal'), (2, 'Contoso.Editor'), (3, 'Fabrikam.Terminal');
     INSERT INTO names(rowid, name) VALUES (1, 'Contoso Terminal'), (2, 'Contoso Editor'), (3, 'Fabrikam Terminal');
     INSERT INTO versions(rowid, version) VALUES (1, '1.0.0');
     INSERT INTO channels(rowid, channel) VALUES (1, '');
     INSERT INTO manifest(rowid, id, name, moniker, version, channel, pathpart) VALUES
         (1, 1, 1, NULL, 1, 1, 0), (2, 2, 2, NULL, 1, 1, 0), (3, 3, 3, NULL, 1, 1, 0);
     INSERT INTO tags(rowid, tag) VALUES (1, 'powertoys'), (2, 'editor');
     INSERT INTO tags_map VALUES (1, 1), (2, 2), (1, 3);";

fn v2_ids(connection: &Connection, query: &PackageQuery, semantics: SearchSemantics) -> Vec<String> {
    let (rows, _) = query_v2_matches(connection, query, semantics).expect("v2 search");
    let mut ids: Vec<_> = rows.into_iter().map(|row| row.id).collect();
    ids.sort_unstable();
    ids
}

fn v1_ids(connection: &Connection, query: &PackageQuery, semantics: SearchSemantics) -> Vec<String> {
    let (rows, _) = query_v1_matches(connection, query, semantics).expect("v1 search");
    let mut ids: Vec<_> = rows.into_iter().map(|row| row.id).collect();
    ids.sort_unstable();
    ids
}

fn filters(body: &JsonValue, key: &str) -> Vec<(String, String)> {
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

fn installer(url: &str, configure: impl FnOnce(&mut Installer)) -> Installer {
    let mut installer = Installer {
        installer_type: Some("exe".to_owned()),
        url: Some(format!("https://example.test/{url}")),
        ..Installer::default()
    };
    configure(&mut installer);
    installer
}

fn select(installers: &[Installer], query: &PackageQuery) -> Option<String> {
    select_installer_for_system(installers, query, "US", &[])
        .and_then(|installer| installer.url)
        .map(|url| url.trim_start_matches("https://example.test/").to_owned())
}

// ── W16-1: `Latest` sorts above every other version ──

#[test]
fn latest_version_sorts_above_known_versions() {
    assert_eq!(compare_version("Latest", "99.0.0"), Ordering::Greater);
    assert_eq!(compare_version("1.0", "latest"), Ordering::Less);
    assert_eq!(compare_version("LATEST", "latest"), Ordering::Equal);
    assert_eq!(compare_version("Latest", "Unknown"), Ordering::Greater);

    let mut versions = ["2.0.0", "Latest", "10.0"]
        .into_iter()
        .map(|version| VersionKey {
            version: version.to_owned(),
            channel: String::new(),
        })
        .collect::<Vec<_>>();
    sort_versions_desc(&mut versions);
    assert_eq!(versions[0].version, "Latest");
}

// ── W16-2: `> 1.0` sorts just above `1.0` ──

#[test]
fn greater_than_approximate_version_sorts_above_its_base() {
    assert_eq!(compare_version("> 2.0", "2.0"), Ordering::Greater);
    assert_eq!(compare_version("2.0.0", "> 2.0"), Ordering::Less);
    assert_eq!(compare_version("> 2.0", "2.1"), Ordering::Less);
    assert_eq!(compare_version("< 2.0", "> 2.0"), Ordering::Less);
    assert_eq!(compare_version("> 2.0", "> 2.0.0"), Ordering::Equal);
    // The existing less-than behaviour is unchanged.
    assert_eq!(compare_version("< 2.0", "2.0"), Ordering::Less);
    assert_eq!(compare_version("< 2.0", "1.9"), Ordering::Greater);
}

// ── W16-3: every pre-indexed search filter applies (AND), not only the first ──

#[test]
fn preindexed_search_applies_all_filters() {
    let (connection, root) = seeded_connection("v2-filters", V2_SCHEMA);

    let id_and_name = PackageQuery {
        id: Some("Contoso".to_owned()),
        name: Some("Terminal".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v2_ids(&connection, &id_and_name, SearchSemantics::Many),
        ["Contoso.Terminal"]
    );

    let query_and_tag = PackageQuery {
        query: Some("Terminal".to_owned()),
        tag: Some("powertoys".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v2_ids(&connection, &query_and_tag, SearchSemantics::Many),
        ["Contoso.Terminal", "Fabrikam.Terminal"]
    );
    let query_and_command = PackageQuery {
        query: Some("Terminal".to_owned()),
        command: Some("contoso-term".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v2_ids(&connection, &query_and_command, SearchSemantics::Many),
        ["Contoso.Terminal"]
    );

    let single = PackageQuery {
        query: Some("Contoso.Terminal".to_owned()),
        name: Some("Something Else".to_owned()),
        ..PackageQuery::default()
    };
    assert!(v2_ids(&connection, &single, SearchSemantics::Single).is_empty());

    drop(connection);
    let _ = fs::remove_dir_all(root);

    let (connection, root) = seeded_connection("v1-filters", V1_SCHEMA);
    assert_eq!(
        v1_ids(&connection, &id_and_name, SearchSemantics::Many),
        ["Contoso.Terminal"]
    );
    let id_and_tag = PackageQuery {
        id: Some("Fabrikam".to_owned()),
        tag: Some("powertoys".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v1_ids(&connection, &id_and_tag, SearchSemantics::Many),
        ["Fabrikam.Terminal"]
    );
    drop(connection);
    let _ = fs::remove_dir_all(root);
}

// ── W16-4: a many-search tag / command filter is a substring match unless --exact ──

#[test]
fn many_search_tag_and_command_filters_match_substrings() {
    let (connection, root) = seeded_connection("v2-tag-substring", V2_SCHEMA);
    let tag = PackageQuery {
        tag: Some("Power".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v2_ids(&connection, &tag, SearchSemantics::Many),
        ["Contoso.Terminal", "Fabrikam.Terminal"]
    );
    let command = PackageQuery {
        command: Some("term".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        v2_ids(&connection, &command, SearchSemantics::Many),
        ["Contoso.Terminal"]
    );

    let exact = PackageQuery {
        exact: true,
        ..tag.clone()
    };
    assert!(v2_ids(&connection, &exact, SearchSemantics::Many).is_empty());
    drop(connection);
    let _ = fs::remove_dir_all(root);

    let body = build_rest_search_body(&tag, &RestInformation::default(), SearchSemantics::Many).expect("body");
    assert_eq!(filters(&body, "Filters"), pairs(&[("Tag", "Substring")]));
    let body = build_rest_search_body(&command, &RestInformation::default(), SearchSemantics::Many).expect("body");
    assert_eq!(filters(&body, "Filters"), pairs(&[("Command", "Substring")]));
    let body = build_rest_search_body(&exact, &RestInformation::default(), SearchSemantics::Many).expect("body");
    assert_eq!(filters(&body, "Filters"), pairs(&[("Tag", "Exact")]));
}

// ── W16-5: --installer-type matches an archive's nested installer type ──

#[test]
fn requested_installer_type_matches_nested_archive_type() {
    let zip = installer("tool.zip", |installer| {
        installer.installer_type = Some("zip".to_owned());
        installer.nested_installer_type = Some("exe".to_owned());
    });
    let msi = installer("tool.msi", |installer| {
        installer.installer_type = Some("msi".to_owned())
    });
    let query = |installer_type: &str| PackageQuery {
        installer_type: Some(installer_type.to_owned()),
        ..PackageQuery::default()
    };

    assert_eq!(
        select(&[msi.clone(), zip.clone()], &query("exe")).as_deref(),
        Some("tool.zip")
    );
    assert_eq!(
        select(&[msi.clone(), zip.clone()], &query("ZIP")).as_deref(),
        Some("tool.zip")
    );
    assert_eq!(select(&[zip.clone(), msi], &query("msi")).as_deref(), Some("tool.msi"));
    assert_eq!(select(&[zip], &query("msi")), None);

    // `appx` and `msix` are one installer type in WinGet.
    let appx = installer("tool.appx", |installer| {
        installer.installer_type = Some("appx".to_owned())
    });
    assert_eq!(select(&[appx], &query("msix")).as_deref(), Some("tool.appx"));
}

// ── W16-6: WinGet's default installer type preference orders equal installers ──

#[test]
fn default_installer_type_preference_orders_installers() {
    let exe = installer("tool.exe", |_| {});
    let msi = installer("tool.msi", |installer| {
        installer.installer_type = Some("msi".to_owned())
    });
    let msix = installer("tool.msix", |installer| {
        installer.installer_type = Some("msix".to_owned())
    });
    let portable = installer("tool-portable.exe", |installer| {
        installer.installer_type = Some("portable".to_owned());
    });
    let zip_msi = installer("tool.zip", |installer| {
        installer.installer_type = Some("zip".to_owned());
        installer.nested_installer_type = Some("msi".to_owned());
    });
    let none = PackageQuery::default();

    assert_eq!(select(&[exe.clone(), msi.clone()], &none).as_deref(), Some("tool.msi"));
    assert_eq!(
        select(&[portable.clone(), exe.clone(), msix], &none).as_deref(),
        Some("tool.msix")
    );
    assert_eq!(select(&[portable, exe.clone()], &none).as_deref(), Some("tool.exe"));
    assert_eq!(select(&[exe.clone(), zip_msi], &none).as_deref(), Some("tool.zip"));

    // A requested type is a requirement, not a preference ordering.
    let exe_or_msi = PackageQuery {
        installer_type: Some("exe".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(select(&[msi, exe], &exe_or_msi).as_deref(), Some("tool.exe"));
}

// ── W16-7: neutral ranks below the native and emulated architectures ──

#[test]
fn neutral_architecture_ranks_below_emulated_architectures() {
    let rank = |architecture: &str, system: &str| architecture_rank(Some(architecture), None, system);
    assert!(rank("x64", "x64") > rank("x86", "x64"));
    assert!(rank("x86", "x64") > rank("neutral", "x64"));
    assert!(rank("arm64", "arm64") > rank("x64", "arm64"));
    assert!(rank("x64", "arm64") > rank("x86", "arm64"));
    assert!(rank("x86", "arm64") > rank("neutral", "arm64"));
    assert!(rank("x86", "x86") > rank("neutral", "x86"));
    assert!(rank("arm64", "x64") < 0);

    if current_architecture() == "x64" {
        let neutral = installer("neutral.exe", |installer| {
            installer.architecture = Some("neutral".to_owned())
        });
        let x86 = installer("x86.exe", |installer| installer.architecture = Some("x86".to_owned()));
        assert_eq!(
            select(&[neutral, x86], &PackageQuery::default()).as_deref(),
            Some("x86.exe")
        );
    }
}

// ── W16-8: a REST single lookup keeps --id/--name/--tag filters next to the query ──

#[test]
fn rest_single_lookup_keeps_filters_with_query() {
    let query = PackageQuery {
        query: Some("Contoso".to_owned()),
        id: Some("Contoso.Tool".to_owned()),
        tag: Some("cli".to_owned()),
        ..PackageQuery::default()
    };
    let body = build_rest_search_body(&query, &RestInformation::default(), SearchSemantics::Single).expect("body");

    assert_eq!(
        filters(&body, "Inclusions"),
        pairs(&[
            ("PackageIdentifier", "CaseInsensitive"),
            ("PackageName", "CaseInsensitive"),
            ("Moniker", "CaseInsensitive"),
        ])
    );
    assert_eq!(
        filters(&body, "Filters"),
        pairs(&[("PackageIdentifier", "CaseInsensitive"), ("Tag", "CaseInsensitive")])
    );
    assert!(body.get("Query").is_none());
}

// ── W16-9: scope preference (requested, else WinGet's default user) ranks installers ──

#[test]
fn scope_preference_ranks_installers() {
    let machine = installer("machine.exe", |installer| installer.scope = Some("machine".to_owned()));
    let user = installer("user.exe", |installer| installer.scope = Some("user".to_owned()));
    let unscoped = installer("unscoped.exe", |_| {});

    // WinGet's default `installBehavior.preferences.scope` is user.
    assert_eq!(
        select(&[machine.clone(), user.clone()], &PackageQuery::default()).as_deref(),
        Some("user.exe")
    );
    assert_eq!(
        select(&[unscoped.clone(), user.clone()], &PackageQuery::default()).as_deref(),
        Some("user.exe")
    );

    // A requested scope prefers an installer declaring it over an undeclared one.
    let machine_scope = PackageQuery {
        install_scope: Some("machine".to_owned()),
        ..PackageQuery::default()
    };
    assert_eq!(
        select(&[unscoped, machine, user], &machine_scope).as_deref(),
        Some("machine.exe")
    );

    // The system architecture (strong) beats a scope preference over an undeclared scope (weak);
    // a scope preference over the opposite scope (strong) beats the architecture.
    let system = current_architecture();
    if system != "neutral" {
        let native_unscoped = installer("native.exe", |installer| {
            installer.architecture = Some(system.to_owned())
        });
        let neutral_user = installer("neutral-user.exe", |installer| {
            installer.architecture = Some("neutral".to_owned());
            installer.scope = Some("user".to_owned());
        });
        assert_eq!(
            select(&[neutral_user.clone(), native_unscoped], &PackageQuery::default()).as_deref(),
            Some("native.exe")
        );
        let native_machine = installer("native-machine.exe", |installer| {
            installer.architecture = Some(system.to_owned());
            installer.scope = Some("machine".to_owned());
        });
        assert_eq!(
            select(&[native_machine, neutral_user], &PackageQuery::default()).as_deref(),
            Some("neutral-user.exe")
        );

        // A compatible locale match (strong) comes first in WinGet's comparator order.
        let native_german = installer("native-de.exe", |installer| {
            installer.architecture = Some(system.to_owned());
            installer.locale = Some("de-DE".to_owned());
        });
        let neutral_english = installer("neutral-en.exe", |installer| {
            installer.architecture = Some("neutral".to_owned());
            installer.locale = Some("en-US".to_owned());
        });
        let selected = select_installer_for_system(
            &[native_german, neutral_english],
            &PackageQuery::default(),
            "US",
            &["en-US".to_owned()],
        );
        assert_eq!(
            selected.and_then(|installer| installer.url).as_deref(),
            Some("https://example.test/neutral-en.exe")
        );
    }
}

// ── W16-10: REST truncation follows MaximumResults and ContinuationToken ──

#[test]
fn rest_search_truncation_needs_more_results_or_a_continuation_token() {
    let complete = serde_json::json!({ "Data": [] });
    assert!(!rest_search_truncated(&complete, 50, 50));
    assert!(!rest_search_truncated(&complete, 3, 50));
    assert!(rest_search_truncated(&complete, 51, 50));

    let more = serde_json::json!({ "Data": [], "ContinuationToken": "abc" });
    assert!(rest_search_truncated(&more, 3, 50));
    let empty_token = serde_json::json!({ "Data": [], "ContinuationToken": "" });
    assert!(!rest_search_truncated(&empty_token, 3, 50));
}
