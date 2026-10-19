using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class AgentSkillCatalogTests
{
    private const string CatalogUrl = "https://catalog.example/catalog.json";
    private const string SkillName = "catalog-skill";
    private const string PackageId = "Example.CatalogSkill";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Archive(string contents, string? skill = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var markdownWriter = new StreamWriter(zip.CreateEntry("SKILL.md").Open(), new UTF8Encoding(false)))
                markdownWriter.Write(skill ?? $"---\nname: {SkillName}\ndescription: Example\n---\nInstructions.\n");
            using var writer = new StreamWriter(zip.CreateEntry("contents.txt").Open(), new UTF8Encoding(false));
            writer.Write(contents);
        }
        return buffer.ToArray();
    }

    private static byte[] Manifest(string version, byte[] artifact, string id = PackageId) => Bytes($$"""
        ManifestType: pinget.agentSkill
        PingetManifestVersion: 1
        PackageIdentifier: {{id}}
        PackageVersion: {{version}}
        PackageName: {{SkillName}}
        AgentSkill:
          ArtifactUrl: https://catalog.example/{{version}}.zip
          ArtifactSha256: {{Hash(artifact)}}
        """);

    private static byte[] Catalog(params (string Version, byte[] Manifest)[] manifests) =>
        Bytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = manifests.Select(m => new
            {
                packageIdentifier = PackageId,
                packageName = SkillName,
                packageVersion = m.Version,
                manifestUrl = $"https://catalog.example/{m.Version}.yaml",
                manifestSha256 = Hash(m.Manifest),
            }).ToArray(),
        }));

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.CurrentDirectory, ".pinget-catalog-test-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "project");
        public string AppRoot => Path.Combine(Root, "state");
        public string Skill => Path.Combine(Project, ".agents", "skills", SkillName);
        public string Provenance => Path.Combine(Project, ".pinget", "agent-skills", "github-copilot", SkillName + ".json");

        public Workspace() => Directory.CreateDirectory(Project);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        public Repository Open(HttpClient client) => Repository.Open(new RepositoryOptions
        {
            AppRoot = AppRoot, SourceMode = SourceMode.Private,
        }, client, Project);
    }

    private static InstallRequest Request(string? version = null, bool upgrade = false) => new()
    {
        Agent = "github-copilot",
        RequireExistingAgentSkill = upgrade,
        Query = new PackageQuery { Id = PackageId, Source = "skills", Version = version },
    };

    private static HttpClient Client(byte[] catalog, params (string Version, byte[] Manifest, byte[] Artifact)[] versions)
    {
        return new HttpClient(new Handler(url =>
        {
            if (url.AbsoluteUri == CatalogUrl || url.AbsoluteUri == "https://other.example/catalog.json")
                return Ok(catalog);
            foreach (var (version, manifest, artifact) in versions)
            {
                if (url.AbsoluteUri == $"https://catalog.example/{version}.yaml") return Ok(manifest);
                if (url.AbsoluteUri == $"https://catalog.example/{version}.zip") return Ok(artifact);
            }
            throw new InvalidOperationException("Unexpected URL: " + url.GetLeftPart(UriPartial.Path));
        }));
    }

    [Fact]
    public void SearchAgentSkills_WithoutCatalogSource_RequiresConfiguration()
    {
        using var workspace = new Workspace();
        using var repo = workspace.Open(new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("No catalog URL should be fetched."))));
        Assert.Contains("No Pinget.AgentSkills source", Assert.Throws<InvalidOperationException>(() =>
            repo.SearchAgentSkills(new PackageQuery { Query = SkillName })).Message);
    }

    [Fact]
    public void Catalog_RejectsInvalidSchemaDuplicatesAndUrls()
    {
        var manifest = Manifest("1.0.0", Archive("one"));
        Assert.Contains("schemaVersion", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes("""{"schemaVersion":2,"packages":[]}"""))).Message);
        Assert.Contains("unknown fields", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes("""{"schemaVersion":1,"packages":[],"unknown":true}"""))).Message);
        var repeated = Catalog(("1.0.0", manifest), ("1.0.0", manifest));
        Assert.Contains("duplicate", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(repeated)).Message);
        var valid = Encoding.UTF8.GetString(Catalog(("1.0.0", manifest)));
        Assert.Contains("unsafe", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes(valid.Replace(PackageId, "../outside", StringComparison.Ordinal)))).Message);
        Assert.Contains("missing", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes(valid.Replace("\"packageName\":\"catalog-skill\",", "", StringComparison.Ordinal)))).Message);
        Assert.Contains("skill slug", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes(valid.Replace("\"packageName\":\"catalog-skill\"", "\"packageName\":\"Bad_Name\"", StringComparison.Ordinal)))).Message);
        Assert.Contains("query", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(Bytes(valid.Replace("1.0.0.yaml\"", "1.0.0.yaml?token=private\"", StringComparison.Ordinal)))).Message);
        foreach (var url in new[] { "http://catalog.example/1.0.0.yaml", "https://catalog.example/../1.0.0.yaml",
                     "https://catalog.example/%2e%2e/1.0.0.yaml" })
        {
            Assert.Contains("HTTPS", Assert.Throws<InvalidOperationException>(() =>
                AgentSkillCatalog.Parse(Bytes(valid.Replace("https://catalog.example/1.0.0.yaml", url)))).Message);
        }
        Assert.Contains("size limit", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(new byte[1024 * 1024 + 1])).Message);
        var tooMany = Bytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = Enumerable.Range(0, 1001).Select(i => new
            {
                packageIdentifier = $"Example.Skill{i}",
                packageName = "catalog-skill",
                packageVersion = "1.0.0",
                manifestUrl = $"https://catalog.example/skill{i}.yaml",
                manifestSha256 = new string('a', 64),
            }).ToArray(),
        }));
        Assert.Contains("too many", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Parse(tooMany)).Message);
    }

    [Theory]
    [InlineData("demo.yaml", "https://catalog.example/skills/demo.yaml")]
    [InlineData("./demo.yaml", "https://catalog.example/skills/demo.yaml")]
    [InlineData("nested/demo.yaml", "https://catalog.example/skills/nested/demo.yaml")]
    [InlineData("/manifests/demo.yaml", "https://catalog.example/manifests/demo.yaml")]
    [InlineData("https://other.example/demo.yaml", "https://other.example/demo.yaml")]
    public void Catalog_ResolvesSafeRelativeAndAbsoluteManifestUrls(string manifestUrl, string expected)
    {
        var catalog = Encoding.UTF8.GetString(Catalog(("1.0.0", Manifest("1.0.0", Archive("one")))))
            .Replace("\"https://catalog.example/1.0.0.yaml\"",
                JsonSerializer.Serialize(manifestUrl), StringComparison.Ordinal);
        var entry = Assert.Single(AgentSkillCatalog.Parse(Bytes(catalog),
            new Uri("https://catalog.example/skills/catalog.json")));
        Assert.Equal(expected, entry.ManifestUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("../demo.yaml")]
    [InlineData("nested/../demo.yaml")]
    [InlineData("%2e%2e/demo.yaml")]
    [InlineData("nested%2fdemo.yaml")]
    [InlineData("nested\\demo.yaml")]
    [InlineData("demo.yaml?token=private")]
    [InlineData("demo.yaml#fragment")]
    [InlineData("//other.example/demo.yaml")]
    [InlineData("http://other.example/demo.yaml")]
    [InlineData("https://user@other.example/demo.yaml")]
    [InlineData("https://other.example/demo.yaml?token=private")]
    [InlineData("https://other.example/demo.yaml#fragment")]
    [InlineData("https:demo.yaml")]
    public void Catalog_RejectsUnsafeManifestUrls(string manifestUrl)
    {
        var catalog = Encoding.UTF8.GetString(Catalog(("1.0.0", Manifest("1.0.0", Archive("one")))))
            .Replace("\"https://catalog.example/1.0.0.yaml\"",
                JsonSerializer.Serialize(manifestUrl), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AgentSkillCatalog.Parse(Bytes(catalog),
            new Uri(CatalogUrl)));
    }

    [Fact]
    public void Catalog_RelativeManifestUrlRequiresCatalogIndexAndQueryFreeSource()
    {
        var catalog = Encoding.UTF8.GetString(Catalog(("1.0.0", Manifest("1.0.0", Archive("one")))))
            .Replace("https://catalog.example/1.0.0.yaml", "demo.yaml", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AgentSkillCatalog.Parse(Bytes(catalog)));
        var source = new SourceRecord
        {
            Name = "skills", Kind = SourceKind.AgentSkills,
            Arg = CatalogUrl + "?token=private", Identifier = "skills",
        };
        using var workspace = new Workspace();
        using var client = new HttpClient(new Handler(_ =>
            throw new InvalidOperationException("Query-bearing source must not be fetched.")));
        Assert.Contains("query", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Read(client, source, workspace.AppRoot)).Message);
        Assert.Contains("query", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Refresh(client, source, workspace.AppRoot)).Message);
    }

    [Fact]
    public void Fetch_RejectsRedirectToHttpAndLimitsResponse()
    {
        using var redirectClient = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://catalog.example/catalog.json") },
        }));
        Assert.Contains("HTTPS", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Fetch(redirectClient, new Uri(CatalogUrl))).Message);
        using var largeClient = new HttpClient(new Handler(_ => Ok(new byte[1024 * 1024 + 1])));
        Assert.Contains("size limit", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Fetch(largeClient, new Uri(CatalogUrl))).Message);
    }

    [Fact]
    public void Resolve_RequiresPinnedManifestIdentityAndVersion()
    {
        var artifact = Archive("one");
        var valid = Manifest("1.0.0", artifact);
        var wrongId = Manifest("1.0.0", artifact, "Other.Skill");
        using var client = Client(Catalog(("1.0.0", valid)), ("1.0.0", wrongId, artifact));
        var source = new SourceRecord { Name = "skills", Kind = SourceKind.AgentSkills,
            Identifier = "skills", Arg = CatalogUrl };
        Assert.Contains("SHA-256", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Resolve(client, source, PackageId, null)).Message);
        using var identityClient = Client(Catalog(("1.0.0", wrongId)), ("1.0.0", wrongId, artifact));
        Assert.Contains("identity", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Resolve(identityClient, source, PackageId, null)).Message);
        using var versionClient = Client(Catalog(("1.0.0", Manifest("2.0.0", artifact))),
            ("1.0.0", Manifest("2.0.0", artifact), artifact));
        Assert.Contains("version", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Resolve(versionClient, source, PackageId, null)).Message);
        var wrongNameCatalog = Bytes(Encoding.UTF8.GetString(Catalog(("1.0.0", valid)))
            .Replace("\"packageName\":\"catalog-skill\"", "\"packageName\":\"different-skill\"", StringComparison.Ordinal));
        using var nameClient = Client(wrongNameCatalog, ("1.0.0", valid, artifact));
        Assert.Contains("packageName", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Resolve(nameClient, source, PackageId, null)).Message);
    }

    [Fact]
    public void Resolve_CaseInsensitiveIdAndVersionLookupAcceptsManifestIdCasing()
    {
        var artifact = Archive("one");
        var manifest = Manifest("2.0.0-PREVIEW", artifact, PackageId.ToLowerInvariant());
        using var client = Client(Catalog(("2.0.0-PREVIEW", manifest)),
            ("2.0.0-PREVIEW", manifest, artifact));
        var source = new SourceRecord
        {
            Name = "skills", Kind = SourceKind.AgentSkills, Identifier = "skills", Arg = CatalogUrl,
        };

        var selected = AgentSkillCatalog.Resolve(client, source, PackageId.ToUpperInvariant(), "2.0.0-preview");
        Assert.Equal(PackageId.ToLowerInvariant(), selected.Id);
        Assert.Equal("2.0.0-PREVIEW", selected.Version);
    }

    [Fact]
    public void Resolve_ManifestVersionMustExactlyMatchCatalogEvenForCaseInsensitiveLookup()
    {
        var artifact = Archive("one");
        var manifest = Manifest("2.0.0-PREVIEW", artifact);
        using var client = Client(Catalog(("2.0.0-preview", manifest)),
            ("2.0.0-preview", manifest, artifact));
        var source = new SourceRecord
        {
            Name = "skills", Kind = SourceKind.AgentSkills, Identifier = "skills", Arg = CatalogUrl,
        };

        Assert.Contains("version", Assert.Throws<InvalidOperationException>(() =>
            AgentSkillCatalog.Resolve(client, source, PackageId, "2.0.0-PREVIEW")).Message);
    }

    [Fact]
    public void Repository_InstallById_CaseInsensitiveAcrossSourcesAndManifestIdentity()
    {
        using var workspace = new Workspace();
        var artifact = Archive("case-insensitive install");
        var manifest = Manifest("1.0.0", artifact, PackageId.ToLowerInvariant());
        var catalog = Catalog(("1.0.0", manifest));
        using var repo = workspace.Open(new HttpClient(new Handler(url => url.AbsoluteUri switch
        {
            CatalogUrl => Ok(catalog),
            "https://other.example/catalog.json" => Ok(Bytes("""{"schemaVersion":1,"packages":[]}""")),
            "https://catalog.example/1.0.0.yaml" => Ok(manifest),
            "https://catalog.example/1.0.0.zip" => Ok(artifact),
            _ => throw new InvalidOperationException("Unexpected request: " + url.AbsoluteUri),
        })));
        repo.AddSource("other", "https://other.example/catalog.json", SourceKind.AgentSkills);
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);

        var result = repo.Install(Request() with
        {
            Query = new PackageQuery { Id = PackageId.ToUpperInvariant() },
        });
        Assert.True(result.Success);
        Assert.Equal(PackageId.ToLowerInvariant(), result.PackageId);
        Assert.Equal("case-insensitive install", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
        using var provenance = JsonDocument.Parse(File.ReadAllText(workspace.Provenance));
        Assert.Equal("skills", provenance.RootElement.GetProperty("catalogSource").GetString());
    }

    [Fact]
    public void Repository_CatalogSourcesPersistSeparatelyAndInstallNewest()
    {
        using var workspace = new Workspace();
        var a1 = Archive("one");
        var a2 = Archive("two");
        var m1 = Manifest("1.0.0", a1);
        var m2 = Manifest("2.0.0", a2);
        using (var repo = workspace.Open(Client(Catalog(("1.0.0", m1), ("2.0.0", m2)),
            ("1.0.0", m1, a1), ("2.0.0", m2, a2))))
        {
            repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
            Assert.Equal(SourceKind.AgentSkills, Assert.Single(repo.ListSources().Where(s => s.Name == "skills")).Kind);
            Assert.Contains("2 AgentSkills", Assert.Single(repo.UpdateSources("skills")).Detail);
            Assert.True(File.Exists(AgentSkillCatalog.CachePath(workspace.AppRoot,
                Assert.Single(repo.ListSources().Where(s => s.Name == "skills")))));
            var install = repo.Install(Request() with { Query = new PackageQuery { Id = PackageId } });
            Assert.True(install.Success);
            Assert.Equal("2.0.0", install.Version);
            Assert.Equal("two", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
            using (var provenance = JsonDocument.Parse(File.ReadAllText(workspace.Provenance)))
            {
                Assert.Equal("skills", provenance.RootElement.GetProperty("catalogSource").GetString());
                Assert.Equal(Hash(m2), provenance.RootElement.GetProperty("catalogManifestSha256").GetString());
                Assert.Equal(Hash(a2), provenance.RootElement.GetProperty("artifactSha256").GetString());
            }
            Assert.True(repo.Install(Request()).NoOp);
            Assert.Contains("downgrade", Assert.Throws<InvalidOperationException>(() =>
                repo.Install(Request("1.0.0"))).Message);
        }
        using (var repo = workspace.Open(Client(Catalog(("1.0.0", m1)),
            ("1.0.0", m1, a1), ("2.0.0", m2, a2))))
        {
            Assert.Equal(SourceKind.AgentSkills, Assert.Single(repo.ListSources().Where(s => s.Name == "skills")).Kind);
            Assert.True(File.Exists(Path.Combine(workspace.AppRoot, "agent-skill-sources.json")));
            Assert.False(File.Exists(Path.Combine(workspace.AppRoot, "sources.json")));
            Assert.True(repo.Install(Request(upgrade: true)).NoOp);
            Assert.Contains("1 AgentSkills", Assert.Single(repo.UpdateSources("skills")).Detail);
            Assert.Contains("downgrade", Assert.Throws<InvalidOperationException>(() =>
                repo.Install(Request(upgrade: true))).Message);
            var cachePath = AgentSkillCatalog.CachePath(workspace.AppRoot,
                Assert.Single(repo.ListSources().Where(s => s.Name == "skills")));
            repo.RemoveSource("skills");
            Assert.DoesNotContain(repo.ListSources(), s => s.Name == "skills");
            Assert.False(File.Exists(cachePath));
        }
    }

    [Fact]
    public void Repository_UpgradeStagesAndProtectsManagedFiles()
    {
        using var workspace = new Workspace();
        var a1 = Archive("one");
        var a2 = Archive("two");
        var m1 = Manifest("1.0.0", a1);
        var m2 = Manifest("2.0.0", a2);
        using (var repo = workspace.Open(Client(Catalog(("1.0.0", m1)), ("1.0.0", m1, a1))))
        {
            repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
            Assert.Contains("No managed", Assert.Throws<InvalidOperationException>(() =>
                repo.Install(Request(upgrade: true))).Message);
            repo.Install(Request());
        }
        var oldState = File.ReadAllText(workspace.Provenance);
        var invalid = Archive("bad", "---\nname: wrong\ndescription: Example\n---\nInstructions.\n");
        using (var repo = workspace.Open(Client(Catalog(("2.0.0", m2)), ("2.0.0", m2, invalid))))
        {
            repo.UpdateSources("skills");
            Assert.Throws<InvalidOperationException>(() => repo.Install(Request(upgrade: true)));
        }
        Assert.Equal(oldState, File.ReadAllText(workspace.Provenance));
        Assert.Equal("one", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
        using (var repo = workspace.Open(Client(Catalog(("2.0.0", m2)), ("2.0.0", m2, a2))))
        {
            repo.UpdateSources("skills");
            Assert.True(repo.Install(Request(upgrade: true) with
            {
                Query = new PackageQuery { Id = PackageId },
            }).Success);
            Assert.True(repo.Install(Request(upgrade: true)).NoOp);
            Assert.Equal("two", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
            using var json = JsonDocument.Parse(File.ReadAllText(workspace.Provenance));
            Assert.Equal("2.0.0", json.RootElement.GetProperty("packageVersion").GetString());
            Assert.Equal(64, json.RootElement.GetProperty("installedTreeSha256").GetString()!.Length);
            File.WriteAllText(Path.Combine(workspace.Skill, "contents.txt"), "edited");
            Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() =>
                repo.Install(Request(upgrade: true))).Message);
        }
    }

    [Fact]
    public void Repository_UpgradeRefusesDifferentCatalogSource()
    {
        using var workspace = new Workspace();
        var first = Archive("one");
        var second = Archive("two");
        var m1 = Manifest("1.0.0", first);
        var m2 = Manifest("2.0.0", second);
        using (var repo = workspace.Open(Client(Catalog(("1.0.0", m1)), ("1.0.0", m1, first))))
        {
            repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
            repo.Install(Request());
        }
        using (var repo = workspace.Open(Client(Catalog(("2.0.0", m2)), ("2.0.0", m2, second))))
        {
            repo.AddSource("other", "https://other.example/catalog.json", SourceKind.AgentSkills);
            var request = Request(upgrade: true) with
            {
                Query = new PackageQuery { Id = PackageId, Source = "other" },
            };
            Assert.Contains("provenance", Assert.Throws<InvalidOperationException>(() => repo.Install(request)).Message);
            Assert.Equal("one", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
        }
    }

    [Fact]
    public void Repository_SameVersionNoOpDoesNotFetchArtifact()
    {
        using var workspace = new Workspace();
        var artifact = Archive("one");
        var manifest = Manifest("1.0.0", artifact);
        var catalog = Catalog(("1.0.0", manifest));
        using (var repo = workspace.Open(Client(catalog, ("1.0.0", manifest, artifact))))
        {
            repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
            repo.Install(Request());
        }
        using var noArtifact = new HttpClient(new Handler(url =>
        {
            if (url.AbsoluteUri == "https://catalog.example/1.0.0.yaml") return Ok(manifest);
            throw new InvalidOperationException("A cached no-op must not fetch the catalog or artifact.");
        }));
        using var reopened = workspace.Open(noArtifact);
        Assert.True(reopened.Install(Request(upgrade: true)).NoOp);
    }

    [Fact]
    public void Repository_RejectsUnmanagedDestinationAndSourceCollision()
    {
        using var workspace = new Workspace();
        var artifact = Archive("one");
        var manifest = Manifest("1.0.0", artifact);
        using var repo = workspace.Open(Client(Catalog(("1.0.0", manifest)), ("1.0.0", manifest, artifact)));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        Assert.Throws<InvalidOperationException>(() =>
            repo.AddSource("skills", "https://other.example", SourceKind.Rest));
        Assert.Contains("query", Assert.Throws<InvalidOperationException>(() =>
            repo.AddSource("tokenized", CatalogUrl + "?token=secret", SourceKind.AgentSkills)).Message);
        Directory.CreateDirectory(workspace.Skill);
        File.WriteAllText(Path.Combine(workspace.Skill, "keep.txt"), "private");
        Assert.Contains("unmanaged", Assert.Throws<InvalidOperationException>(() => repo.Install(Request())).Message);
        Assert.Equal("private", File.ReadAllText(Path.Combine(workspace.Skill, "keep.txt")));
    }

    [Fact]
    public void SearchAgentSkills_OnlyCatalogIdentifiersAndNewestVersions_AreReturned()
    {
        using var workspace = new Workspace();
        var artifact = Archive("one");
        var first = Manifest("1.0.0", artifact);
        var second = Manifest("2.0.0", artifact);
        var catalog = Bytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = new[]
            {
                new { packageIdentifier = PackageId, packageName = SkillName, packageVersion = "1.0.0",
                    manifestUrl = "https://catalog.example/1.0.0.yaml", manifestSha256 = Hash(first) },
                new { packageIdentifier = PackageId, packageName = SkillName, packageVersion = "2.0.0",
                    manifestUrl = "https://catalog.example/2.0.0.yaml", manifestSha256 = Hash(second) },
                new { packageIdentifier = "Another.Tool", packageName = "another-tool", packageVersion = "3.0.0",
                    manifestUrl = "https://catalog.example/other.yaml", manifestSha256 = Hash(first) },
            },
        }));
        using var client = new HttpClient(new Handler(url =>
        {
            Assert.Equal(CatalogUrl, url.AbsoluteUri);
            return Ok(catalog);
        }));
        // An application search with no configured app sources must never fetch the skill catalog.
        Directory.CreateDirectory(workspace.AppRoot);
        SourceStoreManager.Save(new SourceStore { Sources = [] }, workspace.AppRoot);
        using var repo = workspace.Open(client);
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        Assert.Empty(repo.Search(new PackageQuery { Query = "CatalogSkill" }).Matches);
        var results = repo.SearchAgentSkills(new PackageQuery { Query = "catalog" });
        var found = Assert.Single(results.Matches);
        Assert.Equal(PackageId, found.Id);
        Assert.Equal(SkillName, found.Name);
        Assert.Equal("2.0.0", found.Version);
        Assert.Equal("skills", found.SourceName);
        Assert.Equal(SourceKind.AgentSkills, found.SourceKind);
        Assert.Empty(results.SourceWarnings);
        Assert.Contains("search --agent", Assert.Throws<InvalidOperationException>(() =>
            repo.Search(new PackageQuery { Id = PackageId, Source = "skills" })).Message);
        Assert.Contains("AgentSkills", Assert.Throws<InvalidOperationException>(() =>
            repo.Show(new PackageQuery { Id = PackageId, Source = "skills" })).Message);
        Assert.Empty(repo.SearchAgentSkills(new PackageQuery { Query = "not-found" }).Matches);
        Assert.Equal(PackageId, Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Name = SkillName, Exact = true,
        }).Matches).Id);
    }

    [Fact]
    public void SearchAgentSkills_FiltersAndLimitsResultsWithoutDownloadingManifests()
    {
        using var workspace = new Workspace();
        var package = new
        {
            packageVersion = "1.0.0",
            manifestUrl = "https://catalog.example/skill.yaml",
            manifestSha256 = new string('a', 64),
        };
        var catalog = Bytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = new[]
            {
                new { packageIdentifier = "A.Skill", packageName = "a-skill",
                    package.packageVersion, package.manifestUrl, package.manifestSha256 },
                new { packageIdentifier = "B.Skill", packageName = "b-skill",
                    package.packageVersion, package.manifestUrl, package.manifestSha256 },
            },
        }));
        using var repo = workspace.Open(new HttpClient(new Handler(url =>
        {
            Assert.Equal(CatalogUrl, url.AbsoluteUri);
            return Ok(catalog);
        })));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        var one = repo.SearchAgentSkills(new PackageQuery { Query = "skill", Count = 1 });
        Assert.Single(one.Matches);
        Assert.True(one.Truncated);
        Assert.Equal("B.Skill", Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Id = "b.skill", Exact = true,
        }).Matches).Id);
        Assert.Empty(repo.SearchAgentSkills(new PackageQuery { Id = "missing", Exact = true }).Matches);
        Assert.Equal("A.Skill", Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Name = "a-skill", Exact = true,
        }).Matches).Id);
        Assert.Contains("supports only", Assert.Throws<InvalidOperationException>(() =>
            repo.SearchAgentSkills(new PackageQuery { Tag = "unsupported" })).Message);
    }

    [Fact]
    public void SearchAgentSkills_SkipsExplicitSourcesAndReportsCatalogFailures()
    {
        using var workspace = new Workspace();
        var manifest = Manifest("1.0.0", Archive("one"));
        var catalog = Catalog(("1.0.0", manifest));
        using var repo = workspace.Open(new HttpClient(new Handler(url => url.Host switch
        {
            "catalog.example" => Ok(catalog),
            "other.example" => Ok(Bytes("{invalid JSON")),
            _ => throw new InvalidOperationException("Unexpected catalog source."),
        })));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills, explicitSource: true);
        repo.AddSource("broken", "https://other.example/catalog.json", SourceKind.AgentSkills);
        var defaultResults = repo.SearchAgentSkills(new PackageQuery { Id = PackageId, Exact = true });
        Assert.Empty(defaultResults.Matches);
        Assert.Equal("broken", Assert.Single(defaultResults.SourceWarnings).SourceName);
        Assert.Equal(PackageId, Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Id = PackageId, Source = "skills", Exact = true,
        }).Matches).Id);
    }

    [Fact]
    public void Repository_SearchAndInstallUseCachedPinUntilExplicitSourceUpdate()
    {
        using var workspace = new Workspace();
        var a1 = Archive("one");
        var a2 = Archive("two");
        var m1 = Manifest("1.0.0", a1);
        var m2 = Manifest("2.0.0", a2);
        var catalog = Catalog(("1.0.0", m1));
        var catalogFetches = 0;
        using var repo = workspace.Open(new HttpClient(new Handler(url =>
        {
            if (url.AbsoluteUri == CatalogUrl)
            {
                catalogFetches++;
                return Ok(catalog);
            }
            return url.AbsoluteUri switch
            {
                "https://catalog.example/1.0.0.yaml" => Ok(m1),
                "https://catalog.example/1.0.0.zip" => Ok(a1),
                "https://catalog.example/2.0.0.yaml" => Ok(m2),
                "https://catalog.example/2.0.0.zip" => Ok(a2),
                _ => throw new InvalidOperationException("Unexpected request."),
            };
        })));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        Assert.Equal("1.0.0", Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Id = PackageId,
        }).Matches).Version);
        Assert.Equal(1, catalogFetches);
        var source = Assert.Single(repo.ListSources().Where(s => s.Name == "skills"));
        var cache = AgentSkillCatalog.CachePath(workspace.AppRoot, source);
        Assert.True(File.Exists(cache));
        Assert.Equal(Hash(Bytes("skills")), Path.GetFileNameWithoutExtension(cache));
        using (var normalized = JsonDocument.Parse(File.ReadAllBytes(cache)))
        {
            Assert.Equal(2, normalized.RootElement.EnumerateObject().Count());
            Assert.Equal(5, normalized.RootElement.GetProperty("packages")[0].EnumerateObject().Count());
        }
        catalog = Catalog(("2.0.0", m2));
        Assert.Equal("1.0.0", Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Name = SkillName,
        }).Matches).Version);
        Assert.Equal("1.0.0", repo.Install(Request()).Version);
        Assert.Equal(1, catalogFetches);
        Assert.Contains("1 AgentSkills", Assert.Single(repo.UpdateSources("skills")).Detail);
        Assert.Equal("2.0.0", Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Query = SkillName, Exact = true,
        }).Matches).Version);
        Assert.Equal("2.0.0", repo.Install(Request(upgrade: true)).Version);
        Assert.Equal(2, catalogFetches);

        var savedCache = File.ReadAllBytes(cache);
        catalog = Catalog(("2.0.0", Manifest("2.0.0", Archive("changed"))));
        Assert.Contains("publish a new version", Assert.Throws<InvalidOperationException>(() =>
            repo.UpdateSources("skills")).Message);
        Assert.Equal(savedCache, File.ReadAllBytes(cache));
        catalog = Bytes("{malformed JSON");
        Assert.ThrowsAny<JsonException>(() => repo.UpdateSources("skills"));
        Assert.Equal(savedCache, File.ReadAllBytes(cache));
        Assert.Equal("2.0.0", Assert.Single(repo.SearchAgentSkills(new PackageQuery { Id = PackageId }).Matches).Version);
        repo.ResetSource("skills");
        Assert.False(File.Exists(cache));
    }

    [Fact]
    public void Repository_RelativeManifestUrlResolvesAndCachedAbsoluteOrRelativeCatalogLoads()
    {
        using var workspace = new Workspace();
        var artifact = Archive("from relative catalog");
        var manifest = Manifest("1.0.0", artifact);
        var catalog = Bytes(Encoding.UTF8.GetString(Catalog(("1.0.0", manifest)))
            .Replace("https://catalog.example/1.0.0.yaml", "manifests/demo.yaml", StringComparison.Ordinal));
        const string resolvedManifestUrl = "https://catalog.example/manifests/demo.yaml";
        using var client = new HttpClient(new Handler(url => url.AbsoluteUri switch
        {
            CatalogUrl => Ok(catalog),
            resolvedManifestUrl => Ok(manifest),
            "https://catalog.example/1.0.0.zip" => Ok(artifact),
            _ => throw new InvalidOperationException("Unexpected URL: " + url.AbsoluteUri),
        }));
        using (var repo = workspace.Open(client))
        {
            repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
            var source = Assert.Single(repo.ListSources(), entry => entry.Name == "skills");
            Assert.Equal("1.0.0", AgentSkillCatalog.Resolve(client, source, PackageId, null).Version);
            Assert.Contains("1 AgentSkills", Assert.Single(repo.UpdateSources("skills")).Detail);
            var cache = AgentSkillCatalog.CachePath(workspace.AppRoot, source);
            using (var normalized = JsonDocument.Parse(File.ReadAllBytes(cache)))
                Assert.Equal(resolvedManifestUrl,
                    normalized.RootElement.GetProperty("packages")[0].GetProperty("manifestUrl").GetString());
            Assert.Equal(SkillName, Assert.Single(repo.SearchAgentSkills(new PackageQuery { Id = PackageId }).Matches).Name);
            File.WriteAllBytes(cache, catalog);
        }
        using var reopened = workspace.Open(new HttpClient(new Handler(url => url.AbsoluteUri switch
        {
            resolvedManifestUrl => Ok(manifest),
            "https://catalog.example/1.0.0.zip" => Ok(artifact),
            _ => throw new InvalidOperationException("Cached relative catalog must not refetch: " + url.AbsoluteUri),
        })));
        Assert.Equal("1.0.0", reopened.Install(Request()).Version);
        Assert.Equal("from relative catalog", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
    }

    [Theory]
    [InlineData("manifestSha256")]
    [InlineData("manifestUrl")]
    [InlineData("packageName")]
    public void Repository_SourceUpdateRejectsChangedPublishedVersionAndPreservesCache(string changedField)
    {
        using var workspace = new Workspace();
        var catalog = Catalog(("1.0.0", Manifest("1.0.0", Archive("original"))));
        using var repo = workspace.Open(new HttpClient(new Handler(url =>
            url.AbsoluteUri == CatalogUrl ? Ok(catalog) : throw new InvalidOperationException("Unexpected request."))));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        repo.UpdateSources("skills");
        var source = Assert.Single(repo.ListSources(), entry => entry.Name == "skills");
        var originalUpdate = source.LastUpdate;
        var cache = AgentSkillCatalog.CachePath(workspace.AppRoot, source);
        var originalCache = File.ReadAllBytes(cache);
        catalog = changedField switch
        {
            "manifestSha256" => Catalog(("1.0.0", Manifest("1.0.0", Archive("repinned")))),
            "manifestUrl" => Bytes(Encoding.UTF8.GetString(catalog)
                .Replace("https://catalog.example/1.0.0.yaml", "https://catalog.example/replaced.yaml", StringComparison.Ordinal)),
            "packageName" => Bytes(Encoding.UTF8.GetString(catalog)
                .Replace("\"packageName\":\"catalog-skill\"", "\"packageName\":\"replacement-skill\"", StringComparison.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };

        Assert.Contains("publish a new version", Assert.Throws<InvalidOperationException>(() =>
            repo.UpdateSources("skills")).Message);
        Assert.Equal(originalCache, File.ReadAllBytes(cache));
        Assert.Equal(originalUpdate, source.LastUpdate);
        Assert.Equal(SkillName, Assert.Single(repo.SearchAgentSkills(new PackageQuery { Id = PackageId }).Matches).Name);
    }

    [Fact]
    public void Repository_ReadsSharedRustStyleSourceAndCatalogCacheWithoutRefetch()
    {
        using var workspace = new Workspace();
        var artifact = Archive("shared");
        var manifest = Manifest("1.0.0", artifact);
        Directory.CreateDirectory(workspace.AppRoot);
        File.WriteAllText(Path.Combine(workspace.AppRoot, "agent-skill-sources.json"),
            """{"sources":[{"name":"skills","kind":"AgentSkills","arg":"https://catalog.example/catalog.json","identifier":"skills"}]}""");
        var cache = AgentSkillCatalog.CachePath(workspace.AppRoot, new SourceRecord
        {
            Name = "skills", Kind = SourceKind.AgentSkills, Arg = CatalogUrl, Identifier = "skills",
        });
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllBytes(cache, Catalog(("1.0.0", manifest)));
        using var repo = workspace.Open(new HttpClient(new Handler(url => url.AbsoluteUri switch
        {
            "https://catalog.example/1.0.0.yaml" => Ok(manifest),
            "https://catalog.example/1.0.0.zip" => Ok(artifact),
            _ => throw new InvalidOperationException("Shared cache must avoid fetching the catalog."),
        })));
        Assert.Equal(SkillName, Assert.Single(repo.SearchAgentSkills(new PackageQuery
        {
            Id = PackageId,
        }).Matches).Name);
        Assert.Equal("1.0.0", repo.Install(Request()).Version);
        Assert.Equal("shared", File.ReadAllText(Path.Combine(workspace.Skill, "contents.txt")));
    }

    [Fact]
    public void Repository_OmittedSourceSelectsUniqueCatalogButRejectsAmbiguity()
    {
        using var workspace = new Workspace();
        var archive = Archive("one");
        var manifest = Manifest("1.0.0", archive);
        var matching = Catalog(("1.0.0", manifest));
        using var repo = workspace.Open(new HttpClient(new Handler(url => url.AbsoluteUri switch
        {
            CatalogUrl => Ok(Bytes("""{"schemaVersion":1,"packages":[]}""")),
            "https://other.example/catalog.json" => Ok(matching),
            "https://catalog.example/1.0.0.yaml" => Ok(manifest),
            "https://catalog.example/1.0.0.zip" => Ok(archive),
            _ => throw new InvalidOperationException("Unexpected request"),
        })));
        repo.AddSource("skills", CatalogUrl, SourceKind.AgentSkills);
        repo.AddSource("other", "https://other.example/catalog.json", SourceKind.AgentSkills);
        Assert.Equal("1.0.0", repo.Install(Request() with
        {
            Query = new PackageQuery { Id = PackageId },
        }).Version);
        using (var provenance = JsonDocument.Parse(File.ReadAllText(workspace.Provenance)))
            Assert.Equal("other", provenance.RootElement.GetProperty("catalogSource").GetString());
        using var ambiguous = workspace.Open(new HttpClient(new Handler(url =>
            url.AbsolutePath == "/catalog.json" ? Ok(matching) :
            throw new InvalidOperationException("Unexpected manifest or artifact request."))));
        ambiguous.UpdateSources("skills");
        Assert.Contains("specify --source", Assert.Throws<InvalidOperationException>(() =>
            ambiguous.Install(Request() with { Query = new PackageQuery { Id = PackageId } })).Message);
    }
}
