using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Cli;
using Microsoft.Data.Sqlite;
using Xunit;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>
/// Wave 8 regression tests: manifest model, applicability, dependencies and download.
/// Mirrors rust/crates/pinget-core/src/wave8_tests.rs.
/// </summary>
[Collection(RepositoryStateCollection.Name)]
public class Wave8DefectTests
{
    private const string Header = """
        PackageIdentifier: Test.Wave8
        PackageVersion: 1.0.0
        PackageName: Test Wave8
        Publisher: Example
        ManifestType: singleton
        ManifestVersion: 1.10.0

        """;

    // An unroutable URL: these tests must fail before any download is attempted.
    private const string UnreachableInstaller = """
        Installers:
          - Architecture: neutral
            InstallerType: exe
            InstallerUrl: http://127.0.0.1:9/setup.exe
            InstallerSha256: 0000000000000000000000000000000000000000000000000000000000000000
        """;

    private static Manifest Parse(string body) => Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(Header + body));

    private static Installer TestInstaller(string url, string? locale = null) => new() { Url = url, Locale = locale };

    // R8-1: download requires the package agreements to be accepted.
    [Fact]
    public void Download_RequiresPackageAgreements()
    {
        var body = "Agreements:\n  - AgreementLabel: EULA\n    Agreement: Terms\n" + UnreachableInstaller;
        var error = Assert.Throws<InvalidOperationException>(() => Download(body, acceptPackageAgreements: false));
        Assert.Contains("--accept-package-agreements", error.Message);
    }

    // R8-1: an installer marked DownloadCommandProhibited (inherited from the root) is refused.
    [Fact]
    public void Download_RefusesDownloadCommandProhibitedInstaller()
    {
        var body = "DownloadCommandProhibited: true\n" + UnreachableInstaller;
        Assert.True(Parse(body).Installers[0].DownloadCommandProhibited);

        var error = Assert.Throws<InvalidOperationException>(() => Download(body, acceptPackageAgreements: true));
        Assert.Equal(Repository.DownloadCommandProhibitedError, error.Message);
    }

    [Fact]
    public void RestManifest_ReadsInstallerApplicabilityFields()
    {
        const string payload = """
            {
              "Data": {
                "PackageIdentifier": "Test.Rest",
                "Versions": [
                  {
                    "PackageVersion": "1.0.0",
                    "DefaultLocale": { "PackageLocale": "en-US", "PackageName": "Test Rest" },
                    "UpgradeBehavior": "deny",
                    "Installers": [
                      {
                        "Architecture": "x64",
                        "InstallerType": "exe",
                        "InstallerUrl": "https://example.test/a.exe",
                        "DownloadCommandProhibited": true,
                        "Markets": { "AllowedMarkets": ["CN"] },
                        "UnsupportedOSArchitectures": ["arm64"],
                        "InstallerSwitches": { "Upgrade": "/UPDATE" },
                        "AppsAndFeaturesEntries": [{ "ProductCode": "App_is1", "UpgradeCode": "{UP}" }],
                        "Dependencies": {
                          "WindowsFeatures": ["NetFx3"],
                          "PackageDependencies": [{ "PackageIdentifier": "Dep.One", "MinimumVersion": "14.30" }]
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;
        using var document = JsonDocument.Parse(payload);
        var (manifest, _) = RestSource.ParseRestManifest(document.RootElement, "Test.Rest", "1.0.0", "");
        var installer = Assert.Single(manifest.Installers);
        Assert.True(installer.DownloadCommandProhibited);
        Assert.Equal(["CN"], installer.AllowedMarkets);
        Assert.Equal(["arm64"], installer.UnsupportedOSArchitectures);
        Assert.Equal("deny", installer.UpgradeBehavior);
        Assert.Equal("/UPDATE", installer.Switches.Upgrade);
        Assert.Equal("App_is1", installer.AppsAndFeaturesEntries[0].ProductCode);
        Assert.Equal(["NetFx3"], installer.Dependencies.WindowsFeatures);
        Assert.Equal("14.30", installer.Dependencies.PackageDependencies[0].MinimumVersion);
        Assert.Equal(["Dep.One"], installer.PackageDependencies);
    }

    // R8-2: Markets is parsed (root inherited) and filters installer selection.
    [Fact]
    public void Markets_FilterInstallerSelection()
    {
        var manifest = Parse("""
            Markets:
              AllowedMarkets: [CN]
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/cn.exe
              - InstallerType: exe
                InstallerUrl: https://example.test/global.exe
                Markets:
                  ExcludedMarkets: [FR]
            """);
        Assert.Equal(["CN"], manifest.Installers[0].AllowedMarkets);
        Assert.Empty(manifest.Installers[1].AllowedMarkets);
        Assert.Equal(["FR"], manifest.Installers[1].ExcludedMarkets);

        string? Pick(string market) => Repository.SelectInstaller(manifest.Installers, new PackageQuery(), market, [])?.Url;
        Assert.Equal("https://example.test/global.exe", Pick("US"));
        Assert.Equal("https://example.test/cn.exe", Pick("cn"));
        Assert.Null(Pick("FR"));
    }

    [Fact]
    public void AllowedMarkets_TakePrecedenceOverExcludedMarkets()
    {
        var installer = TestInstaller("https://example.test/a.exe") with { AllowedMarkets = ["US"], ExcludedMarkets = ["US"] };
        Assert.True(Repository.InstallerMatchesMarket(installer, "us"));
        Assert.False(Repository.InstallerMatchesMarket(installer, "CA"));
    }

    // R8-2: an installer that lists the system architecture as unsupported is not applicable.
    [Fact]
    public void UnsupportedOsArchitecture_FiltersInstallerSelection()
    {
        var system = Repository.CurrentArchitecture();
        var manifest = Parse($"""
            UnsupportedOSArchitectures: [{system}]
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/emulated.exe
              - InstallerType: exe
                InstallerUrl: https://example.test/native.exe
                UnsupportedOSArchitectures: [not-this-one]
            """);
        Assert.Equal([system], manifest.Installers[0].UnsupportedOSArchitectures);
        var selected = Repository.SelectInstaller(manifest.Installers, new PackageQuery(), "US", []);
        Assert.Equal("https://example.test/native.exe", selected?.Url);
    }

    // R8-3: UpgradeBehavior is parsed with root inheritance; deny blocks updates only.
    [Fact]
    public void UpgradeBehavior_IsParsedAndDenyBlocksUpdates()
    {
        var manifest = Parse("""
            UpgradeBehavior: deny
            Installers:
              - InstallerUrl: https://example.test/a.exe
              - InstallerUrl: https://example.test/b.exe
                UpgradeBehavior: uninstallPrevious
            """);
        var deny = manifest.Installers[0];
        var uninstallPrevious = manifest.Installers[1];
        Assert.Equal("deny", deny.UpgradeBehavior);
        Assert.True(Repository.UpgradeBehaviorIs(uninstallPrevious, "uninstallprevious"));
        Assert.False(Repository.UpgradeBehaviorIs(deny, "uninstallPrevious"));

        var error = Assert.Throws<InvalidOperationException>(() => Repository.EnsureUpgradeAllowed(deny, isUpdate: true));
        Assert.Contains("cannot be upgraded", error.Message);
        Repository.EnsureUpgradeAllowed(deny, isUpdate: false);
        Repository.EnsureUpgradeAllowed(uninstallPrevious, isUpdate: true);

        // Only a version change over an existing installation is an update; a repair is not.
        Assert.True(Repository.InstallIsUpdate("1.0.0", "2.0.0"));
        Assert.False(Repository.InstallIsUpdate("2.0", "2.0.0"));
    }

    // R8-5: MinimumVersion keeps its text, and installer dependencies of any kind override the root.
    [Fact]
    public void Dependencies_KeepMinimumVersionAndAnyKindOverridesRoot()
    {
        var manifest = Parse("""
            Dependencies:
              PackageDependencies:
                - PackageIdentifier: Root.Dep
                  MinimumVersion: 14.30
            Installers:
              - InstallerUrl: https://example.test/a.exe
              - InstallerUrl: https://example.test/b.exe
                Dependencies:
                  WindowsFeatures: [NetFx3]
            """);
        var inherited = Assert.Single(Repository.SelectedInstallerDependencies(manifest, manifest.Installers[0]));
        Assert.Equal(new PackageDependency { PackageIdentifier = "Root.Dep", MinimumVersion = "14.30" }, inherited);

        var featuresOnly = manifest.Installers[1];
        Assert.Equal(["NetFx3"], featuresOnly.Dependencies.WindowsFeatures);
        Assert.Empty(Repository.SelectedInstallerDependencies(manifest, featuresOnly));
        Assert.Empty(Repository.SelectedInstallerPackageDependencies(manifest, featuresOnly));
    }

    [Fact]
    public void DuplicateDependencies_KeepTheHighestMinimumVersion()
    {
        var installer = TestInstaller("https://example.test/a.exe") with
        {
            Dependencies = new InstallerDependencies
            {
                PackageDependencies =
                [
                    new PackageDependency { PackageIdentifier = "Dep", MinimumVersion = "1.2" },
                    new PackageDependency { PackageIdentifier = "DEP", MinimumVersion = "1.10" },
                ],
            },
        };
        var manifest = Parse("Installers:\n  - InstallerUrl: https://example.test/a.exe\n");
        var dependency = Assert.Single(Repository.SelectedInstallerDependencies(manifest, installer));
        Assert.Equal(new PackageDependency { PackageIdentifier = "Dep", MinimumVersion = "1.10" }, dependency);
    }

    [Fact]
    public void DependencyMinimumVersion_Checks()
    {
        Assert.True(Repository.DependencyVersionSatisfies("14.38.33130.0", "14.30"));
        Assert.True(Repository.DependencyVersionSatisfies("14.30", "14.30"));
        Assert.False(Repository.DependencyVersionSatisfies("14.29.30133.0", "14.30"));
        Assert.False(Repository.DependencyVersionSatisfies("Unknown", "1.0"));

        Repository.EnsureDependencyMinimumVersion("Dep", "2.0", "1.5");
        var error = Assert.Throws<InvalidOperationException>(() => Repository.EnsureDependencyMinimumVersion("Dep", "1.0", "1.5"));
        Assert.Contains("minimum version 1.5", error.Message);
    }

    // R8-6: the download manifest keeps every field of the selected installer entry.
    [Fact]
    public void DownloadManifest_KeepsSelectedInstallerFields()
    {
        var yaml = Header + """
            InstallerType: zip
            Installers:
              - Architecture: x64
                InstallerUrl: https://example.test/x64.zip
                InstallerSha256: AAA
                NestedInstallerType: portable
                NestedInstallerFiles:
                  - RelativeFilePath: tool.exe
                    PortableCommandAlias: tool
                InstallerSwitches:
                  Custom: /custom
                Dependencies:
                  PackageDependencies:
                    - PackageIdentifier: Dep.One
                InstallerSuccessCodes: [3010]
              - Architecture: arm64
                InstallerUrl: https://example.test/arm64.zip
                InstallerSha256: BBB
            """;
        var bytes = Encoding.UTF8.GetBytes(yaml);
        var manifest = Repository.ParseYamlManifest(bytes);
        var documents = Repository.ParseYamlManifestDocuments(bytes);
        var rendered = Repository.BuildDownloadManifestYaml(manifest, documents, manifest.Installers[0]);
        var reparsed = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(rendered));

        var installer = Assert.Single(reparsed.Installers);
        Assert.Equal("https://example.test/x64.zip", installer.Url);
        Assert.Equal("zip", installer.InstallerType);
        Assert.Equal("portable", installer.NestedInstallerType);
        Assert.Equal("tool", installer.NestedInstallerFiles[0].PortableCommandAlias);
        Assert.Equal("/custom", installer.Switches.Custom);
        Assert.Equal(["Dep.One"], installer.PackageDependencies);
        Assert.Equal([3010L], installer.InstallerSuccessCodes);
    }

    // R8-7: the Upgrade switch is parsed (root fallback) and appended after the custom switches.
    [Fact]
    public void UpgradeSwitch_IsParsedAndAppended()
    {
        var manifest = Parse("""
            InstallerSwitches:
              Upgrade: /UPDATE
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
                InstallerSwitches:
                  Silent: /S
                  Custom: /custom
            """);
        var installer = manifest.Installers[0];
        Assert.Equal("/UPDATE", installer.Switches.Upgrade);

        var request = new InstallRequest { Query = new PackageQuery(), Mode = InstallerMode.Silent };
        Assert.Equal(["/S", "/custom", "/UPDATE"], InstallerDispatch.BuildArguments("exe", request, manifest, installer, "setup.exe"));

        // The install flow clears the switch when nothing is installed yet.
        var freshInstall = installer with { Switches = installer.Switches with { Upgrade = null } };
        Assert.Equal(["/S", "/custom"], InstallerDispatch.BuildArguments("exe", request, manifest, freshInstall, "setup.exe"));
    }

    // R8-8: without --locale, the preferred languages rank installer locales like WinGet.
    [Fact]
    public void InstallerLocale_FollowsPreferredLanguages()
    {
        string[] preferred = ["en-US", "fr-FR"];
        string? Pick(List<Installer> installers, IReadOnlyList<string> preferences) =>
            Repository.SelectInstaller(installers, new PackageQuery(), "US", preferences)?.Url;

        List<Installer> germanThenEnglish = [TestInstaller("de", "de-DE"), TestInstaller("en", "en-US")];
        Assert.Equal("en", Pick(germanThenEnglish, preferred));

        List<Installer> unknownThenFrench = [TestInstaller("unknown"), TestInstaller("fr", "fr-FR")];
        // fr-FR matches the second preference, which beats an unknown locale.
        Assert.Equal("fr", Pick(unknownThenFrench, preferred));
        // With only en-US preferred, the unknown locale beats a non-matching declared one.
        Assert.Equal("unknown", Pick(unknownThenFrench, ["en-US"]));

        List<Installer> englishVariants = [TestInstaller("gb", "en-GB"), TestInstaller("us", "en-US")];
        Assert.Equal("us", Pick(englishVariants, preferred));
        List<Installer> frenchAndBritish = [TestInstaller("fr", "fr-FR"), TestInstaller("gb", "en-GB")];
        Assert.Equal("gb", Pick(frenchAndBritish, preferred));

        // No preference at all: the manifest order decides.
        Assert.Equal("de", Pick(germanThenEnglish, []));
    }

    // R8-10: show prints unlabeled documentation as just the url and lists every dependency kind.
    [Fact]
    public void ShowFormatting_MatchesRustOutput()
    {
        Assert.Equal("  https://example.test/docs", ShowFormatting.DocumentationLine(new Documentation { Url = "https://example.test/docs" }));
        Assert.Equal("  Guide: https://example.test/docs", ShowFormatting.DocumentationLine(new Documentation { Label = "Guide", Url = "https://example.test/docs" }));

        var installer = TestInstaller("https://example.test/a.exe") with
        {
            Dependencies = new InstallerDependencies
            {
                WindowsFeatures = ["NetFx3"],
                PackageDependencies =
                [
                    new PackageDependency { PackageIdentifier = "Dep.One", MinimumVersion = "14.30" },
                    new PackageDependency { PackageIdentifier = "Dep.Two" },
                ],
                ExternalDependencies = ["Java"],
            },
        };
        Assert.Equal(
            ["  Dependencies: Dep.One [>= 14.30], Dep.Two", "  Windows Features: NetFx3", "  External Dependencies: Java"],
            ShowFormatting.InstallerDependencyLines(installer));
        Assert.Equal(
            ["  Dependencies: Legacy.Dep"],
            ShowFormatting.InstallerDependencyLines(TestInstaller("x") with { PackageDependencies = ["Legacy.Dep"] }));
    }

    // R8-11: root Commands are inherited and Channel is read from YAML.
    [Fact]
    public void YamlManifest_InheritsRootCommandsAndReadsChannel()
    {
        var manifest = Parse("""
            Channel: beta
            Commands: [nuget]
            Tags: [2048, game, 1.10, true]
            Installers:
              - InstallerType: portable
                InstallerUrl: https://example.test/nuget.exe
              - InstallerType: portable
                InstallerUrl: https://example.test/other.exe
                Commands: [other]
            """);
        Assert.Equal("beta", manifest.Channel);
        Assert.Equal(["nuget"], manifest.Installers[0].Commands);
        Assert.Equal(["other"], manifest.Installers[1].Commands);
        // Parity with the Rust fix: numeric and boolean list items keep their source text.
        Assert.Equal(["2048", "game", "1.10", "true"], manifest.Tags);
    }

    [Fact]
    public void YamlHexReturnCodes_Parse()
    {
        var manifest = Parse("""
            Installers:
              - InstallerUrl: https://example.test/a.exe
                InstallerSuccessCodes: [0x10, 3010]
            """);
        Assert.Equal([16L, 3010L], manifest.Installers[0].InstallerSuccessCodes);
    }

    // R8-12: AppsAndFeaturesEntries codes (root inherited) identify the installed package.
    [Fact]
    public void AppsAndFeaturesEntries_SupplyCorrelationCodes()
    {
        var manifest = Parse("""
            AppsAndFeaturesEntries:
              - DisplayName: Git
                ProductCode: Git_is1
            Installers:
              - InstallerType: inno
                InstallerUrl: https://example.test/a.exe
              - InstallerType: msi
                InstallerUrl: https://example.test/b.msi
                ProductCode: '{PRODUCT}'
                AppsAndFeaturesEntries:
                  - UpgradeCode: '{UPGRADE}'
            """);
        Assert.Equal("Git_is1", manifest.Installers[0].AppsAndFeaturesEntries[0].ProductCode);
        var codes = Repository.ManifestCorrelationCodes.FromManifest(manifest);
        Assert.Equal(["Git_is1", "{PRODUCT}"], codes.ProductCodes);
        Assert.Equal(["{UPGRADE}"], codes.UpgradeCodes);
        Assert.True(codes.Matches(["git_is1"], []));
        Assert.True(codes.Matches([], ["{upgrade}"]));
        Assert.False(codes.Matches(["Other"], ["{OTHER}"]));
        Assert.True(Repository.ManifestCorrelationCodes.FromManifest(Parse("Installers:\n  - InstallerUrl: https://x.test/a.exe\n")).IsEmpty);
    }

    private static DownloadResult Download(string body, bool acceptPackageAgreements)
    {
        var root = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(root, "manifest.yaml");
            File.WriteAllText(manifestPath, Header + body);
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = root, SourceMode = SourceMode.Private });
            repo.SetAdminSetting("LocalManifestFiles", true);
            foreach (var source in repo.ListSources())
                repo.RemoveSource(source.Name);

            return repo.DownloadInstaller(new InstallRequest
            {
                Query = new PackageQuery(),
                ManifestPath = manifestPath,
                AcceptPackageAgreements = acceptPackageAgreements,
            }, Path.Combine(root, "out"));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-dotnet-wave8", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDir(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
