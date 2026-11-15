using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 16 (search, version comparison, installer selection) regression tests.</summary>
public class Wave16DefectTests
{
    private const string V2Schema = """
        CREATE TABLE packages (rowid INTEGER PRIMARY KEY, id TEXT, name TEXT, moniker TEXT, latest_version TEXT, hash BLOB);
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
        INSERT INTO commands2_map VALUES (1, 1);
        """;

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
        INSERT INTO ids VALUES (1, 'Contoso.Terminal'), (2, 'Contoso.Editor'), (3, 'Fabrikam.Terminal');
        INSERT INTO names VALUES (1, 'Contoso Terminal'), (2, 'Contoso Editor'), (3, 'Fabrikam Terminal');
        INSERT INTO versions VALUES (1, '1.0.0');
        INSERT INTO channels VALUES (1, '');
        INSERT INTO manifest VALUES (1, 1, 1, NULL, 1, 1, 0), (2, 2, 2, NULL, 1, 1, 0), (3, 3, 3, NULL, 1, 1, 0);
        INSERT INTO tags VALUES (1, 'powertoys'), (2, 'editor');
        INSERT INTO tags_map VALUES (1, 1), (2, 2), (1, 3);
        """;

    private static SqliteConnection SeedDatabase(string sql)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static string[] V2Ids(SqliteConnection connection, PackageQuery query, SearchSemantics semantics) =>
        [.. PreIndexedSource.SearchV2(connection, query, semantics).Rows.Select(row => row.Id).Order(StringComparer.Ordinal)];

    private static string[] V1Ids(SqliteConnection connection, PackageQuery query, SearchSemantics semantics) =>
        [.. PreIndexedSource.SearchV1(connection, query, semantics).Rows.Select(row => row.Id).Distinct().Order(StringComparer.Ordinal)];

    private static (string Field, string MatchType)[] Filters(PackageQuery query, SearchSemantics semantics, string key)
    {
        var body = JsonNode.Parse(RestSource.BuildSearchBody(query, new RestSource.RestInformation(), semantics))!.AsObject();
        return body[key] is JsonArray items
            ? [.. items.Select(item => (
                item!["PackageMatchField"]!.GetValue<string>(),
                item["RequestMatch"]!["MatchType"]!.GetValue<string>()))]
            : [];
    }

    private static Installer MakeInstaller(string url, string installerType = "exe") => new()
    {
        InstallerType = installerType,
        Url = $"https://example.test/{url}",
    };

    private static string? Select(IEnumerable<Installer> installers, PackageQuery query, params string[] preferredLocales) =>
        Repository.SelectInstaller([.. installers], query, "US", preferredLocales)?.Url?.Replace("https://example.test/", "");

    // ── W16-1: `Latest` sorts above every other version ──

    [Fact]
    public void LatestVersion_SortsAboveKnownVersions()
    {
        Assert.True(RestSource.CompareVersionStrings("Latest", "99.0.0") > 0);
        Assert.True(RestSource.CompareVersionStrings("1.0", "latest") < 0);
        Assert.Equal(0, RestSource.CompareVersionStrings("LATEST", "latest"));
        Assert.True(RestSource.CompareVersionStrings("Latest", "Unknown") > 0);

        var versions = new List<VersionKey>
        {
            new() { Version = "2.0.0", Channel = "" },
            new() { Version = "Latest", Channel = "" },
            new() { Version = "10.0", Channel = "" },
        };
        RestSource.SortVersionsDesc(versions);
        Assert.Equal("Latest", versions[0].Version);
    }

    // ── W16-2: `> 1.0` sorts just above `1.0` ──

    [Theory]
    [InlineData("> 2.0", "2.0", 1)]
    [InlineData("2.0.0", "> 2.0", -1)]
    [InlineData("> 2.0", "2.1", -1)]
    [InlineData("< 2.0", "> 2.0", -1)]
    [InlineData("> 2.0", "> 2.0.0", 0)]
    [InlineData("< 2.0", "2.0", -1)]
    [InlineData("< 2.0", "1.9", 1)]
    public void ApproximateVersions_SortAroundTheirBase(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(RestSource.CompareVersionStrings(left, right)));
    }

    // ── W16-3: every pre-indexed search filter applies (AND), not only the first ──

    [Fact]
    public void PreIndexedSearch_AppliesAllFilters()
    {
        var idAndName = new PackageQuery { Id = "Contoso", Name = "Terminal" };
        var queryAndTag = new PackageQuery { Query = "Terminal", Tag = "powertoys" };
        var queryAndCommand = new PackageQuery { Query = "Terminal", Command = "contoso-term" };
        var singleWithName = new PackageQuery { Query = "Contoso.Terminal", Name = "Something Else" };

        using (var connection = SeedDatabase(V2Schema))
        {
            Assert.Equal(["Contoso.Terminal"], V2Ids(connection, idAndName, SearchSemantics.Many));
            Assert.Equal(["Contoso.Terminal", "Fabrikam.Terminal"], V2Ids(connection, queryAndTag, SearchSemantics.Many));
            Assert.Equal(["Contoso.Terminal"], V2Ids(connection, queryAndCommand, SearchSemantics.Many));
            Assert.Empty(V2Ids(connection, singleWithName, SearchSemantics.Single));
        }

        using (var connection = SeedDatabase(V1Schema))
        {
            Assert.Equal(["Contoso.Terminal"], V1Ids(connection, idAndName, SearchSemantics.Many));
            Assert.Equal(["Fabrikam.Terminal"], V1Ids(connection, new PackageQuery { Id = "Fabrikam", Tag = "powertoys" }, SearchSemantics.Many));
        }
    }

    // ── W16-4: a many-search tag / command filter is a substring match unless --exact ──

    [Fact]
    public void ManySearch_TagAndCommandFiltersMatchSubstrings()
    {
        var tag = new PackageQuery { Tag = "Power" };
        var command = new PackageQuery { Command = "term" };
        var exact = tag with { Exact = true };

        using (var connection = SeedDatabase(V2Schema))
        {
            Assert.Equal(["Contoso.Terminal", "Fabrikam.Terminal"], V2Ids(connection, tag, SearchSemantics.Many));
            Assert.Equal(["Contoso.Terminal"], V2Ids(connection, command, SearchSemantics.Many));
            Assert.Empty(V2Ids(connection, exact, SearchSemantics.Many));
        }

        Assert.Equal([("Tag", "Substring")], Filters(tag, SearchSemantics.Many, "Filters"));
        Assert.Equal([("Command", "Substring")], Filters(command, SearchSemantics.Many, "Filters"));
        Assert.Equal([("Tag", "Exact")], Filters(exact, SearchSemantics.Many, "Filters"));
    }

    // ── W16-5: --installer-type matches an archive's nested installer type ──

    [Fact]
    public void RequestedInstallerType_MatchesNestedArchiveType()
    {
        var zip = MakeInstaller("tool.zip", "zip") with { NestedInstallerType = "exe" };
        var msi = MakeInstaller("tool.msi", "msi");

        Assert.Equal("tool.zip", Select([msi, zip], new PackageQuery { InstallerType = "exe" }));
        Assert.Equal("tool.zip", Select([msi, zip], new PackageQuery { InstallerType = "ZIP" }));
        Assert.Equal("tool.msi", Select([zip, msi], new PackageQuery { InstallerType = "msi" }));
        Assert.Null(Select([zip], new PackageQuery { InstallerType = "msi" }));

        // `appx` and `msix` are one installer type in WinGet.
        Assert.Equal("tool.appx", Select([MakeInstaller("tool.appx", "appx")], new PackageQuery { InstallerType = "msix" }));
    }

    // ── W16-6: WinGet's default installer type preference orders equal installers ──

    [Fact]
    public void DefaultInstallerTypePreference_OrdersInstallers()
    {
        var exe = MakeInstaller("tool.exe");
        var msi = MakeInstaller("tool.msi", "msi");
        var msix = MakeInstaller("tool.msix", "msix");
        var portable = MakeInstaller("tool-portable.exe", "portable");
        var zipMsi = MakeInstaller("tool.zip", "zip") with { NestedInstallerType = "msi" };
        var none = new PackageQuery();

        Assert.Equal("tool.msi", Select([exe, msi], none));
        Assert.Equal("tool.msix", Select([portable, exe, msix], none));
        Assert.Equal("tool.exe", Select([portable, exe], none));
        Assert.Equal("tool.zip", Select([exe, zipMsi], none));

        // A requested type is a requirement, not a preference ordering.
        Assert.Equal("tool.exe", Select([msi, exe], new PackageQuery { InstallerType = "exe" }));
    }

    // ── W16-7: neutral ranks below the native and emulated architectures ──

    [Fact]
    public void NeutralArchitecture_RanksBelowEmulatedArchitectures()
    {
        static int Rank(string architecture, string system) => Repository.ArchitectureRank(architecture, null, system);

        Assert.True(Rank("x64", "x64") > Rank("x86", "x64"));
        Assert.True(Rank("x86", "x64") > Rank("neutral", "x64"));
        Assert.True(Rank("arm64", "arm64") > Rank("x64", "arm64"));
        Assert.True(Rank("x64", "arm64") > Rank("x86", "arm64"));
        Assert.True(Rank("x86", "arm64") > Rank("neutral", "arm64"));
        Assert.True(Rank("x86", "x86") > Rank("neutral", "x86"));
        Assert.True(Rank("arm64", "x64") < 0);

        if (Repository.CurrentArchitecture() == "x64")
        {
            var neutral = MakeInstaller("neutral.exe") with { Architecture = "neutral" };
            var x86 = MakeInstaller("x86.exe") with { Architecture = "x86" };
            Assert.Equal("x86.exe", Select([neutral, x86], new PackageQuery()));
        }
    }

    // ── W16-8: a REST single lookup keeps --id/--name/--tag filters next to the query ──

    [Fact]
    public void RestSingleLookup_KeepsFiltersWithQuery()
    {
        var query = new PackageQuery { Query = "Contoso", Id = "Contoso.Tool", Tag = "cli" };

        Assert.Equal(
            [("PackageIdentifier", "CaseInsensitive"), ("PackageName", "CaseInsensitive"), ("Moniker", "CaseInsensitive")],
            Filters(query, SearchSemantics.Single, "Inclusions"));
        Assert.Equal(
            [("PackageIdentifier", "CaseInsensitive"), ("Tag", "CaseInsensitive")],
            Filters(query, SearchSemantics.Single, "Filters"));
    }

    // ── W16-9: scope preference (requested, else WinGet's default user) ranks installers ──

    [Fact]
    public void ScopePreference_RanksInstallers()
    {
        var machine = MakeInstaller("machine.exe") with { Scope = "machine" };
        var user = MakeInstaller("user.exe") with { Scope = "user" };
        var unscoped = MakeInstaller("unscoped.exe");

        // WinGet's default `installBehavior.preferences.scope` is user.
        Assert.Equal("user.exe", Select([machine, user], new PackageQuery()));
        Assert.Equal("user.exe", Select([unscoped, user], new PackageQuery()));

        // A requested scope prefers an installer declaring it over an undeclared one.
        Assert.Equal("machine.exe", Select([unscoped, machine, user], new PackageQuery { InstallScope = "machine" }));

        // The system architecture (strong) beats a scope preference over an undeclared scope (weak); a scope
        // preference over the opposite scope (strong) beats the architecture.
        var system = Repository.CurrentArchitecture();
        if (system == "neutral")
            return;
        var nativeUnscoped = MakeInstaller("native.exe") with { Architecture = system };
        var neutralUser = MakeInstaller("neutral-user.exe") with { Architecture = "neutral", Scope = "user" };
        Assert.Equal("native.exe", Select([neutralUser, nativeUnscoped], new PackageQuery()));
        var nativeMachine = MakeInstaller("native-machine.exe") with { Architecture = system, Scope = "machine" };
        Assert.Equal("neutral-user.exe", Select([nativeMachine, neutralUser], new PackageQuery()));

        // A compatible locale match (strong) comes first in WinGet's comparator order.
        var nativeGerman = MakeInstaller("native-de.exe") with { Architecture = system, Locale = "de-DE" };
        var neutralEnglish = MakeInstaller("neutral-en.exe") with { Architecture = "neutral", Locale = "en-US" };
        Assert.Equal("neutral-en.exe", Select([nativeGerman, neutralEnglish], new PackageQuery(), "en-US"));
    }

    // ── W16-10: REST truncation follows MaximumResults and ContinuationToken ──

    [Fact]
    public void RestSearchTruncation_NeedsMoreResultsOrAContinuationToken()
    {
        Assert.False(RestSource.RestSearchTruncated(null, 50, 50));
        Assert.False(RestSource.RestSearchTruncated(null, 3, 50));
        Assert.True(RestSource.RestSearchTruncated(null, 51, 50));
        Assert.True(RestSource.RestSearchTruncated("abc", 3, 50));
        Assert.False(RestSource.RestSearchTruncated("", 3, 50));
        Assert.False(RestSource.RestSearchTruncated("  ", 3, 50));
    }
}
