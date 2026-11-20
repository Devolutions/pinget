using System.Text.Json.Nodes;
using Devolutions.Pinget.Core;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

public sealed class HomebrewImporterTests
{
    [Fact]
    public void PinnedImport_MirrorsExactBytesAndIsDeterministic()
    {
        using var fixture = new Fixture();
        var report = HomebrewImporter.Import(fixture.Options);
        var manifestPath = Path.Combine(fixture.Options.ManifestOutput, report["manifestFile"]!.GetValue<string>());
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(System.Text.Encoding.UTF8.GetString(manifestBytes)));
        var root = Assert.IsType<YamlDotNet.RepresentationModel.YamlMappingNode>(yaml.Documents[0].RootNode);
        var homebrew = root["Pinget"]["Homebrew"];
        Assert.Equal(HomebrewPackageIds.Create("homebrew/core", "tinytool"), report["packageIdentifier"]!.GetValue<string>());
        Assert.Equal("1.2.3_1", report["packageVersion"]!.GetValue<string>());
        Assert.Equal("singleton", root["ManifestType"].ToString());
        Assert.Equal("https://tinytool.example.invalid/", root["PackageUrl"].ToString());
        Assert.Equal("Small dependency-free test utility", root["ShortDescription"].ToString());
        Assert.Equal("Unknown", root["Publisher"].ToString());
        Assert.NotEqual("homebrew/core", root["Publisher"].ToString());
        Assert.Equal("homebrew-bottle", root["Installers"][0]["InstallerType"].ToString());
        Assert.Equal("1", homebrew["Revision"].ToString());
        Assert.Equal("3", homebrew["Bottles"][0]["Rebuild"].ToString());
        Assert.Equal("14.0", homebrew["Bottles"][0]["MinimumOSVersion"].ToString());
        Assert.Equal(root["Installers"][0]["InstallerSha256"].ToString(), homebrew["Bottles"][0]["Sha256"].ToString());
        Assert.Equal(root["Installers"][0]["InstallerUrl"].ToString(), homebrew["Bottles"][0]["Url"].ToString());
        Assert.Equal(new[] { "small", "tiny" }, ((YamlDotNet.RepresentationModel.YamlSequenceNode)homebrew["Aliases"]).Select(n => n.ToString()));
        Assert.Equal(File.ReadAllBytes(fixture.Options.BottleArchive),
            File.ReadAllBytes(Path.Combine(fixture.Options.BottleMirror, report["bottleFile"]!.GetValue<string>())));
        var reportPath = manifestPath + ".import.json";
        var reportBytes = File.ReadAllBytes(reportPath);
        Assert.True(JsonNode.DeepEquals(report, JsonNode.Parse(reportBytes)));
        var state = Path.Combine(fixture.Root, "state");
        var input = PublisherInput(fixture);
        var build = new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state));
        Assert.Equal(1, build.PackageCount);
        Assert.Equal(1, build.ManifestCount);
        var snapshot = JsonNode.Parse(File.ReadAllBytes(Path.Combine(build.SnapshotDirectory, "snapshot.json")));
        Assert.Equal("homebrew", snapshot!["profile"]!.GetValue<string>());
        var bottleFile = report["bottleFile"]!.GetValue<string>();
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(fixture.Options.BottleMirror, bottleFile)),
            File.ReadAllBytes(Path.Combine(build.SnapshotDirectory, "feed", "bottles", bottleFile)));
        var msix = Path.Combine(fixture.Root, "package", "brew.msix");
        PublisherPackaging.Package(new PublisherPackageOptions(
            state, msix, "Pinget.Test.Homebrew", "CN=Pinget Homebrew", "1.0.0.0", build.BuildId));
        var destination = Path.Combine(fixture.Root, "static");
        PublisherPackaging.Publish(new PublisherPublishOptions(
            state, destination, msix, UnsignedDevelopment: true, BuildId: build.BuildId));
        Assert.True(File.Exists(Path.Combine(destination, "brew.msix")));
        Assert.False(File.Exists(Path.Combine(destination, "source2.msix")));
        var marker = JsonNode.Parse(File.ReadAllBytes(Path.Combine(destination, ".pinget-publisher.json")));
        Assert.Equal("brew.msix", marker!["packageFileName"]!.GetValue<string>());
        Assert.Equal("homebrew", marker["profile"]!.GetValue<string>());
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(fixture.Options.BottleMirror, bottleFile)),
            File.ReadAllBytes(Path.Combine(destination, "bottles", bottleFile)));
        PublisherPackaging.VerifyDestination(destination);
        HomebrewImporter.Import(fixture.Options);
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(reportBytes, File.ReadAllBytes(reportPath));
    }

    [Fact]
    public void Build_RejectsMixedHomebrewAndWindowsProfiles()
    {
        using var fixture = new Fixture();
        HomebrewImporter.Import(fixture.Options);
        File.WriteAllText(Path.Combine(fixture.Options.ManifestOutput, "Pinget.Contract.Singleton.yaml"), """
            PackageIdentifier: Pinget.Contract.Singleton
            PackageVersion: 1.0.0
            PackageLocale: en-US
            Publisher: Pinget Test Fixtures
            PackageName: Pinget Contract Fixture
            ShortDescription: Harmless production publisher conformance fixture
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.invalid/a.exe
                InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
            ManifestType: singleton
            ManifestVersion: 1.10.0
            """);
        var state = Path.Combine(fixture.Root, "state");
        var error = Assert.Throws<PublisherException>(() =>
            new PublisherWorkspace(state).Build(new PublisherBuildOptions(PublisherInput(fixture), state)));
        Assert.Contains("cannot mix Homebrew and Windows", error.Message);
        Assert.False(Directory.Exists(Path.Combine(state, "builds")));
    }

    [Fact]
    public void Build_RequiresValidMirroredBottlesForHomebrewManifests()
    {
        using (var fixture = new Fixture())
        {
            HomebrewImporter.Import(fixture.Options);
            var input = PublisherInput(fixture);
            File.Delete(Directory.EnumerateFiles(Path.Combine(input, "bottles")).Single());
            var state = Path.Combine(fixture.Root, "missing-state");
            var error = Assert.Throws<PublisherException>(() =>
                new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));
            Assert.Contains("missing from the bottles/ mirror", error.Message);
        }

        using (var fixture = new Fixture())
        {
            HomebrewImporter.Import(fixture.Options);
            var input = PublisherInput(fixture);
            File.WriteAllText(Directory.EnumerateFiles(Path.Combine(input, "bottles")).Single(), "tampered");
            var state = Path.Combine(fixture.Root, "corrupt-state");
            var error = Assert.Throws<PublisherException>(() =>
                new PublisherWorkspace(state).Build(new PublisherBuildOptions(input, state)));
            Assert.Contains("hash does not match filename", error.Message);
        }
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("recommended_dependencies")]
    [InlineData("optional_dependencies")]
    [InlineData("uses_from_macos")]
    [InlineData("requirements")]
    [InlineData("conflicts_with")]
    public void RejectsRuntimeDependenciesAndRequirements(string field)
    {
        using var fixture = new Fixture();
        fixture.Change(v => v[field] = new JsonArray("runtime"));
        fixture.Reject(field);
    }

    [Fact]
    public void RejectsVariations()
    {
        using var fixture = new Fixture();
        fixture.Change(v => v["variations"] = JsonNode.Parse("""{"arm64_sonoma":{"dependencies":["runtime"]}}"""));
        fixture.Reject("variations");
    }

    [Theory]
    [InlineData("tap", "\"homebrew\"", "identity")]
    [InlineData("full_name", "\"other\"", "full_name")]
    [InlineData("versions/stable", "\"../bad\"", "version")]
    [InlineData("revision", "-1", "revision")]
    [InlineData("homepage", "\"https://user:secret@example.invalid/\"", "homepage")]
    [InlineData("version_scheme", "6", "version_scheme")]
    [InlineData("bottle/stable/rebuild", "-1", "rebuild")]
    [InlineData("bottle/stable/files/arm64_sonoma/cellar", "\"/opt/homebrew/Cellar\"", "fixed-cellar")]
    [InlineData("bottle/stable/files/arm64_sonoma/sha256", "\"bad\"", "sha256")]
    [InlineData("bottle/stable/files/arm64_sonoma/sha256", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "SHA-256 mismatch")]
    [InlineData("bottle/stable/files/arm64_sonoma/url", "\"https://user:secret@example.invalid/bottle\"", "public HTTPS")]
    public void RejectsBadMetadataAndHashWithoutOutputs(string path, string json, string message)
    {
        using var fixture = new Fixture();
        fixture.Change(v =>
        {
            var parts = path.Split('/');
            JsonNode parent = v;
            foreach (var part in parts[..^1])
                parent = parent[part]!;
            parent[parts[^1]] = JsonNode.Parse(json);
        });
        fixture.Reject(message);
    }

    [Fact]
    public void RejectsMissingSelectedBottle()
    {
        using var fixture = new Fixture();
        fixture.Change(v => v["bottle"]!["stable"]!["files"] = new JsonObject());
        fixture.Reject("no arm64_sonoma bottle");
    }

    [Fact]
    public void RejectsUnsupportedTargetsOverlapAndSecretUrls()
    {
        using var fixture = new Fixture();
        fixture.Options = fixture.Options with { Tag = "unknown" };
        fixture.Reject("unsupported target");
        fixture.Options = fixture.Options with { Tag = "arm64_sonoma", Architecture = "x64" };
        fixture.Reject("unsupported target");
        fixture.Options = fixture.Options with { Architecture = "arm64" };
        foreach (var url in new[] { "https://user:secret@example.invalid/bottles", "https://example.invalid/bottles?token=secret", "https://example.invalid/%2e%2e" })
        {
            fixture.Options = fixture.Options with { BaseUrl = url };
            fixture.Reject("base URL");
            Assert.DoesNotContain("secret", Assert.Throws<PublisherException>(() => HomebrewImporter.Import(fixture.Options)).Message);
        }
        fixture.Options = fixture.Options with { BaseUrl = "https://example.invalid/bottles", ManifestOutput = fixture.Root };
        Assert.Contains("disjoint", Assert.Throws<PublisherException>(() => HomebrewImporter.Import(fixture.Options)).Message);
        fixture.Options = fixture.Options with { ManifestOutput = Path.Combine(fixture.Root, "out"), BottleMirror = Path.Combine(fixture.Root, "out", "bottles") };
        fixture.Reject("disjoint");
    }

    [Fact]
    public void RejectsCorruptExistingMirrorWithoutWritingManifests()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Options.BottleMirror);
        var formula = JsonNode.Parse(File.ReadAllBytes(fixture.Options.FormulaJson))!;
        var hash = formula["bottle"]!["stable"]!["files"]!["arm64_sonoma"]!["sha256"]!.GetValue<string>();
        var path = Path.Combine(fixture.Options.BottleMirror, $"{hash}.tar.gz");
        File.WriteAllText(path, "corruption");
        Assert.Contains("different bytes", Assert.Throws<PublisherException>(() => HomebrewImporter.Import(fixture.Options)).Message);
        Assert.False(Directory.Exists(fixture.Options.ManifestOutput));
        Assert.Equal("corruption", File.ReadAllText(path));
    }

    [Fact]
    public void RejectsSymlinkDestinations()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        var real = Path.Combine(fixture.Root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(fixture.Root, "link");
        Directory.CreateSymbolicLink(link, real);
        fixture.Options = fixture.Options with { ManifestOutput = Path.Combine(link, "manifests") };
        fixture.Reject("symbolic links");
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), ".homebrew-tests", Guid.NewGuid().ToString("N"));
        public HomebrewImportOptions Options { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Options = new HomebrewImportOptions(Path.Combine(Root, "formula.json"), Path.Combine(Root, "input.tar.gz"),
                Path.Combine(Root, "manifests"), Path.Combine(Root, "bottles"), "arm64_sonoma", "macOS", "arm64",
                "https://mirror.example.invalid/bottles/");
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Homebrew");
            File.Copy(Path.Combine(fixtures, "formula.json"), Options.FormulaJson);
            File.WriteAllBytes(Options.BottleArchive, Convert.FromBase64String(File.ReadAllText(Path.Combine(fixtures, "bottle.base64")).Trim()));
        }

        public void Change(Action<JsonNode> mutate)
        {
            var formula = JsonNode.Parse(File.ReadAllBytes(Options.FormulaJson))!;
            mutate(formula);
            File.WriteAllText(Options.FormulaJson, formula.ToJsonString());
        }

        public void Reject(string message)
        {
            Assert.Contains(message, Assert.Throws<PublisherException>(() => HomebrewImporter.Import(Options)).Message);
            Assert.False(Directory.Exists(Options.ManifestOutput));
            Assert.False(Directory.Exists(Options.BottleMirror));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static string PublisherInput(Fixture fixture)
    {
        var input = Path.Combine(fixture.Root, "publisher-input");
        var manifests = Path.Combine(input, "manifests");
        var bottles = Path.Combine(input, "bottles");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(bottles);
        foreach (var path in Directory.EnumerateFiles(fixture.Options.ManifestOutput, "*.yaml"))
            File.Copy(path, Path.Combine(manifests, Path.GetFileName(path)));
        foreach (var path in Directory.EnumerateFiles(fixture.Options.BottleMirror))
            File.Copy(path, Path.Combine(bottles, Path.GetFileName(path)));
        return input;
    }
}
