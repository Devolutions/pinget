using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 2 (package sources) regression tests.</summary>
public class Wave2DefectTests
{
    // ── S1 / S5: REST search request shape ──

    private static JsonObject SearchBody(PackageQuery query, SearchSemantics semantics, params string[] requiredFields) =>
        JsonNode.Parse(RestSource.BuildSearchBody(
            query,
            new RestSource.RestInformation { RequiredPackageMatchFields = [.. requiredFields] },
            semantics))!.AsObject();

    [Fact]
    public void RestSingleQuery_PutsIdentityFieldsInInclusions()
    {
        var body = SearchBody(new PackageQuery { Query = "Contoso.App" }, SearchSemantics.Single);

        var inclusions = body["Inclusions"]!.AsArray();
        Assert.Equal(
            ["PackageIdentifier", "PackageName", "Moniker"],
            inclusions.Select(item => item!["PackageMatchField"]!.GetValue<string>()).ToArray());
        Assert.All(inclusions, item =>
        {
            // Wave 9 (R9-5): single lookups are CaseInsensitive unless --exact, like WinGet.
            Assert.Equal("CaseInsensitive", item!["RequestMatch"]!["MatchType"]!.GetValue<string>());
            Assert.Equal("Contoso.App", item["RequestMatch"]!["KeyWord"]!.GetValue<string>());
        });
        Assert.False(body.ContainsKey("Filters"), "identity fields must not be ANDed as Filters");
        Assert.False(body.ContainsKey("Query"));
    }

    [Fact]
    public void RestSingleQuery_KeepsRequiredMarketAsTheOnlyFilter()
    {
        var body = SearchBody(new PackageQuery { Query = "Contoso.App" }, SearchSemantics.Single, "Market");

        Assert.Equal(3, body["Inclusions"]!.AsArray().Count);
        var filter = Assert.Single(body["Filters"]!.AsArray());
        Assert.Equal("Market", filter!["PackageMatchField"]!.GetValue<string>());
    }

    [Fact]
    public void RestSingleIdFilter_UsesCaseInsensitiveMatch()
    {
        var query = new PackageQuery { Id = "Contoso.App" };

        var single = SearchBody(query, SearchSemantics.Single)["Filters"]!.AsArray()[0]!;
        Assert.Equal("PackageIdentifier", single["PackageMatchField"]!.GetValue<string>());
        // Wave 9 (R9-5): CaseInsensitive unless --exact, like WinGet.
        Assert.Equal("CaseInsensitive", single["RequestMatch"]!["MatchType"]!.GetValue<string>());

        var many = SearchBody(query, SearchSemantics.Many)["Filters"]!.AsArray()[0]!;
        Assert.Equal("Substring", many["RequestMatch"]!["MatchType"]!.GetValue<string>());
    }

    // ── S3: REST information cache lifetime ──

