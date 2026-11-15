using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>
/// Wave 18 regression tests: manifest model, YAML parsing and emission, dependencies, MSZIP bounds.
/// Mirrors rust/crates/pinget-core/src/wave18_tests.rs.
/// </summary>
public class Wave18DefectTests
{
    private const string Header = """
        PackageIdentifier: Test.Wave18
        PackageVersion: 1.0.0
        PackageName: Test Wave18
        Publisher: Example
        ManifestType: singleton
        ManifestVersion: 1.10.0

        """;

    private static Manifest Parse(string body) => Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(Header + body));

    private static Dictionary<string, object?> Structured(string yaml) =>
        StructuredOutput.CollapseManifestDocuments(Repository.ParseYamlManifestDocuments(Encoding.UTF8.GetBytes(yaml)));

    // W18-1: an installer key with an empty (null) value is ignored, like WinGet's populator, instead of
    // erasing the value the installer inherits from the root.
    [Fact]
    public void NullInstallerValue_KeepsRootDefault()
    {
        var manifest = Parse("""
            Scope: machine
            MinimumOSVersion: 10.0.17763.0
            Platform: [Windows.Desktop]
            Commands: [tool]
            InstallerSuccessCodes: [3010]
            DownloadCommandProhibited: true
            Markets:
              AllowedMarkets: [US]
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
                Scope:
                MinimumOSVersion:
                Platform:
                Commands:
                InstallerSuccessCodes:
                DownloadCommandProhibited:
                Markets:
            """);
        var installer = Assert.Single(manifest.Installers);
        Assert.Equal("machine", installer.Scope);
        Assert.Equal("10.0.17763.0", installer.MinimumOsVersion);
        Assert.Equal(["Windows.Desktop"], installer.Platforms);
        Assert.Equal(["tool"], installer.Commands);
        Assert.Equal([3010L], installer.InstallerSuccessCodes);
        Assert.True(installer.DownloadCommandProhibited);
        Assert.Equal(["US"], installer.AllowedMarkets);
    }

    // W18-2: a root ProductCode / AppsAndFeaturesEntries only flows into installers whose effective type
    // writes an ARP entry (WinGet DoesInstallerTypeUseProductCode / ...WriteAppsAndFeaturesEntry).
    [Fact]
    public void RootProductCodeAndArpEntries_OnlyReachArpInstallerTypes()
    {
        var manifest = Parse("""
            ProductCode: '{ROOT-PRODUCT}'
            AppsAndFeaturesEntries:
              - DisplayName: Root
                UpgradeCode: '{ROOT-UPGRADE}'
            Installers:
              - InstallerType: msi
                InstallerUrl: https://example.test/a.msi
              - InstallerType: msix
                InstallerUrl: https://example.test/a.msix
              - InstallerType: zip
                NestedInstallerType: wix
                NestedInstallerFiles:
                  - RelativeFilePath: setup.msi
                InstallerUrl: https://example.test/a.zip
            """);
        var msi = manifest.Installers[0];
        var msix = manifest.Installers[1];
        var zip = manifest.Installers[2];
        Assert.Equal("{ROOT-PRODUCT}", msi.ProductCode);
        Assert.Single(msi.AppsAndFeaturesEntries);
        Assert.Null(msix.ProductCode);
        Assert.Empty(msix.AppsAndFeaturesEntries);
        Assert.Equal("{ROOT-PRODUCT}", zip.ProductCode);
        Assert.Single(zip.AppsAndFeaturesEntries);
    }

    // W18-3: a root PackageFamilyName only flows into msix/msstore installers (or one whose
    // AppsAndFeaturesEntries name an msix type), like WinGet DoesInstallerTypeUsePackageFamilyName.
    [Fact]
    public void RootPackageFamilyName_OnlyReachesMsixInstallerTypes()
    {
        var manifest = Parse("""
            PackageFamilyName: Contoso.App_8wekyb3d8bbwe
            Installers:
              - InstallerType: appx
                InstallerUrl: https://example.test/a.appx
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
              - InstallerType: exe
                InstallerUrl: https://example.test/b.exe
                AppsAndFeaturesEntries:
                  - InstallerType: msix
            """);
        Assert.Equal(
            ["Contoso.App_8wekyb3d8bbwe", null, "Contoso.App_8wekyb3d8bbwe"],
            manifest.Installers.Select(installer => installer.PackageFamilyName).ToList());
    }

    // W18-4: root NestedInstallerType/NestedInstallerFiles only flow into archive (zip) installers.
    [Fact]
    public void RootNestedInstallerFields_OnlyReachArchiveInstallers()
    {
        var manifest = Parse("""
            NestedInstallerType: portable
            NestedInstallerFiles:
              - RelativeFilePath: tool/tool.exe
                PortableCommandAlias: tool
            Installers:
              - InstallerType: zip
                InstallerUrl: https://example.test/tool.zip
              - InstallerType: portable
                InstallerUrl: https://example.test/standalone.exe
                Commands: [standalone]
            """);
        var zip = manifest.Installers[0];
        Assert.Equal("portable", zip.NestedInstallerType);
        Assert.Single(zip.NestedInstallerFiles);
        var portable = manifest.Installers[1];
        Assert.Null(portable.NestedInstallerType);
        Assert.Empty(portable.NestedInstallerFiles);
    }

    // W18-5: the legacy (unframed) MSZYML path is bounded at 64 MiB like the framed path and the Rust reader.
    [Fact]
    public void LegacyMszyml_RejectsPayloadAboveDecodedSizeLimit()
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var i = 0; i < 64; i++)
                deflate.Write(chunk);
            deflate.WriteByte((byte)'a');
        }

        var payload = new byte[] { (byte)'C', (byte)'K' }.Concat(compressed.ToArray()).ToArray();
        Assert.Throws<InvalidDataException>(() => PreIndexedSource.DecompressMszyml(payload));

        var small = new MemoryStream();
        using (var deflate = new DeflateStream(small, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write("Versions: []\n"u8);
        Assert.Equal("Versions: []\n", PreIndexedSource.DecompressMszyml([(byte)'C', (byte)'K', .. small.ToArray()]));
    }

    // W18-6: an empty Description falls back to ShortDescription, as WinGet `show` does.
    [Fact]
    public void EmptyDescription_FallsBackToShortDescription()
    {
        var manifest = Parse("""
            ShortDescription: A short summary
            Description: ''
            Installers:
              - InstallerUrl: https://example.test/a.exe
            """);
        Assert.Equal("A short summary", manifest.Description);

        const string payload = """
            { "Data": { "PackageIdentifier": "Test.Rest", "Versions": [ {
              "PackageVersion": "1.0.0",
              "DefaultLocale": { "PackageLocale": "en-US", "PackageName": "Test Rest",
                                 "ShortDescription": "A short summary", "Description": "" },
              "Installers": [ { "InstallerType": "exe", "InstallerUrl": "https://example.test/a.exe" } ] } ] } }
            """;
        using var document = JsonDocument.Parse(payload);
        var (restManifest, _) = RestSource.ParseRestManifest(document.RootElement, "Test.Rest", "1.0.0", "");
        Assert.Equal("A short summary", restManifest.Description);
    }

    // W18-7: the additional locales (`Localization`) of a merged manifest survive the structured
    // document used by `show -o json|yaml` and the `download` manifest.
    [Fact]
    public void MergedManifestStructuredDocument_KeepsLocalization()
    {
        var document = Structured("""
            PackageIdentifier: Test.Merged
            PackageVersion: 1.0.0
            PackageLocale: en-US
            PackageName: Test Merged
            Publisher: Example
            ShortDescription: English
            Localization:
              - PackageLocale: fr-FR
                ShortDescription: Francais
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
            ManifestType: merged
            ManifestVersion: 1.10.0
            """);
        Assert.Equal("singleton", document["ManifestType"]);
        var localizations = Assert.IsAssignableFrom<IList<object?>>(document["Localization"]);
        var french = Assert.IsAssignableFrom<IDictionary<string, object?>>(Assert.Single(localizations));
        Assert.Equal("fr-FR", french["PackageLocale"]);
        Assert.Equal("Francais", french["ShortDescription"]);
    }

    // W18-8: dependency ids are trimmed and de-duplicated case-insensitively per kind, keeping the highest
    // MinimumVersion, like WinGet DependencyList::Add.
    [Fact]
    public void DependencyLists_AreTrimmedAndDeduplicated()
    {
        var manifest = Parse("""
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
                Dependencies:
                  WindowsFeatures: [NetFx3, ' netfx3 ']
                  WindowsLibraries: [vcruntime, VCRuntime]
                  ExternalDependencies: [Java, java]
                  PackageDependencies:
                    - PackageIdentifier: Contoso.Runtime
                      MinimumVersion: 1.0
                    - PackageIdentifier: ' contoso.runtime '
                      MinimumVersion: 2.0
            """);
        var dependencies = manifest.Installers[0].Dependencies;
        Assert.Equal(["NetFx3"], dependencies.WindowsFeatures);
        Assert.Equal(["vcruntime"], dependencies.WindowsLibraries);
        Assert.Equal(["Java"], dependencies.ExternalDependencies);
        var package = Assert.Single(dependencies.PackageDependencies);
        Assert.Equal("Contoso.Runtime", package.PackageIdentifier);
        Assert.Equal("2.0", package.MinimumVersion);
        Assert.Equal(["Contoso.Runtime"], manifest.Installers[0].PackageDependencies);
    }

    // W18-9 (Rust parity): boolean-looking scalars keep their source text while boolean fields still read them.
    [Fact]
    public void BooleanScalars_KeepTheirSourceText()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Test.Bool
            PackageVersion: 1.0.0
            PackageName: True
            Publisher: FALSE
            Tags: [TRUE, false]
            Installers:
              - InstallerUrl: https://example.test/a.exe
                DownloadCommandProhibited: True
            ManifestType: singleton
            ManifestVersion: 1.10.0
            """));
        Assert.Equal("True", manifest.Name);
        Assert.Equal("FALSE", manifest.Publisher);
        Assert.Equal(["TRUE", "false"], manifest.Tags);
        Assert.True(manifest.Installers[0].DownloadCommandProhibited);
    }

    // W18-10 (Rust parity): an empty YAML document (a trailing `---`) does not wipe the structured document.
    [Fact]
    public void EmptyYamlDocument_DoesNotWipeStructuredDocument()
    {
        var document = Structured(Header + """
            Installers:
              - InstallerUrl: https://example.test/a.exe
            ---

            """);
        Assert.Equal("Test.Wave18", document["PackageIdentifier"]);
        Assert.Equal("Test Wave18", document["PackageName"]);
    }
}
