using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 9 (sources and HTTP, round 2) regression tests.</summary>
public class Wave9DefectTests
{
    private const string InformationJson = """
        {"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}
        """;

    /// <summary>In-process HTTP stub: answers each request from a handler and records the request URIs.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "", string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static SourceRecord Source(string name, SourceKind kind, string arg) => new()
    {
        Name = name,
        Kind = kind,
        Arg = arg,
        Identifier = name,
        TrustLevel = "Trusted",
    };

    private static string CreateAppRoot() =>
        Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", "wave9-" + Guid.NewGuid().ToString("N"));

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    // ── R9-1: index-supplied relative paths must stay inside the source cache ──

    public static TheoryData<string> HostileRelativePaths() =>
    [
        @"..\..\..\..\..\..\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.cmd",
        "../escape.txt",
        "manifests/../../escape.txt",
        "manifests/a/..",
        @"C:\Windows\Temp\x.cmd",
        "C:x.cmd",
        @"\\server\share\x.cmd",
        "//server/share/x.cmd",
        "/etc/passwd",
        @"\rooted\x.cmd",
        "",
        "manifests//x.yaml",
        "manifests/./x.yaml",
        "manifests/ ../x.yaml",
        "manifests/.../x.yaml",
        "manifests/x.yaml:stream",
    ];

    [Theory]
    [MemberData(nameof(HostileRelativePaths))]
    public void SourceRelativePaths_RejectTraversalRootedDriveAndUnc(string path)
    {
        Assert.Throws<InvalidOperationException>(() => PreIndexedSource.ValidatedSourceRelativeSegments(path));
    }

    [Fact]
    public void SourceRelativePaths_AcceptManifestAndVersionDataPaths()
    {
        Assert.Equal(
            ["manifests", "c", "Contoso", "App", "1.0.0", "abcd.yaml"],
            PreIndexedSource.ValidatedSourceRelativeSegments(@"manifests\c\Contoso\App\1.0.0\abcd.yaml"));
        Assert.Equal(
            ["packages", "Contoso.App", "0123abcd", "versionData.mszyml"],
            PreIndexedSource.ValidatedSourceRelativeSegments("packages/Contoso.App/0123abcd/versionData.mszyml"));
    }

