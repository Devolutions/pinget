using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 10 (installed packages, correlation, pins, name normalization) regression tests.</summary>
public class Wave10DefectTests
{
    private static string CreateAppRoot()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);
        return appRoot;
    }

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    private static List<string> QueryStrings(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    // ── R10-2: pinning.db carries WinGet's metadata, pin table and index ──

    [Fact]
    public void PinAdd_CreatesWinGetPinningSchema()
    {
        var appRoot = CreateAppRoot();
        try
        {
            PinStore.Add("Contoso.Tool", "1.0.0", "winget", PinType.Pinning, appRoot);
            var dbPath = SourceStoreManager.PinsDbPath(appRoot);

            Assert.Equal(["majorVersion=1", "minorVersion=0"],
                QueryStrings(dbPath, "SELECT name || '=' || value FROM metadata ORDER BY name"));
            Assert.Contains("pin_index",
                QueryStrings(dbPath, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'pin'"));
            Assert.Equal(["package_id", "source_id", "type", "version"],
                QueryStrings(dbPath, "SELECT name FROM pragma_table_info('pin')"));
            Assert.Contains("WITHOUT ROWID",
                Assert.Single(QueryStrings(dbPath, "SELECT sql FROM sqlite_master WHERE name = 'metadata'")));

            // INSERT OR REPLACE keyed by the unique (package_id, source_id) index.
            PinStore.Add("Contoso.Tool", "*", "winget", PinType.Blocking, appRoot);
            var pin = Assert.Single(PinStore.List(appRoot));
            Assert.Equal(PinType.Blocking, pin.PinType);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void PinAdd_AddsMetadataToLegacyPingetPinTable()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var dbPath = SourceStoreManager.PinsDbPath(appRoot);
            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE pin (
                        package_id TEXT NOT NULL,
                        version TEXT NOT NULL DEFAULT '*',
                        source_id TEXT NOT NULL DEFAULT '',
                        type INTEGER NOT NULL DEFAULT 0,
                        PRIMARY KEY (package_id, source_id)
                    );
                    INSERT INTO pin (package_id, version, source_id, type) VALUES ('Old.Pin', '*', 'winget', 4);
                    """;
                cmd.ExecuteNonQuery();
            }

            PinStore.Add("Contoso.Tool", "1.0.0", "winget", PinType.Pinning, appRoot);

            Assert.Equal(2, PinStore.List(appRoot).Count);
            Assert.Equal(["1"], QueryStrings(dbPath, "SELECT value FROM metadata WHERE name = 'majorVersion'"));
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── R10-13: pin source filters are case-insensitive ──

    [Fact]
    public void PinStore_SourceFiltersIgnoreCase()
    {
        var appRoot = CreateAppRoot();
        try
        {
            PinStore.Add("Contoso.Tool", "1.0.0", "winget", PinType.Pinning, appRoot);
            PinStore.Add("Other.Tool", "1.0.0", "msstore", PinType.Pinning, appRoot);

            var listed = Assert.Single(PinStore.List(appRoot, "WinGet"));
            Assert.Equal("Contoso.Tool", listed.PackageId);

            Assert.True(PinStore.Remove("contoso.tool", appRoot, "WINGET"));
            PinStore.Add("Contoso.Tool", "1.0.0", "winget", PinType.Pinning, appRoot);

            PinStore.Reset(appRoot, "WINGET");
            var remaining = Assert.Single(PinStore.List(appRoot));
            Assert.Equal("msstore", remaining.SourceId);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── R10-4: --include-pinned relaxes only Pinning pins in the list filter ──

    private static ListMatch PinnedMatch(string availableVersion) => new()
    {
        Name = "Contoso Tool",
        Id = "Contoso.Tool",
        LocalId = @"ARP\User\X64\Contoso.Tool",
        InstalledVersion = "1.0.0",
        AvailableVersion = availableVersion,
        SourceName = "winget",
    };

    private static PinRecord Pin(PinType pinType, string version) => new()
    {
        PackageId = "Contoso.Tool",
        Version = version,
        SourceId = "winget",
        PinType = pinType,
    };

    [Fact]
    public void IncludePinned_ListFilterStillAppliesBlockingAndGating()
    {
        var match = PinnedMatch("2.0.0");
        Assert.False(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Pinning, "1.0.0")], includePinned: true));
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Pinning, "1.0.0")], includePinned: false));
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Blocking, "*")], includePinned: true));
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Gating, "1.*")], includePinned: true));
        Assert.False(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Gating, "2.*")], includePinned: true));
    }

    // ── R10-5: ARP version fallbacks ──

    [Fact]
    public void ArpVersion_FollowsWinGetDetermineVersionOrder()
    {
        Assert.Equal("3.1.4", InstalledPackages.DetermineArpVersion("3.1.4", 5, 2, null, null, null));
        Assert.Equal("5.2", InstalledPackages.DetermineArpVersion(null, 5, 2, 7, null, null));
        Assert.Equal("0.9", InstalledPackages.DetermineArpVersion(null, null, 9, null, null, null));
        Assert.Equal("7.1", InstalledPackages.DetermineArpVersion(null, 0, 0, 7, 1, null));
        Assert.Equal("10.2.4660", InstalledPackages.DetermineArpVersion(null, null, null, null, null, 0x0A021234));
        Assert.Equal("Unknown", InstalledPackages.DetermineArpVersion(null, null, null, null, null, null));
        Assert.Equal("Unknown", InstalledPackages.DetermineArpVersion("", null, null, null, null, 0));
    }

    // ── R10-6: explicit-upgrade flags come from the upgrade target's manifest ──

    private static PreIndexedSource.V2VersionDataEntry VersionEntry(string version) => new()
    {
        Version = version,
        ManifestRelativePath = $"manifests/c/Contoso/Tool/{version}/Contoso.Tool.yaml",
    };

    [Fact]
    public void UpgradeTargetEntry_MatchesCorrelatedVersionElseHighest()
    {
        List<PreIndexedSource.V2VersionDataEntry> entries = [VersionEntry("1.0.0"), VersionEntry("2.10.0"), VersionEntry("2.9.0")];

        Assert.Equal("2.10.0", Repository.UpgradeTargetVersionEntry(entries, null)?.Version);
        Assert.Equal("2.9.0", Repository.UpgradeTargetVersionEntry(entries, "2.9")?.Version);
        Assert.Equal("2.10.0", Repository.UpgradeTargetVersionEntry(entries, "3.0.0")?.Version);
        Assert.Null(Repository.UpgradeTargetVersionEntry([], "1.0.0"));
    }

    // ── R10-8 / R10-10: name normalization parity with WinGet ──

    [Fact]
    public void Normalizer_StripsArmArchitectures()
    {
        var arm64 = NameNormalization.NormalizeName("Contoso Tool ARM64");
        Assert.Equal("contosotool", arm64.Name);
        Assert.Equal(NameNormalization.Architecture.Arm64, arm64.Architecture);

        var aarch64 = NameNormalization.NormalizeName("Contoso Tool (AArch64)");
        Assert.Equal("contosotool", aarch64.Name);
        Assert.Equal(NameNormalization.Architecture.Arm64, aarch64.Architecture);

        var arm32 = NameNormalization.NormalizeName("Contoso Tool arm32 Edition");
        Assert.Equal("contosotool", arm32.Name);
        Assert.Equal(NameNormalization.Architecture.Arm, arm32.Architecture);

        Assert.Equal(NameNormalization.Architecture.X64, NameNormalization.NormalizeName("Contoso Tool x64").Architecture);
        Assert.Equal(NameNormalization.Architecture.Unknown, NameNormalization.NormalizeName("Contoso Arm Tool").Architecture);
    }

    [Fact]
    public void Normalizer_KeepsSoftwareToken()
    {
        Assert.Equal("acmesoftware", NameNormalization.NormalizePublisher("Acme Software Inc"));
        Assert.Equal("acmesoftwaresuite", NameNormalization.NormalizeName("Acme Software Suite").Name);
    }

    // ── R10-12: Windows directory comes from %SystemRoot% ──

    [Fact]
    public void WindowsDirectoryTest_UsesTheSystemRoot()
    {
        Assert.True(InstalledPackages.IsPathUnderWindowsDirectory(@"D:\Windows\SystemApps\Microsoft.Windows.Search", @"D:\Windows"));
        Assert.True(InstalledPackages.IsPathUnderWindowsDirectory(@"c:\winnt\system32\x", @"C:\WINNT\"));
        Assert.False(InstalledPackages.IsPathUnderWindowsDirectory(@"C:\Windows\SystemApps\x", @"D:\Windows"));
        Assert.False(InstalledPackages.IsPathUnderWindowsDirectory(@"C:\WindowsApps\Contoso", @"C:\Windows"));
        Assert.False(InstalledPackages.IsPathUnderWindowsDirectory(@"C:\Windows\x", ""));
    }

    // ── R10-3: installed.db REST correlation resolves the available version ──

    private const string InformationJson = """
        {"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.10.0"],"RequiredPackageMatchFields":[],"UnsupportedPackageMatchFields":[]}}
        """;

    private const string SearchJson = """
        {"Data":[{"PackageIdentifier":"Contoso.Store","PackageName":"Contoso Store App","Publisher":"Contoso","Versions":[{"PackageVersion":"1.0.0"},{"PackageVersion":"2.5.0"}]}]}
        """;

    private static InstalledPackage RestCorrelatedPackage(string sourceName) => new()
    {
        Name = "Contoso Store App",
        LocalId = @"MSIX\Contoso.Store_1.0.0.0_x64__abc",
        InstalledVersion = "1.0.0",
        InstallerCategory = "msix",
        PackageFamilyNames = ["Contoso.Store_abc"],
        Correlated = new SearchMatch
        {
            SourceName = sourceName,
            SourceKind = SourceKind.Rest,
            Id = "Contoso.Store",
            Name = "Contoso Store App",
            MatchCriteria = "PackageFamilyName",
        },
    };

    private static Repository OpenWithRestSource(string appRoot, string url)
    {
        var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot, SourceMode = SourceMode.Private });
        foreach (var source in repo.ListSources())
            repo.RemoveSource(source.Name);
        // Loopback http:// stub server: bypass the https-only source-argument check (test seam).
        repo.AddSourceUnvalidated("store", url, SourceKind.Rest, trustLevel: "trusted", explicitSource: false);
        return repo;
    }

    [Fact]
    public void InstalledDbRestCorrelation_GetsLatestAvailableVersion()
    {
        using var server = new StubRestServer(path => path switch
        {
            "/api/information" => (200, InformationJson),
            "/api/manifestSearch" => (200, SearchJson),
            _ => (404, ""),
        });
        var appRoot = CreateAppRoot();
        try
        {
            using var repo = OpenWithRestSource(appRoot, $"{server.Url}/api");
            List<InstalledPackage> installed = [RestCorrelatedPackage("Store")];

            var warnings = repo.ResolveRestCorrelatedVersions(installed);

            Assert.Empty(warnings);
            Assert.Equal("2.5.0", installed[0].Correlated?.Version);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    [Fact]
    public void InstalledDbRestCorrelation_ReportsUnreachableSourceOnce()
    {
        using var server = new StubRestServer(path => path == "/api/information" ? (200, InformationJson) : (404, ""));
        var appRoot = CreateAppRoot();
        try
        {
            using var repo = OpenWithRestSource(appRoot, $"{server.Url}/api");
            List<InstalledPackage> installed = [RestCorrelatedPackage("store"), RestCorrelatedPackage("store")];

            var warnings = repo.ResolveRestCorrelatedVersions(installed);

            Assert.Single(warnings);
            Assert.Null(installed[0].Correlated?.Version);
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