    [Fact]
    public void InformationCacheLifetime_HonorsAndClampsMaxAge()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), RestSource.InformationCacheLifetime(null));
        Assert.Equal(TimeSpan.FromSeconds(120), RestSource.InformationCacheLifetime(TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.Zero, RestSource.InformationCacheLifetime(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromDays(1), RestSource.InformationCacheLifetime(TimeSpan.FromDays(3650)));
        Assert.Equal(TimeSpan.FromDays(1), RestSource.InformationCacheLifetime(TimeSpan.MaxValue));
    }

    // ── S4: sources.json key format shared with Rust ──

    // Exactly what the Rust SourceRecord serializer writes (camelCase, `eTag`, 9-digit fractions).
    private const string RustSourcesJson = """
        {
          "sources": [
            {
              "name": "contoso",
              "kind": "Rest",
              "arg": "https://contoso.example/api",
              "identifier": "Contoso.Rest",
              "trustLevel": "Trusted",
              "explicit": true,
              "priority": 3,
              "lastUpdate": "2024-05-01T12:34:56.123456789Z",
              "sourceVersion": "1.10.0",
              "eTag": "\"abc\"",
              "lastModified": "Wed, 01 May 2024 12:34:56 GMT"
            }
          ]
        }
        """;

    private static void AssertContosoSource(SourceRecord record)
    {
        Assert.Equal("contoso", record.Name);
        Assert.Equal(SourceKind.Rest, record.Kind);
        Assert.Equal("Contoso.Rest", record.Identifier);
        Assert.Equal("Trusted", record.TrustLevel);
        Assert.True(record.Explicit);
        Assert.Equal(3, record.Priority);
        var lastUpdate = record.LastUpdate!.Value;
        Assert.Equal(DateTimeKind.Utc, lastUpdate.Kind);
        Assert.Equal(new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc), lastUpdate.AddTicks(-(lastUpdate.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal("1.10.0", record.SourceVersion);
        Assert.Equal("\"abc\"", record.ETag);
        Assert.Equal("Wed, 01 May 2024 12:34:56 GMT", record.LastModified);
    }

    [Fact]
    public void SourceStore_ReadsRustFormat()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(appRoot);
            File.WriteAllText(Path.Combine(appRoot, "sources.json"), RustSourcesJson);

            var store = SourceStoreManager.Load(appRoot);

            AssertContosoSource(Assert.Single(store.Sources));
        }
        finally
        {
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void SourceStore_WritesKeysRustReads()
    {
        var store = JsonSerializer.Deserialize(RustSourcesJson, SourceStoreContext.Default.SourceStore)!;
        var json = JsonSerializer.Serialize(store, SourceStoreContext.Default.SourceStore);
        var record = JsonNode.Parse(json)!["sources"]![0]!.AsObject();

        foreach (var key in new[] { "name", "kind", "arg", "identifier", "trustLevel", "explicit", "priority", "lastUpdate", "sourceVersion", "eTag", "lastModified" })
            Assert.True(record.ContainsKey(key), $"missing {key} in {json}");
        Assert.Equal("Rest", record["kind"]!.GetValue<string>());
        Assert.EndsWith("Z", record["lastUpdate"]!.GetValue<string>());

        AssertContosoSource(Assert.Single(JsonSerializer.Deserialize(json, SourceStoreContext.Default.SourceStore)!.Sources));
    }

    // ── S10: LIKE wildcards in user input ──

    [Fact]
    public void MatchParameter_EscapesLikeWildcards()
    {
        Assert.Equal(@"100\%\_a\\b", PreIndexedSource.MatchParameter(@"100%_a\b", exact: true));
        Assert.Equal(@"%100\%%", PreIndexedSource.MatchParameter("100%", exact: false));
        Assert.Equal("%plain%", PreIndexedSource.MatchParameter("plain", exact: false));
    }

    private static SqliteConnection SeedDatabase(string sql)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private const string V1Schema = """
        CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT);
        CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT);
        CREATE TABLE monikers (rowid INTEGER PRIMARY KEY, moniker TEXT);
        CREATE TABLE versions (rowid INTEGER PRIMARY KEY, version TEXT);
        CREATE TABLE channels (rowid INTEGER PRIMARY KEY, channel TEXT);
        CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64, name INT64, moniker INT64, version INT64, channel INT64, pathpart INT64);
        CREATE TABLE tags (rowid INTEGER PRIMARY KEY, tag TEXT);
        CREATE TABLE tags_map (tag INT64, manifest INT64);
        CREATE TABLE commands (rowid INTEGER PRIMARY KEY, command TEXT);
        CREATE TABLE commands_map (command INT64, manifest INT64);
        INSERT INTO ids VALUES (1, 'Contoso_App'), (2, 'ContosoXApp');
        INSERT INTO names VALUES (1, 'Get 100% Done'), (2, 'Get 1000 Things');
        INSERT INTO versions VALUES (1, '1.0.0');
        INSERT INTO channels VALUES (1, '');
        INSERT INTO manifest (id, name, moniker, version, channel, pathpart) VALUES (1, 1, NULL, 1, 1, 0), (2, 2, NULL, 1, 1, 0);
        """;

    private const string V2Schema = """
        CREATE TABLE packages (rowid INTEGER PRIMARY KEY, id TEXT, name TEXT, moniker TEXT, latest_version TEXT, hash BLOB);
        CREATE TABLE tags2 (rowid INTEGER PRIMARY KEY, tag TEXT);
        CREATE TABLE tags2_map (tag INT64, package INT64);
        CREATE TABLE commands2 (rowid INTEGER PRIMARY KEY, command TEXT);
        CREATE TABLE commands2_map (command INT64, package INT64);
        INSERT INTO packages VALUES
            (1, 'Contoso_App', 'Get 100% Done', NULL, '1.0.0', X'00AB'),
            (2, 'ContosoXApp', 'Get 1000 Things', NULL, '1.0.0', X'00AC');
        """;

    private static readonly PackageQuery ExactIdQuery = new() { Id = "Contoso_App", Exact = true };
    private static readonly PackageQuery PercentQuery = new() { Query = "100%" };

    [Fact]
    public void SearchV1_TreatsPercentAndUnderscoreLiterally()
    {
        using var connection = SeedDatabase(V1Schema);

        var (byId, _) = PreIndexedSource.SearchV1(connection, ExactIdQuery, SearchSemantics.Single);
        Assert.Equal(["Contoso_App"], byId.Select(row => row.Id).ToArray());

        var (byQuery, _) = PreIndexedSource.SearchV1(connection, PercentQuery, SearchSemantics.Many);
        Assert.Equal(["Get 100% Done"], byQuery.Select(row => row.Name).ToArray());
    }

    [Fact]
    public void SearchV2_TreatsPercentAndUnderscoreLiterally()
    {
        using var connection = SeedDatabase(V2Schema);

        var (byId, _) = PreIndexedSource.SearchV2(connection, ExactIdQuery, SearchSemantics.Single);
        Assert.Equal(["Contoso_App"], byId.Select(row => row.Id).ToArray());

        var (byQuery, _) = PreIndexedSource.SearchV2(connection, PercentQuery, SearchSemantics.Many);
        Assert.Equal(["Get 100% Done"], byQuery.Select(row => row.Name).ToArray());
    }

    // ── S14: case-insensitive version and channel selection (Rust parity) ──

    [Fact]
    public void VersionSelection_IgnoresVersionAndChannelCase()
    {
        List<PreIndexedSource.V1VersionRow> v1 = [new() { Version = "1.0.0-BETA", Channel = "Preview" }];
        Assert.Equal("1.0.0-BETA", Repository.SelectV1VersionForTesting(v1, "1.0.0-beta", "preview").Version);
        Assert.Equal("Preview", Repository.SelectV1VersionForTesting(v1, null, "PREVIEW").Channel);

        List<PreIndexedSource.V2VersionDataEntry> v2 = [new() { Version = "3.0.0-RC" }];
        Assert.Equal("3.0.0-RC", Repository.SelectV2VersionForTesting(v2, "3.0.0-rc").Version);

        List<VersionKey> rest = [new() { Version = "2.0.0-RC", Channel = "Beta" }];
        Assert.Equal("2.0.0-RC", Repository.SelectRestVersionForTesting(rest, "2.0.0-rc", "beta").Version);
        Assert.Equal("Beta", Repository.SelectRestVersionForTesting(rest, null, "BETA").Channel);
    }
}

[Collection(RepositoryStateCollection.Name)]
public class Wave2RepositoryTests
{
    private const string InformationJson = """
        {"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}
        """;

    private static string CreateAppRoot() =>
        Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    private static Repository OpenWithSources(string appRoot, params (string Name, string Arg, bool Explicit)[] sources)
    {
        var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot, SourceMode = SourceMode.Private });
        foreach (var source in repo.ListSources())
            repo.RemoveSource(source.Name);
        foreach (var source in sources)
            repo.AddSourceUnvalidated(source.Name, source.Arg, SourceKind.Rest, trustLevel: "trusted", explicitSource: source.Explicit);
        return repo;
    }

    // ── S6 / S13: source update ──

    [Fact]
    public void UpdateSources_ReportsFailuresAndSavesSuccessfulSources()
    {
        using var server = new StubRestServer(path => path switch
        {
            "/good/information" => (200, InformationJson),
            "/bad/information" => (500, ""),
            _ => (404, ""),
        });
        var appRoot = CreateAppRoot();
        try
        {
            List<SourceUpdateResult> results;
            using (var repo = OpenWithSources(appRoot, ("bad", $"{server.Url}/bad", false), ("good", $"{server.Url}/good", false)))
                results = repo.UpdateSources();

            Assert.Equal(["bad", "good"], results.Select(r => r.Name).ToArray());
            Assert.False(results[0].Success);
            Assert.False(string.IsNullOrEmpty(results[0].Detail));
            Assert.True(results[1].Success, results[1].Detail);

            var saved = SourceStoreManager.Load(appRoot);
            var good = saved.Sources.Single(s => s.Name == "good");
            Assert.NotNull(good.LastUpdate);
            Assert.Equal("1.10.0", good.SourceVersion);
            Assert.Null(saved.Sources.Single(s => s.Name == "bad").LastUpdate);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void UpdateSources_WithoutName_IncludesExplicitSources()
    {
        using var server = new StubRestServer(path => path == "/explicit/information" ? (200, InformationJson) : (404, ""));
        var appRoot = CreateAppRoot();
        try
        {
            using var repo = OpenWithSources(appRoot, ("explicit-src", $"{server.Url}/explicit", true));

            var result = Assert.Single(repo.UpdateSources());

            Assert.Equal("explicit-src", result.Name);
            Assert.True(result.Success, result.Detail);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── S11: source removal ──

    [Fact]
    public void RemoveSource_MatchesNameCaseInsensitively()
    {
        var appRoot = CreateAppRoot();
        try
        {
            using (var repo = OpenWithSources(appRoot, ("Contoso", "https://contoso.example", false)))
            {
                repo.RemoveSource("CONTOSO");
                Assert.Empty(repo.ListSources());
            }

            Assert.Empty(SourceStoreManager.Load(appRoot).Sources);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    private sealed class StubRestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        public StubRestServer(Func<string, (int Status, string Body)> handler)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception) when (!_listener.IsListening)
                    {
                        break;
                    }

                    var (status, body) = handler(context.Request.Url?.AbsolutePath ?? "/");
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }
}
