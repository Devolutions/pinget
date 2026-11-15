using System.Net;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Wave 14 (pre-indexed sources, source store, settings) regression tests.</summary>
[Collection(RepositoryStateCollection.Name)]
public class Wave14DefectTests
{
    private const string CorpExportLine =
        """{"Arg":"https://packages.contoso.test/api","Data":"","Explicit":false,"Identifier":"Contoso.Corp","Name":"corp","TrustLevel":["Trusted"],"Type":"Microsoft.Rest"}""";

    /// <summary>In-process HTTP stub that serves both the synchronous and asynchronous send paths.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static SourceRecord Source(string name, SourceKind kind, string arg, string identifier) => new()
    {
        Name = name,
        Kind = kind,
        Arg = arg,
        Identifier = identifier,
        TrustLevel = "Trusted",
    };

    private static string CreateAppRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", "wave14-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteAppRoot(string appRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(appRoot))
            Directory.Delete(appRoot, recursive: true);
    }

    private static T WithRunner<T>(Func<IReadOnlyList<string>, WingetCommandResult> runner, Func<T> action)
    {
        var original = SystemWingetSourceStore.CommandRunner;
        try
        {
            SystemWingetSourceStore.CommandRunner = runner;
            return action();
        }
        finally
        {
            SystemWingetSourceStore.CommandRunner = original;
        }
    }

    private static WingetCommandResult CorpExport(IReadOnlyList<string> _) => new(0, CorpExportLine + "\n", "");

    // ── W14-1: `winget source export` with no sources prints a message, not JSON ──

    [Fact]
    public void SystemWingetExport_WithoutSources_IsAnEmptyList()
    {
        Assert.Empty(SystemWingetSourceStore.ParseExport("There are no sources configured.\r\n"));

        // A JSON line mixed with something else is still rejected.
        Assert.Throws<InvalidOperationException>(() => SystemWingetSourceStore.ParseExport(CorpExportLine + "\nnot a source\n"));
    }

    // ── W14-2: mirror refresh survives sources that share an identifier ──

    [Fact]
    public void MirrorRefresh_ToleratesSourcesSharingAnIdentifier()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var mirror = Source("winget-mirror", SourceKind.PreIndexed, "https://mirror.contoso.test/cache", "Microsoft.Winget.Source_8wekyb3d8bbwe");
            mirror.ETag = "\"mirror-etag\"";
            var prior = new SourceStore { Sources = [SourceStore.Default().Sources[0], mirror] };
            SourceStoreManager.SaveSystemWingetMirrorStore(appRoot, prior);

            const string export = """
{"Arg":"https://cdn.winget.microsoft.com/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","TrustLevel":["Trusted"],"Type":"Microsoft.PreIndexed.Package"}
{"Arg":"https://mirror.contoso.test/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget-mirror","TrustLevel":["Trusted"],"Type":"Microsoft.PreIndexed.Package"}
""";
            var refreshed = WithRunner(
                _ => new WingetCommandResult(0, export, ""),
                () => SourceStoreManager.RefreshSystemWingetMirrorStore(appRoot));

            Assert.Equal(["winget", "winget-mirror"], refreshed.Sources.Select(source => source.Name).ToArray());
            Assert.Equal("\"mirror-etag\"", refreshed.Sources[1].ETag);
            Assert.Null(refreshed.Sources[0].ETag);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── W14-3: mirror metadata is not carried onto a source whose argument changed ──

    [Fact]
    public void MirrorRefresh_DropsMetadataWhenTheSourceArgumentChanged()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var previous = Source("corp", SourceKind.Rest, "https://old.contoso.test/api", "Contoso.Corp");
            previous.ETag = "\"old-etag\"";
            previous.SourceVersion = "1.4.0";
            previous.LastUpdate = DateTime.UtcNow;
            SourceStoreManager.SaveSystemWingetMirrorStore(appRoot, new SourceStore { Sources = [previous] });

            var moved = WithRunner(CorpExport, () => SourceStoreManager.RefreshSystemWingetMirrorStore(appRoot));
            var corp = Assert.Single(moved.Sources);
            Assert.Equal("https://packages.contoso.test/api", corp.Arg);
            Assert.Null(corp.ETag);
            Assert.Null(corp.SourceVersion);
            Assert.Null(corp.LastUpdate);

            // The same source (a trailing slash aside) keeps its metadata.
            var same = Source("corp", SourceKind.Rest, "https://packages.contoso.test/api/", "contoso.corp");
            same.ETag = "\"same-etag\"";
            SourceStoreManager.SaveSystemWingetMirrorStore(appRoot, new SourceStore { Sources = [same] });
            var kept = WithRunner(CorpExport, () => SourceStoreManager.RefreshSystemWingetMirrorStore(appRoot));
            Assert.Equal("\"same-etag\"", Assert.Single(kept.Sources).ETag);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── W14-4: an unreadable mirror cache is re-exported instead of failing forever ──

    [Fact]
    public void CorruptSystemWingetMirrorStore_IsReExported()
    {
        var appRoot = CreateAppRoot();
        try
        {
            var mirrorPath = Path.Combine(appRoot, "system-sources.json");
            File.WriteAllText(mirrorPath, "{\"sources\": [ {\"name\": ");

            var (mode, store) = WithRunner(CorpExport, () => SourceStoreManager.LoadEffective(appRoot, SourceMode.SystemWingetMirror));

            Assert.Equal(EffectiveSourceMode.SystemWingetMirror, mode);
            Assert.Equal("corp", Assert.Single(store.Sources).Name);
            Assert.Contains("corp", File.ReadAllText(mirrorPath));
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── W14-5: direct-mode update by identifier names the source for winget ──

    [Fact]
    public void DirectModeUpdateByIdentifier_PassesTheSourceNameToWinget()
    {
        var appRoot = CreateAppRoot();
        var calls = new List<string[]>();
        try
        {
            WingetCommandResult Runner(IReadOnlyList<string> args)
            {
                calls.Add(args.ToArray());
                if (args[1] == "export")
                    return CorpExport(args);

                // `winget source update --name X` only knows source names.
                var nameIndex = args.ToList().IndexOf("--name");
                var name = nameIndex >= 0 ? args[nameIndex + 1] : null;
                return name is not null && !name.Equals("corp", StringComparison.OrdinalIgnoreCase)
                    ? new WingetCommandResult(1, "", $"Did not find a source named: {name}")
                    : new WingetCommandResult(0, "Updating source: corp...\nDone", "");
            }

            var results = WithRunner(Runner, () =>
            {
                var constructor = typeof(Repository).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                using var repository = (Repository)constructor.Invoke(
                    [appRoot, new HttpClient(), new SourceStore(), EffectiveSourceMode.SystemWingetDirect, null, null, null]);
                return repository.UpdateSources("Contoso.Corp");
            });

            Assert.Equal("corp", Assert.Single(results).Name);
            var update = calls.Single(call => call[1] == "update");
            var updateNameIndex = Array.IndexOf(update, "--name");
            Assert.True(updateNameIndex >= 0);
            Assert.Equal("corp", update[updateNameIndex + 1]);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── W14-6: a pre-indexed update failure reports why each candidate failed ──

    [Fact]
    public void PreIndexedUpdate_ReportsTheHttpStatusOfFailedCandidates()
    {
        var appRoot = CreateAppRoot();
        try
        {
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            var source = Source("corp", SourceKind.PreIndexed, "https://packages.contoso.test/cache", "Contoso.Corp");

            var error = Assert.Throws<InvalidOperationException>(() => PreIndexedSource.Update(client, source, appRoot));

            Assert.Contains("404", error.Message);
            Assert.Contains("source.msix", error.Message);
        }
        finally
        {
            DeleteAppRoot(appRoot);
        }
    }

    // ── W14-8: a pathparts cycle in a V1 index is an error, not an endless loop ──

    [Fact]
    public void V1RelativePath_WithAPathpartsCycle_Fails()
    {
        var resolve = Task.Run(() =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE pathparts (parent INT64, pathpart TEXT);
                    INSERT INTO pathparts(rowid, parent, pathpart) VALUES (1, 2, 'a'), (2, 1, 'b');
                    """;
                create.ExecuteNonQuery();
            }

            return PreIndexedSource.ResolveV1RelativePath(connection, 1);
        });

        var finished = Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(20))).GetAwaiter().GetResult();
        Assert.True(finished == resolve, "resolving a pathparts cycle must terminate");
        var error = Assert.IsType<InvalidOperationException>(resolve.Exception?.InnerException);
        Assert.Contains("cycle", error.Message);
    }

    // ── W14-9: packaged user_sources keeps the Identifier key WinGet reads ──

    [Fact]
    public void PackagedSourcesYaml_WritesTheIdentifierKey()
    {
        var store = new SourceStore
        {
            Sources = [Source("corp", SourceKind.Rest, "https://packages.contoso.test/api", "Contoso.Rest")],
        };

        var yaml = SourceStoreManager.RenderPackagedUserSourcesYaml(store);

        Assert.Contains(
            yaml.Replace("\r\n", "\n").Split('\n'),
            line => line.Trim() == "Identifier: Contoso.Rest");
    }

    // ── W14-10: packaged sources are ordered by priority, otherwise as listed ──

    [Fact]
    public void PackagedSources_AreOrderedByDescendingPriority()
    {
        const string userSources = """
            Sources:
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
            """;

        var store = SourceStoreManager.ParsePackagedSourceStore(userSources, null)!;

        Assert.Equal(["high", "winget", "msstore", "zeta"], store.Sources.Select(source => source.Name).ToArray());
    }
}
