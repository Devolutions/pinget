using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 15 (REST sources and HTTP) regression tests.</summary>
public class Wave15DefectTests
{
    private const string InformationJson = """
        {"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}
        """;

    /// <summary>In-process HTTP stub: answers each request from a handler and records the requests.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static SourceRecord RestSourceRecord() => new()
    {
        Name = "rest",
        Kind = SourceKind.Rest,
        Arg = "https://rest.example/api",
        Identifier = "rest",
        TrustLevel = "Trusted",
    };

    private static RestSource.RestInformation Info(string[]? unsupported = null, string[]? required = null) => new()
    {
        ServerSupportedVersions = ["1.10.0"],
        UnsupportedPackageMatchFields = [.. unsupported ?? []],
        RequiredPackageMatchFields = [.. required ?? []],
    };

    private static string PackageJson(string id, string version) =>
        $$"""{"PackageIdentifier":"{{id}}","PackageName":"{{id}}","Publisher":"Contoso","Versions":[{"PackageVersion":"{{version}}"}]}""";

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static List<string> Fields(JsonObject body, string key) =>
        body[key] is JsonArray items
            ? items.Select(item => item!["PackageMatchField"]!.GetValue<string>()).ToList()
            : [];

    private static JsonObject Body(PackageQuery query, RestSource.RestInformation info, SearchSemantics semantics) =>
        JsonNode.Parse(RestSource.BuildSearchBody(query, info, semantics))!.AsObject();

    // ── W15-1: manifestSearch pages through ContinuationToken ──

    [Fact]
    public void RestSearch_FollowsContinuationTokens()
    {
        var handler = new StubHandler(request => Header(request, "ContinuationToken") switch
        {
            null => Json($$"""{"Data":[{{PackageJson("Contoso.One", "1.0.0")}}],"ContinuationToken":"page-2"}"""),
            "page-2" => Json($$"""{"Data":[{{PackageJson("Contoso.Two", "2.0.0")}}]}"""),
            _ => Json("""{"Data":[]}"""),
        });
        using var client = new HttpClient(handler);

        var (results, truncated) = RestSource.Search(
            client, RestSourceRecord(), new PackageQuery { Query = "contoso" }, Info(), SearchSemantics.Many);

        Assert.Equal(["Contoso.One", "Contoso.Two"], results.Select(result => result.PackageId).ToArray());
        Assert.False(truncated);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("page-2", Header(handler.Requests[1], "ContinuationToken"));
    }