    [Fact]
    public void CachedSourceFile_WithHostilePath_WritesNothingOutsideTheCache()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "attacker bytes", "text/plain"));
            using var client = new HttpClient(handler);
            var source = Source("evil", SourceKind.PreIndexed, "https://evil.example/cache");

            foreach (var relative in new[] { "../../../escape.txt", @"..\..\..\escape.txt", "manifests/../../../../escape.txt" })
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    PreIndexedSource.GetCachedSourceFile(client, "V1_M", source, relative, null, appRoot));
                Assert.Contains("not a safe relative path", error.Message);
            }

            Assert.False(File.Exists(Path.Combine(SourceStoreManager.GetPackagedFileCacheRoot(appRoot), "escape.txt")));
            Assert.False(File.Exists(Path.Combine(appRoot, "escape.txt")));
            Assert.Empty(handler.Requests);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void CachedSourceFile_WithSafePath_IsWrittenUnderTheBucket()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "PackageIdentifier: Contoso.App", "text/plain"));
            using var client = new HttpClient(handler);
            var source = Source("good", SourceKind.PreIndexed, "https://good.example/cache");

            var bytes = PreIndexedSource.GetCachedSourceFile(
                client, "V1_M", source, @"manifests\c\Contoso\App\1.0.0\a.yaml", null, appRoot);

            Assert.Equal("PackageIdentifier: Contoso.App", Encoding.UTF8.GetString(bytes));
            var cached = Path.Combine(PreIndexedSource.TempCachePath(appRoot, "V1_M", "good"), "manifests", "c", "Contoso", "App", "1.0.0", "a.yaml");
            Assert.True(File.Exists(cached));
            Assert.Equal("https://good.example/cache/manifests/c/Contoso/App/1.0.0/a.yaml", Assert.Single(handler.Requests).ToString());
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void DotOnlyIdentifiers_DoNotNameTheParentDirectory()
    {
        Assert.Equal("_", SourceStoreManager.SanitizePathSegment(".."));
        Assert.Equal("_", SourceStoreManager.SanitizePathSegment("."));
        Assert.Equal("_", SourceStoreManager.SanitizePathSegment(""));
        Assert.Equal("Contoso.Rest", SourceStoreManager.SanitizePathSegment("Contoso.Rest"));
        Assert.Equal("a_.._b", SourceStoreManager.SanitizePathSegment("a/../b"));
    }

    // ── R9-2: source add accepts only secure remote URLs ──

    [Fact]
    public void SourceArgument_MustBeSecureAndRemote()
    {
        var notSecure = Assert.Throws<InvalidOperationException>(() => Repository.ValidateSourceArgument("http://corp/api"));
        Assert.Contains("not secure", notSecure.Message);

        foreach (var arg in new[] { "ftp://corp/api", @"C:\feeds\corp", "corp", "file:///C:/feeds/corp", "" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => Repository.ValidateSourceArgument(arg));
            Assert.Contains("not remote", error.Message);
        }

        Assert.Throws<InvalidOperationException>(() => Repository.ValidateSourceArgument("https://"));
        Repository.ValidateSourceArgument("https://corp.example/api");
        Repository.ValidateSourceArgument("HTTPS://Corp.Example/api");
    }

    // ── R9-3: REST 204 and 404 {code,message} are empty results ──

    [Fact]
    public void ReadResponseBody_MapsNoContentAndNotFoundWithCodeAndMessageToEmpty()
    {
        using (var noContent = new HttpResponseMessage(HttpStatusCode.NoContent))
            Assert.Null(RestSource.ReadResponseBody(noContent));

        using (var notFound = Response(HttpStatusCode.NotFound, """{"code":"NotFound","message":"No packages found"}"""))
            Assert.Null(RestSource.ReadResponseBody(notFound));

        using (var ok = Response(HttpStatusCode.OK, """{"Data":[]}"""))
            Assert.Equal("""{"Data":[]}""", RestSource.ReadResponseBody(ok));
    }

    [Theory]
    [InlineData("missing", "text/plain")]
    [InlineData("""{"message":"gone"}""", "application/json")]
    [InlineData("not json", "application/json")]
    public void ReadResponseBody_KeepsOtherNotFoundResponsesAsErrors(string body, string mediaType)
    {
        using var response = Response(HttpStatusCode.NotFound, body, mediaType);
        Assert.Throws<HttpRequestException>(() => RestSource.ReadResponseBody(response));
    }

    [Fact]
    public void RestSearch_NoContentIsAnEmptyResult()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var info = new RestSource.RestInformation { ServerSupportedVersions = ["1.10.0"] };

        var (results, truncated) = RestSource.Search(
            client, Source("rest", SourceKind.Rest, "https://rest.example/api"), new PackageQuery { Query = "nomatch" }, info, SearchSemantics.Many);

        Assert.Empty(results);
        Assert.False(truncated);
    }

    [Fact]
    public void RestManifest_NoContentReportsAMissingManifest()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var info = new RestSource.RestInformation { ServerSupportedVersions = ["1.10.0"] };

        var error = Assert.Throws<InvalidOperationException>(() => RestSource.FetchManifestWithDocuments(
            client, Source("rest", SourceKind.Rest, "https://rest.example/api"), info, "Contoso.App", "1.0.0", ""));
        Assert.Contains("No manifest found", error.Message);
    }

    // ── R9-5: single REST lookups are CaseInsensitive unless --exact ──

    private static List<(string Field, string MatchType)> MatchTypes(JsonObject body, string key) =>
        body[key] is JsonArray items
            ? items.Select(item => (
                item!["PackageMatchField"]!.GetValue<string>(),
                item["RequestMatch"]!["MatchType"]!.GetValue<string>())).ToList()
            : [];

    private static JsonObject SearchBody(PackageQuery query, SearchSemantics semantics, params string[] requiredFields) =>
        JsonNode.Parse(RestSource.BuildSearchBody(
            query,
            new RestSource.RestInformation { RequiredPackageMatchFields = [.. requiredFields] },
            semantics))!.AsObject();

    [Fact]
    public void RestSingleLookups_UseCaseInsensitiveMatch()
    {
        var body = SearchBody(new PackageQuery { Query = "contoso.tool" }, SearchSemantics.Single, "Market");
        Assert.Equal(
            [("PackageIdentifier", "CaseInsensitive"), ("PackageName", "CaseInsensitive"), ("Moniker", "CaseInsensitive")],
            MatchTypes(body, "Inclusions"));
        Assert.Equal([("Market", "Exact")], MatchTypes(body, "Filters"));

        var filters = SearchBody(new PackageQuery { Id = "contoso.tool", Tag = "cli" }, SearchSemantics.Single);
        Assert.Equal([("PackageIdentifier", "CaseInsensitive"), ("Tag", "CaseInsensitive")], MatchTypes(filters, "Filters"));
    }

    [Fact]
    public void RestExactAndManyLookups_KeepTheirMatchTypes()
    {
        var exact = SearchBody(new PackageQuery { Query = "Contoso.Tool", Exact = true }, SearchSemantics.Single);
        Assert.All(MatchTypes(exact, "Inclusions"), pair => Assert.Equal("Exact", pair.MatchType));

        var many = SearchBody(new PackageQuery { Id = "Contoso", Tag = "cli" }, SearchSemantics.Many);
        Assert.Equal([("PackageIdentifier", "Substring"), ("Tag", "Exact")], MatchTypes(many, "Filters"));
    }

    // ── R9-8 / R9-11: packaged layout YAML ──

    [Fact]
    public void PackagedSourcesYaml_TombstonesRemovedDefaultSources()
    {
        var store = SourceStore.Default();
        store.Sources.RemoveAll(source => source.Name == "msstore");

        var yaml = SourceStoreManager.RenderPackagedUserSourcesYaml(store);
        Assert.Contains("IsTombstone: true", yaml);

        var reloaded = SourceStoreManager.ParsePackagedSourceStore(yaml, null)!;
        Assert.Equal(["winget"], reloaded.Sources.Select(source => source.Name).ToArray());

        var defaults = SourceStoreManager.RenderPackagedUserSourcesYaml(SourceStore.Default());
        Assert.DoesNotContain("IsTombstone: true", defaults);
    }

    [Fact]
    public void PackagedYamlScalars_RoundTripQuotesAndCommentMarkers()
    {
        var corp = Source("#corp", SourceKind.Rest, "https://corp.example/it's api") with { Identifier = "Corp: \"Rest\"" };
        corp.ETag = "\"0x8DCB1A2B3C4D5E6\"";
        corp.LastModified = "Tue, 01 Jan 2030 00:00:00 GMT";
        corp.SourceVersion = "1.10";
        var store = new SourceStore { Sources = [corp] };

        var reloaded = SourceStoreManager.ParsePackagedSourceStore(
            SourceStoreManager.RenderPackagedUserSourcesYaml(store),
            SourceStoreManager.RenderPackagedMetadataYaml(store))!;
        var source = Assert.Single(reloaded.Sources, source => source.Name == "#corp");

        Assert.Equal(corp.Arg, source.Arg);
        Assert.Equal(corp.Identifier, source.Identifier);
        Assert.Equal(corp.ETag, source.ETag);
        Assert.Equal(corp.LastModified, source.LastModified);
        Assert.Equal(corp.SourceVersion, source.SourceVersion);
    }

    [Theory]
    [InlineData("'it''s'", "it's")]
    [InlineData("'\"0x8DC\"'", "\"0x8DC\"")]
    [InlineData("\"a \\\"b\\\" \\\\ c\"", "a \"b\" \\ c")]
    [InlineData("\"tab\\there \\u00e9\"", "tab\there \u00e9")]
    [InlineData("plain", "plain")]
    [InlineData("''", "")]
    public void UnquoteYamlScalar_HandlesSingleAndDoubleQuotedEscapes(string raw, string expected)
    {
        Assert.Equal(expected, SourceStoreManager.UnquoteYamlScalar(raw));
    }

    // ── R9-9: V1 search limits packages, not manifest rows ──

    private const string V1ManyVersionsSchema = """
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
        INSERT INTO ids VALUES (1, 'Contoso.App'), (2, 'Other.App');
        INSERT INTO names VALUES (1, 'Contoso App'), (2, 'Contoso.App');
        INSERT INTO versions VALUES (1, '1.0.0'), (2, '2.0.0'), (3, '3.0.0');
        INSERT INTO channels VALUES (1, '');
        INSERT INTO manifest (id, name, moniker, version, channel, pathpart) VALUES
            (1, 1, NULL, 1, 1, 0), (1, 1, NULL, 2, 1, 0), (1, 1, NULL, 3, 1, 0), (2, 2, NULL, 1, 1, 0);
        """;

    [Fact]
    public void SearchV1_LimitCountsPackagesNotVersions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = V1ManyVersionsSchema;
            seed.ExecuteNonQuery();
        }

        // `Contoso.App` matches Contoso.App by id (3 versions) and Other.App by name; a
        // single lookup fetches up to two packages.
        var (rows, truncated) = PreIndexedSource.SearchV1(connection, new PackageQuery { Query = "Contoso.App" }, SearchSemantics.Single);

        Assert.Equal(["Contoso.App", "Other.App"], rows.Select(row => row.Id).Order().ToArray());
        Assert.False(truncated);
        Assert.Equal("3.0.0", rows.Single(row => row.Id == "Contoso.App").Version);
    }

    // ── R9-10: REST source update refreshes /information and negotiates the contract ──

    [Theory]
    [InlineData(new[] { "1.0.0", "1.12.0" }, "1.12.0")]
    [InlineData(new[] { "1.12" }, "1.12.0")]
    [InlineData(new[] { "1.12.1" }, "1.12.0")]
    [InlineData(new[] { "1.9.3", "1.7.0" }, "1.9.0")]
    [InlineData(new[] { "2.0.0" }, null)]
    public void ChooseContract_MatchesOnMajorMinor(string[] serverVersions, string? expected)
    {
        Assert.Equal(expected, RestSource.ChooseContract(serverVersions));
    }

    [Fact]
    public void UpdateRest_RefetchesInformationAndStoresTheNegotiatedContract()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var versions = """["1.0.0"]""";
            var handler = new StubHandler(_ => Response(
                HttpStatusCode.OK,
                $$$"""{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":{{{versions}}}}}"""));
            using var client = new HttpClient(handler);
            var source = Source("rest", SourceKind.Rest, "https://rest.example/api");

            // Prime the (unexpired) information cache, then the server upgrades.
            RestSource.LoadInformation(client, source, appRoot);
            versions = """["1.0.0","1.12.0"]""";

            RestSource.UpdateRest(client, source, appRoot);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("1.12.0", source.SourceVersion);
            Assert.Equal(["1.0.0", "1.12.0"], RestSource.LoadInformation(client, source, appRoot).ServerSupportedVersions);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── R9-15 parity: package ids are percent-encoded in the manifest URL ──

    [Theory]
    [InlineData("Contoso.C#Tool", "https://corp.example/api/packageManifests/Contoso.C%23Tool")]
    [InlineData("A b/c?d%e+f", "https://corp.example/api/packageManifests/A%20b%2Fc%3Fd%25e%2Bf")]
    [InlineData("Contoso.App_1-2~3", "https://corp.example/api/packageManifests/Contoso.App_1-2~3")]
    public void RestManifestUrl_PercentEncodesThePackageId(string packageId, string expected)
    {
        var url = RestSource.BuildManifestRequestUrl(
            Source("corp", SourceKind.Rest, "https://corp.example/api/"), new RestSource.RestInformation(), packageId, "", "");
        Assert.Equal(expected, url);
    }
}

