using System.Net;
using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Cli;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>
/// Review fixes A: manifest BOMs, <c>validate</c> required values, <c>--rename</c> extensions, and REST/YAML
/// parity with the Rust implementation.
/// </summary>
public class ReviewFixesATests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-review-a-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private const string Singleton = """
        PackageIdentifier: Test.ReviewA
        PackageVersion: 1.10
        PackageName: Test ReviewA
        Publisher: Example
        ManifestType: singleton
        ManifestVersion: 1.10.0
        InstallerType: exe
        Installers:
          - Architecture: x64
            InstallerUrl: https://example.test/setup.exe
            InstallerSha256: AAA
            Dependencies:
              PackageDependencies:
                - PackageIdentifier: Microsoft.VCRedist.2015+.x64
                  MinimumVersion: 14.30
        """;

    // ── #1: a UTF-8 BOM does not change how a YAML manifest parses ──

    [Fact]
    public void BomPrefixedYamlManifest_ParsesLikeTheUnprefixedOne()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Singleton)).ToArray();
        var manifest = Repository.ParseYamlManifest(bytes);

        Assert.Equal("Test.ReviewA", manifest.Id);
        Assert.Equal("Test ReviewA", manifest.Name);
        Assert.Equal("1.10", manifest.Version);
        var installer = Assert.Single(manifest.Installers);
        Assert.Equal("14.30", Assert.Single(installer.Dependencies.PackageDependencies).MinimumVersion);
    }

    // ── #3: validate defaults an empty ManifestType to singleton and requires values ──

    [Theory]
    [InlineData("PackageIdentifier:\nPackageVersion: ''\nManifestType:\nManifestVersion: 1.6.0\nInstallers: ~\n")]
    [InlineData("PackageIdentifier: ~\nPackageVersion: null\nManifestType: ~\nManifestVersion: 1.6.0\nInstallers: []\n")]
    public void Validate_ReportsEmptyRequiredFieldsWhenManifestTypeIsEmpty(string content)
    {
        var directory = CreateTempDirectory();
        try
        {
            var manifest = Path.Combine(directory, "manifest.yaml");
            File.WriteAllText(manifest, content);
            var report = ManifestValidator.Validate(manifest);
            foreach (var field in new[] { "'Installers'", "'PackageIdentifier'", "'PackageVersion'" })
                Assert.Contains(report.Errors, error => error.Contains(field));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Validate_DirectoryModeMatchesExtensionsCaseInsensitively()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "Upper.YAML"), "PackageIdentifier: Contoso.Tool\nManifestType: singleton\nManifestVersion: 1.6.0\n");
            File.WriteAllText(Path.Combine(directory, "Mixed.Yml"),
                "PackageIdentifier: Contoso.Tool\nPackageVersion: 1.10\nManifestType: singleton\nManifestVersion: 1.6.0\nInstallers:\n  - InstallerUrl: https://example.test/a.exe\n");
            Directory.CreateDirectory(Path.Combine(directory, "folder.yaml"));

            var report = ManifestValidator.Validate(directory);
            Assert.Contains(report.Errors, error => error.Contains("Upper.YAML") && error.Contains("'Installers'"));
            Assert.DoesNotContain(report.Errors, error => error.Contains("Mixed.Yml"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // ── #5: a --rename download name keeps the installer type's extension ──

    [Theory]
    [InlineData("mytool", "exe", "mytool.exe")]
    [InlineData("mytool", "inno", "mytool.exe")]
    [InlineData("mytool", "msi", "mytool.msi")]
    [InlineData("mytool", "msix", "mytool.msix")]
    [InlineData("mytool.appxbundle", "msix", "mytool.appxbundle")]
    [InlineData("custom.EXE", "exe", "custom.EXE")]
    [InlineData("mytool", "font", "mytool")]
    public void RenamedDownload_GetsTheInstallerTypeExtension(string rename, string installerType, string expected)
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(Singleton));
        Assert.Equal(expected, Repository.InstallerDownloadFileName("https://example.test/setup.bin", rename, installerType, manifest));
    }

    // ── #8: REST update adopts the server's SourceIdentifier, like Rust ──

    [Fact]
    public void UpdateRest_AdoptsTheServerSourceIdentifier()
    {
        var appRoot = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Data":{"SourceIdentifier":"Test.Rest","ServerSupportedVersions":["1.12.0"]}}""",
                    Encoding.UTF8, "application/json"),
            }));
            var source = new SourceRecord
            {
                Name = "rest",
                Kind = SourceKind.Rest,
                Arg = "https://rest.example/api",
                Identifier = "rest",
                TrustLevel = "Trusted",
            };

            RestSource.UpdateRest(client, source, appRoot);

            Assert.Equal("Test.Rest", source.Identifier);
            Assert.Equal("1.12.0", source.SourceVersion);
        }
        finally
        {
            DeleteDirectory(appRoot);
        }
    }

    // ── #8: REST return codes given as strings may be 0x hex, like Rust and the YAML reader ──

    [Fact]
    public void ParseRestManifest_ReadsHexStringReturnCodes()
    {
        using var document = JsonDocument.Parse("""
            {
              "Data": {
                "PackageIdentifier": "Contoso.Tool",
                "Versions": [
                  {
                    "PackageVersion": "1.0.0",
                    "DefaultLocale": { "PackageLocale": "en-US", "Publisher": "Contoso", "PackageName": "Tool" },
                    "Installers": [
                      {
                        "Architecture": "x64",
                        "InstallerType": "exe",
                        "InstallerUrl": "https://example.test/setup.exe",
                        "InstallerSha256": "AAA",
                        "InstallerSuccessCodes": ["0x10", "3010", 1641],
                        "ExpectedReturnCodes": [
                          { "InstallerReturnCode": "0x8A150001", "ReturnResponse": "packageInUse" }
                        ]
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var (manifest, _) = RestSource.ParseRestManifest(document.RootElement, "Contoso.Tool", "1.0.0", "");
        var installer = Assert.Single(manifest.Installers);
        Assert.Equal([16L, 3010L, 1641L], installer.InstallerSuccessCodes);
        var expected = Assert.Single(installer.ExpectedReturnCodes);
        Assert.Equal(0x8A150001L, expected.InstallerReturnCode);
        Assert.Equal("packageInUse", expected.ReturnResponse);
    }

    // ── #8: an explicit empty installer-level list overrides the root default, like Rust ──

    [Fact]
    public void ExplicitEmptyInstallerLists_OverrideRootDefaults()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Test.ReviewA
            PackageVersion: 1.0
            PackageName: Test ReviewA
            Publisher: Example
            ManifestType: singleton
            ManifestVersion: 1.10.0
            InstallerType: zip
            NestedInstallerType: portable
            NestedInstallerFiles:
              - RelativeFilePath: tool.exe
            Platform: [Windows.Desktop]
            AppsAndFeaturesEntries:
              - DisplayName: Root
            Installers:
              - Architecture: x64
                InstallerUrl: https://example.test/x64.zip
                InstallerSha256: AAA
                NestedInstallerFiles: []
                Platform: []
                AppsAndFeaturesEntries: []
              - Architecture: arm64
                InstallerUrl: https://example.test/arm64.zip
                InstallerSha256: BBB
            """));

        var overriding = manifest.Installers[0];
        Assert.Empty(overriding.NestedInstallerFiles);
        Assert.Empty(overriding.Platforms);
        Assert.Empty(overriding.AppsAndFeaturesEntries);

        var inheriting = manifest.Installers[1];
        Assert.Single(inheriting.NestedInstallerFiles);
        Assert.Equal(["Windows.Desktop"], inheriting.Platforms);
        Assert.Single(inheriting.AppsAndFeaturesEntries);
    }
}