    [Fact]
    public void RestSearch_RejectsARepeatedContinuationToken()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Json($$"""{"Data":[{{PackageJson("Contoso.Loop", "1.0.0")}}],"ContinuationToken":"same"}""")));

        var error = Assert.Throws<InvalidOperationException>(() => RestSource.Search(
            client, RestSourceRecord(), new PackageQuery { Query = "contoso" }, Info(), SearchSemantics.Many));
        Assert.Contains("repeated continuation token", error.Message);
    }

    // ── W15-2: unsupported package match fields are dropped from Inclusions ──

    [Fact]
    public void RestSingleSearch_DropsUnsupportedInclusions()
    {
        var body = Body(new PackageQuery { Query = "vscode" }, Info(unsupported: ["moniker"]), SearchSemantics.Single);
        Assert.Equal(["PackageIdentifier", "PackageName"], Fields(body, "Inclusions"));
    }

    [Fact]
    public void RestSingleSearch_WithNoSupportedInclusion_IsEmptyWithoutARequest()
    {
        var handler = new StubHandler(_ => Json($$"""{"Data":[{{PackageJson("Contoso.App", "1.0.0")}}]}"""));
        using var client = new HttpClient(handler);

        var (results, _) = RestSource.Search(
            client,
            RestSourceRecord(),
            new PackageQuery { Query = "contoso" },
            Info(unsupported: ["PackageIdentifier", "PackageName", "Moniker"]),
            SearchSemantics.Single);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    // ── W15-3: unsupported filters and missing required fields fail the search ──

    [Fact]
    public void RestSearch_WithAnUnsupportedFilter_IsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RestSource.BuildSearchBody(
            new PackageQuery { Tag = "cli" }, Info(unsupported: ["Tag"]), SearchSemantics.Many));
        Assert.Contains("'Tag'", error.Message);

        // Other unsupported fields that the request does not use are fine.
        RestSource.BuildSearchBody(new PackageQuery { Query = "contoso" }, Info(unsupported: ["Tag"]), SearchSemantics.Many);
    }

    [Fact]
    public void RestSearch_MissingARequiredField_IsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RestSource.BuildSearchBody(
            new PackageQuery { Query = "contoso" }, Info(required: ["PackageIdentifier"]), SearchSemantics.Many));
        Assert.Contains("PackageIdentifier", error.Message);

        var body = Body(new PackageQuery { Id = "Contoso.App" }, Info(required: ["packageidentifier", "Market"]), SearchSemantics.Many);
        Assert.Equal(["PackageIdentifier", "Market"], Fields(body, "Filters"));
    }

    // ── W15-4: an empty search response that names required/unsupported fields is an error ──

    [Fact]
    public void RestEmptySearchResponse_WithUnsupportedFields_IsAnError()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Json("""{"Data":[],"UnsupportedPackageMatchFields":["Moniker"]}""")));

        var error = Assert.Throws<InvalidOperationException>(() => RestSource.Search(
            client, RestSourceRecord(), new PackageQuery { Query = "contoso" }, Info(), SearchSemantics.Single));
        Assert.Contains("Moniker", error.Message);
    }

    // ── W15-5: null optional arrays in /information ──

    [Fact]
    public void RestInformation_ToleratesNullAndNonStringArrays()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", "wave15-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new StubHandler(_ => Json("""
                {"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0",7],
                 "RequiredPackageMatchFields":null,"UnsupportedPackageMatchFields":null,
                 "RequiredQueryParameters":["Market",null],"UnsupportedQueryParameters":null}}
                """)));

            var info = RestSource.LoadInformation(client, RestSourceRecord(), appRoot);

            Assert.Equal(["1.10.0"], info.ServerSupportedVersions);
            Assert.Empty(info.RequiredPackageMatchFields);
            Assert.Empty(info.UnsupportedPackageMatchFields);
            Assert.Equal(["Market"], info.RequiredQueryParameters);
            Assert.Empty(info.UnsupportedQueryParameters);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    // ── W15-6: search response identifier and version are trimmed ──

    [Fact]
    public void RestSearch_TrimsIdentifierAndVersion()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            Json($$"""{"Data":[{{PackageJson(" Contoso.App ", " 1.2.3 ")}}]}""")));

        var (results, _) = RestSource.Search(
            client, RestSourceRecord(), new PackageQuery { Query = "contoso" }, Info(), SearchSemantics.Many);

        var result = Assert.Single(results);
        Assert.Equal("Contoso.App", result.PackageId);
        Assert.Equal("1.2.3", result.LatestVersion.Version);
        Assert.Equal("1.2.3", result.Versions[0].Version);
    }

    // ── W15-7: manifest response version is trimmed before matching ──

    private static string ManifestJson(string version, string versionSwitches = "", string installerSwitches = "") =>
        $$$"""
        {"Data":{"PackageIdentifier":"Contoso.App","Versions":[{"PackageVersion":"{{{version}}}",
          "DefaultLocale":{"PackageLocale":"en-US","PackageName":"Contoso App","Publisher":"Contoso","ShortDescription":"App"},
          {{{versionSwitches}}}
          "Installers":[{"Architecture":"x64","InstallerType":"exe","InstallerUrl":"https://example.com/app.exe","InstallerSha256":"00",
            {{{installerSwitches}}}
            "Scope":"user"}]}]}}
        """;

    private static Manifest ParseManifest(string json, string version)
    {
        using var document = JsonDocument.Parse(json);
        return RestSource.ParseRestManifest(document.RootElement, "Contoso.App", version, "").Manifest;
    }

    [Fact]
    public void RestManifest_VersionIsTrimmedBeforeMatching()
    {
        var manifest = ParseManifest(ManifestJson("1.2.3 "), "1.2.3");
        Assert.Equal("1.2.3", manifest.Version);
    }

    // ── W15-8: empty installer switches are absent ──

    [Fact]
    public void RestManifest_EmptySwitchesDoNotHideDefaults()
    {
        var manifest = ParseManifest(
            ManifestJson("1.2.3", "\"InstallerSwitches\":{\"Silent\":\"/quiet\"},", "\"InstallerSwitches\":{\"Silent\":\"\",\"Custom\":\"\"},"),
            "1.2.3");
        Assert.Equal("/quiet", manifest.Installers[0].Switches.Silent);
        Assert.Null(manifest.Installers[0].Switches.Custom);

        manifest = ParseManifest(
            ManifestJson("1.2.3", installerSwitches: "\"InstallerSwitches\":{\"Silent\":\"\",\"SilentWithProgress\":\"/passive\"},"),
            "1.2.3");
        Assert.Null(manifest.Installers[0].Switches.Silent);
        Assert.Equal("/passive", manifest.Installers[0].Switches.SilentWithProgress);
    }

    // ── W15-9: /information Cache-Control no-store/no-cache is not cached ──

    private static int InformationRequests(string cacheControl)
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", "wave15-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StubHandler(_ =>
            {
                var response = Json(InformationJson);
                response.Headers.CacheControl = CacheControlHeaderValue.Parse(cacheControl);
                return response;
            });
            using var client = new HttpClient(handler);
            var source = RestSourceRecord();

            RestSource.LoadInformation(client, source, appRoot);
            RestSource.LoadInformation(client, source, appRoot);
            return handler.Requests.Count;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void RestInformation_WithNoStoreOrNoCache_IsNotCached()
    {
        Assert.Equal(1, InformationRequests("public, max-age=600"));
        Assert.Equal(2, InformationRequests("no-store"));
        Assert.Equal(2, InformationRequests("no-cache, max-age=600"));
    }

    // ── W15-10: a search result without usable versions is invalid data, not version "?" ──

    [Theory]
    [InlineData("""{"Data":[{"PackageIdentifier":"Contoso.App","PackageName":"Contoso App","Versions":[]}]}""")]
    [InlineData("""{"Data":[{"PackageIdentifier":"Contoso.App","PackageName":"Contoso App"}]}""")]
    [InlineData("""{"Data":[{"PackageIdentifier":"Contoso.App","PackageName":"Contoso App","Versions":[{"Channel":"beta"}]}]}""")]
    public void RestSearch_ResultWithoutVersions_IsAnError(string response)
    {
        using var client = new HttpClient(new StubHandler(_ => Json(response)));

        Assert.Throws<InvalidOperationException>(() => RestSource.Search(
            client, RestSourceRecord(), new PackageQuery { Query = "contoso" }, Info(), SearchSemantics.Many));
    }
}
