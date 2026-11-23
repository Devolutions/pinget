using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Devolutions.Pinget.Core;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class HomebrewSourceTests
{
    [Fact]
    public void AddHomebrewSourcePersistsPublisherAndExplicitTrustAnchors()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var anchor = Path.Combine(temp.Path, "test-root.cer");
        File.WriteAllText(anchor, "test certificate bytes");
        var appRoot = Path.Combine(temp.Path, "app");
        using (var repository = Repository.Open(new RepositoryOptions
        {
            AppRoot = appRoot,
            SourceMode = SourceMode.Private,
        }))
        {
            repository.AddHomebrewSource(
                "homebrew",
                "https://packages.example.test/brew/",
                "CN=Pinget Homebrew Source",
                [anchor]);
        }

        using var reopened = Repository.Open(new RepositoryOptions
        {
            AppRoot = appRoot,
            SourceMode = SourceMode.Private,
        });
        var source = Assert.Single(reopened.ListSources(), item => item.Name == "homebrew");
        Assert.Equal(SourceKind.HomebrewPreIndexed, source.Kind);
        Assert.Equal("https://packages.example.test/brew", source.Arg);
        Assert.Equal("CN=Pinget Homebrew Source", source.Publisher);
        Assert.Equal(Path.GetFullPath(anchor), Assert.Single(source.TrustedCa));
    }

    [Fact]
    public void PowerShellEngineRegistersAndListsHomebrewVerificationSettings()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var anchor = Path.Combine(temp.Path, "test-root.cer");
        File.WriteAllText(anchor, "test certificate bytes");
        using var client = PingetClient.Open(new RepositoryOptions
        {
            AppRoot = Path.Combine(temp.Path, "app"),
            SourceMode = SourceMode.Private,
        });

        client.AddSource(
            "homebrew",
            "https://packages.example.test/brew",
            "Pinget.PreIndexed.Homebrew",
            PSSourceTrustLevel.Default,
            explicitSource: false,
            priority: 0,
            publisher: "CN=Pinget Homebrew Source",
            trustedCa: [anchor]);

        var listed = Assert.Single(client.GetSources("homebrew").Items);
        Assert.Equal("Pinget.PreIndexed.Homebrew", listed.Type);
        Assert.Equal("CN=Pinget Homebrew Source", listed.Publisher);
        Assert.Equal(Path.GetFullPath(anchor), Assert.Single(listed.TrustedCa));
    }

    [Theory]
    [InlineData("https://user@packages.example.test/brew")]
    [InlineData("https://packages.example.test/brew?token=secret")]
    [InlineData("https://packages.example.test/brew/../private")]
    [InlineData("https://packages.example.test/brew%2fprivate")]
    public void AddHomebrewSourceRejectsCredentialsQueriesAndUnsafePaths(string sourceUrl)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        using var repository = Repository.Open(new RepositoryOptions
        {
            AppRoot = temp.Path,
            SourceMode = SourceMode.Private,
        });

        Assert.Throws<InvalidOperationException>(() =>
            repository.AddHomebrewSource("homebrew", sourceUrl, "CN=Publisher", []));
    }

    [Fact]
    public void UpdateRequestsOnlyBrewMsixAndPreservesCacheWhenPsignIsMissing()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var source = new SourceRecord
        {
            Name = "homebrew",
            Kind = SourceKind.HomebrewPreIndexed,
            Arg = "https://packages.example.test/brew",
            Identifier = "homebrew",
            Publisher = "CN=Pinget Homebrew Source",
        };
        var stateDir = SourceStoreManager.SourceStateDir(source, temp.Path);
        Directory.CreateDirectory(stateDir);
        var oldIndex = Encoding.UTF8.GetBytes("previous verified index");
        var oldPackage = Encoding.UTF8.GetBytes("previous verified package");
        File.WriteAllBytes(PreIndexedSource.IndexPath(source, temp.Path), oldIndex);
        File.WriteAllBytes(Path.Combine(stateDir, "brew.msix"), oldPackage);

        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(BuildUnsignedMsix()),
            };
        }));

        var missingPsign = Path.Combine(temp.Path, "missing", "psign-tool");
        var error = Assert.Throws<InvalidOperationException>(() =>
            HomebrewPreIndexedSource.Update(client, source, temp.Path, missingPsign));

        Assert.Contains("psign-tool does not exist", error.Message, StringComparison.Ordinal);
        Assert.Equal(["/brew/brew.msix"], requests);
        Assert.Equal(oldIndex, File.ReadAllBytes(PreIndexedSource.IndexPath(source, temp.Path)));
        Assert.Equal(oldPackage, File.ReadAllBytes(Path.Combine(stateDir, "brew.msix")));
        Assert.Empty(Directory.GetFiles(stateDir, "*.tmp"));
    }

    private static byte[] BuildUnsignedMsix()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("AppxManifest.xml");
            using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Pinget.HomebrewSource" Publisher="CN=Pinget Homebrew Source" Version="1.0.0.0" /></Package>""");

            var signature = archive.CreateEntry("AppxSignature.p7x");
            using var signatureStream = signature.Open();
            signatureStream.Write([1, 2, 3]);
        }
        return stream.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pinget-homebrew-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