[Collection(RepositoryStateCollection.Name)]
public class Wave9RepositoryTests
{
    private static string CreateAppRoot() =>
        Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", "wave9-" + Guid.NewGuid().ToString("N"));

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    private static Repository OpenEmpty(string appRoot)
    {
        var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot, SourceMode = SourceMode.Private });
        foreach (var source in repo.ListSources())
            repo.RemoveSource(source.Name);
        return repo;
    }

    // ── R9-2 ──

    [Fact]
    public void AddSource_RejectsPlainHttpAndKeepsTheStoreUnchanged()
    {
        var appRoot = CreateAppRoot();
        try
        {
            using var repo = OpenEmpty(appRoot);

            foreach (var kind in new[] { SourceKind.Rest, SourceKind.PreIndexed })
            {
                var error = Assert.Throws<InvalidOperationException>(() => repo.AddSource("corp", "http://corp/api", kind));
                Assert.Contains("not secure", error.Message);
            }
            Assert.Empty(repo.ListSources());

            repo.AddSource("corp", "https://corp.example/api", SourceKind.Rest);
            Assert.Single(repo.ListSources());
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── R9-7 parity: remove/reset clear per-identifier source file caches ──

    private static readonly string[] Buckets = ["REST_M", "V1_M", "V2_M", "V2_PVD"];

    private static void SeedCaches(string appRoot, string identifier)
    {
        foreach (var bucket in Buckets)
        {
            var dir = PreIndexedSource.TempCachePath(appRoot, bucket, identifier);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stale"), "stale");
        }
    }

    [Fact]
    public void RemoveAndResetSource_ClearPerIdentifierFileCaches()
    {
        var appRoot = CreateAppRoot();
        try
        {
            using var repo = OpenEmpty(appRoot);
            repo.AddSource("corp", "https://corp.example/cache", SourceKind.PreIndexed);
            repo.AddSource("other", "https://other.example/cache", SourceKind.PreIndexed);
            SeedCaches(appRoot, "corp");
            SeedCaches(appRoot, "other");

            repo.ResetSource("corp");
            foreach (var bucket in Buckets)
            {
                Assert.False(Directory.Exists(PreIndexedSource.TempCachePath(appRoot, bucket, "corp")), bucket);
                Assert.True(Directory.Exists(PreIndexedSource.TempCachePath(appRoot, bucket, "other")), bucket);
            }

            SeedCaches(appRoot, "corp");
            repo.RemoveSource("corp");
            foreach (var bucket in Buckets)
            {
                Assert.False(Directory.Exists(PreIndexedSource.TempCachePath(appRoot, bucket, "corp")), bucket);
                Assert.True(Directory.Exists(PreIndexedSource.TempCachePath(appRoot, bucket, "other")), bucket);
            }
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }
}
