using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>
/// Wave 4 regression tests: manifests, installer selection, dependencies and versions.
/// Mirrors rust/crates/pinget-core/src/wave4_tests.rs.
/// </summary>
[Collection(RepositoryStateCollection.Name)]
public class Wave4DefectTests
{
    private const string VersionYaml = """
        PackageIdentifier: Test.Multi
        PackageVersion: 1.2.3
        DefaultLocale: en-US
        ManifestType: version
        ManifestVersion: 1.10.0
        """;

    private const string DefaultLocaleYaml = """
        PackageIdentifier: Test.Multi
        PackageVersion: 1.2.3
        PackageLocale: en-US
        Publisher: Example
        PackageName: Test Multi
        ShortDescription: English description
        ManifestType: defaultLocale
        ManifestVersion: 1.10.0
        """;

    private const string FrenchLocaleYaml = """
        PackageIdentifier: Test.Multi
        PackageVersion: 1.2.3
        PackageLocale: fr-FR
        Publisher: Exemple
        PackageName: Test Multi FR
        ShortDescription: Description francaise
        ManifestType: locale
        ManifestVersion: 1.10.0
        """;

    private const string InstallerYaml = """
        PackageIdentifier: Test.Multi
        PackageVersion: 1.2.3
        InstallerType: exe
        Installers:
          - Architecture: x64
            InstallerUrl: https://example.test/Test.Multi.exe
            InstallerSha256: ABC123
        ManifestType: installer
        ManifestVersion: 1.10.0
        """;

    private const string DependencyManifestHeader = """
        PackageIdentifier: Test.DepRoot
        PackageVersion: 1.0.0
        PackageName: Test Dep Root
        ManifestType: singleton
        ManifestVersion: 1.10.0

        """;

    // V1: `--manifest <dir>` loads every file of a multi-file manifest.
    [Fact]
    public void ManifestDirectory_LoadsAllMultiFileParts()
    {
        var root = CreateTempDir();
        try
        {
            var manifestDir = Path.Combine(root, "manifest");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "Test.Multi.installer.yaml"), InstallerYaml);
            File.WriteAllText(Path.Combine(manifestDir, "Test.Multi.locale.en-US.yaml"), DefaultLocaleYaml);
            // Sorts after en-US: before the fix its fields would win a last-wins merge.
            File.WriteAllText(Path.Combine(manifestDir, "Test.Multi.locale.fr-FR.yaml"), FrenchLocaleYaml);
            File.WriteAllText(Path.Combine(manifestDir, "Test.Multi.yaml"), VersionYaml);

            var paths = Repository.ResolveManifestPaths(manifestDir);
            Assert.Equal(4, paths.Count);
            var parts = paths.Select(File.ReadAllBytes).ToList();

            var manifest = Repository.ParseYamlManifest(parts);
            Assert.Equal("Test.Multi", manifest.Id);
            Assert.Equal("1.2.3", manifest.Version);
            // The non-default fr-FR locale must not override the default-locale root fields.
            Assert.Equal("Test Multi", manifest.Name);
            Assert.Equal("Example", manifest.Publisher);
            var installer = Assert.Single(manifest.Installers);
            Assert.Equal("exe", installer.InstallerType);
            Assert.Equal("https://example.test/Test.Multi.exe", installer.Url);

            var document = StructuredOutput.CollapseManifestDocuments(Repository.ParseYamlManifestDocuments(parts));
            Assert.Equal("singleton", document["ManifestType"]);
            Assert.Equal("Test Multi", document["PackageName"]);
            Assert.NotNull(document["Installers"]);

            // A single-file path keeps working.
            var single = Path.Combine(root, "single.yaml");
            File.WriteAllText(single, VersionYaml);
            Assert.Equal([single], Repository.ResolveManifestPaths(single));
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    // V1: a `locale` document never overrides the root, even inside one multi-document stream.
    [Fact]
    public void NonDefaultLocaleDocument_DoesNotOverrideRootFields()
    {
        var yaml = string.Join("\n---\n", VersionYaml, DefaultLocaleYaml, FrenchLocaleYaml, InstallerYaml);
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(yaml));
        Assert.Equal("Test Multi", manifest.Name);
        Assert.Equal("English description", manifest.Description);
    }

    // V6 (parity with the Rust fix): a singleton manifest's structured document keeps its content.
    [Fact]
    public void SingletonManifestStructuredDocument_KeepsContent()
    {
        var yaml = """
            PackageIdentifier: Test.Single
            PackageVersion: 1.0.0
            PackageName: Test Single
            Publisher: Example
            License: MIT
            ManifestType: singleton
            ManifestVersion: 1.10.0
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/Test.Single.exe
                InstallerSha256: ABC123
            """;
        var document = StructuredOutput.CollapseManifestDocuments(
            Repository.ParseYamlManifestDocuments(Encoding.UTF8.GetBytes(yaml)));
        Assert.Equal("singleton", document["ManifestType"]);
        Assert.Equal("Test.Single", document["PackageIdentifier"]);
        Assert.Equal("Test Single", document["PackageName"]);
        Assert.Equal("MIT", document["License"]);
        Assert.Single(Assert.IsAssignableFrom<System.Collections.IList>(document["Installers"]).Cast<object>());
    }

    // V2: installer-level Dependencies.PackageDependencies[*].PackageIdentifier is parsed.
    // V3: dependencies come from the selected installer (root fallback), deduped case-insensitively.
    [Fact]
    public void SelectedInstallerDependencies_ParseInstallerLevelAndFallBackToRoot()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Test.Deps
            PackageVersion: 1.0.0
            PackageName: Test Deps
            ManifestType: singleton
            ManifestVersion: 1.10.0
            Dependencies:
              PackageDependencies:
                - PackageIdentifier: Root.Dep
                - PackageIdentifier: root.dep
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/x64.exe
                InstallerSha256: ABC123
              - Architecture: arm64
                InstallerType: exe
                InstallerUrl: https://example.test/arm64.exe
                InstallerSha256: ABC123
                Dependencies:
                  PackageDependencies:
                    - PackageIdentifier: Arm.Dep
                    - PackageIdentifier: ARM.DEP
                    - PackageIdentifier: Other.Dep
            """));

        // Wave 8 (R8-5): an installer declaring no dependencies inherits the root ones at parse time, as in WinGet.
        Assert.Equal(["Root.Dep", "root.dep"], manifest.Installers[0].PackageDependencies);
        Assert.Equal(["Arm.Dep", "ARM.DEP", "Other.Dep"], manifest.Installers[1].PackageDependencies);
        Assert.Equal(["Root.Dep"], Repository.SelectedInstallerPackageDependencies(manifest, manifest.Installers[0]));
        Assert.Equal(["Arm.Dep", "Other.Dep"], Repository.SelectedInstallerPackageDependencies(manifest, manifest.Installers[1]));
    }

    // V3: no applicable installer fails before any dependency is looked up.
    [Fact]
    public void Install_WithoutApplicableInstaller_FailsBeforeDependencies()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ex = Assert.Throws<InvalidOperationException>(() => InstallFromManifest(DependencyManifestHeader + """
            Dependencies:
              PackageDependencies:
                - PackageIdentifier: Missing.Dependency
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/x64.exe
                InstallerSha256: ABC123
            """, new PackageQuery { InstallerArchitecture = "arm64" }, dependenciesOnly: false));
        Assert.Contains("No applicable installer found", ex.Message);
    }

    // V3: an unselected installer's dependencies are not installed.
    [Fact]
    public void Install_DependenciesOnly_UsesSelectedInstallerDependencies()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = InstallFromManifest(DependencyManifestHeader + """
            Installers:
              - Architecture: x64
                InstallerType: exe
                InstallerUrl: https://example.test/x64.exe
                InstallerSha256: ABC123
              - Architecture: arm64
                InstallerType: exe
                InstallerUrl: https://example.test/arm64.exe
                InstallerSha256: ABC123
                Dependencies:
                  PackageDependencies:
                    - PackageIdentifier: Missing.Dependency
            """, new PackageQuery { InstallerArchitecture = "x64" }, dependenciesOnly: true);
        Assert.Equal("dependencies", result.InstallerType);
        Assert.True(result.Success);
    }

    // V4: a self-dependency (the smallest cycle) is skipped instead of recursing.
    [Fact]
    public void Install_SkipsSelfDependencyCycle()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var result = InstallFromManifest(DependencyManifestHeader + """
            Dependencies:
              PackageDependencies:
                - PackageIdentifier: test.deproot
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/neutral.exe
                InstallerSha256: ABC123
            """, new PackageQuery(), dependenciesOnly: true);
        Assert.Equal("dependencies", result.InstallerType);
    }

    private static PackageQuery OsQuery(string? locale = null, string? platform = null) => new()
    {
        OsVersion = "10.0.19041",
        Locale = locale,
        Platform = platform,
    };

    private static Installer TestInstaller(string? locale = null, string? minimumOsVersion = null, List<string>? platforms = null, string url = "https://example.test/setup.exe") => new()
    {
        InstallerType = "exe",
        Url = url,
        Locale = locale,
        MinimumOsVersion = minimumOsVersion,
        Platforms = platforms ?? [],
    };

    // V8: MinimumOSVersion compares with zero-padded components.
    [Theory]
    [InlineData("10.0.19041.0", true)]
    [InlineData("10.0.19041", true)]
    [InlineData("10", true)]
    [InlineData("10.0", true)]
    [InlineData("10.0.19041.1", false)]
    [InlineData("10.0.22000.0", false)]
    [InlineData("11", false)]
    public void MinimumOsVersion_ComparesDifferingComponentCounts(string minimum, bool expected)
    {
        Assert.Equal(expected, Repository.SelectInstaller([TestInstaller(minimumOsVersion: minimum)], OsQuery()) is not null);
        Assert.Equal(0, Repository.CompareDottedVersions([10, 0], [10, 0, 0, 0]));
        Assert.True(Repository.CompareDottedVersions([10, 0, 1], [10, 0, 0, 5]) > 0);
    }

    // V9: --locale excludes installers declaring another language, keeps undeclared ones.
    [Fact]
    public void RequestedLocale_FiltersMismatchedInstallers()
    {
        var french = TestInstaller(locale: "fr-FR");
        var neutral = TestInstaller(url: "https://example.test/neutral.exe");
        var british = TestInstaller(locale: "en-GB");

        Assert.Null(Repository.SelectInstaller([french], OsQuery(locale: "en-US")));
        Assert.Equal("https://example.test/neutral.exe", Repository.SelectInstaller([french, neutral], OsQuery(locale: "en-US"))?.Url);
        Assert.Equal("en-GB", Repository.SelectInstaller([french, british], OsQuery(locale: "en-US"))?.Locale);
        // Without --locale, a declared locale is still applicable.
        Assert.NotNull(Repository.SelectInstaller([french], OsQuery()));
    }

    // V14: Windows.Universal installers apply on desktop unless a platform is requested.
    [Fact]
    public void UniversalPlatformInstaller_AppliesWithoutExplicitPlatform()
    {
        var universal = TestInstaller(platforms: ["Windows.Universal"]);
        Assert.NotNull(Repository.SelectInstaller([universal], OsQuery()));
        Assert.Null(Repository.SelectInstaller([universal], OsQuery(platform: "Windows.Desktop")));
        Assert.NotNull(Repository.SelectInstaller([universal], OsQuery(platform: "Windows.Universal")));
    }

    // V12: the upgrade compatibility check applies the full installer selection.
    [Theory]
    [InlineData("", true)]
    [InlineData("    MinimumOSVersion: 99.0.0.0\n", false)]
    [InlineData("    Platform:\n      - Windows.IoT\n", false)]
    public void ManifestCompatibility_AppliesFullInstallerSelection(string extra, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(
            "PackageIdentifier: Test.Compat\n" +
            "PackageVersion: 1.0.0\n" +
            "PackageName: Test Compat\n" +
            "ManifestType: singleton\n" +
            "ManifestVersion: 1.10.0\n" +
            "Installers:\n" +
            "  - InstallerType: exe\n" +
            "    InstallerUrl: https://example.test/setup.exe\n" +
            "    InstallerSha256: ABC123\n" +
            extra));
        Assert.Equal(expected, Repository.ManifestHasCompatibleInstallerForTesting(manifest));
    }

    // V10: a leading non-digit prefix is trimmed like WinGet's Version.
    [Fact]
    public void VersionCompare_TrimsLeadingNonDigitPrefix()
    {
        Assert.Equal(0, RestSource.CompareVersionStrings("v1.2.3", "1.2.3"));
        Assert.True(RestSource.CompareVersionStrings("v1.10", "v1.9") > 0);
        Assert.True(RestSource.CompareVersionStrings("v2.0", "1.9.9") > 0);
        Assert.True(RestSource.CompareVersionStrings("version 3", "2.5") > 0);
        Assert.Equal(["1", "2", "3"], RestSource.TokenizeVersion("v1.2.3"));
        // The first digit after the first '.' keeps the prefix, as in WinGet.
        Assert.Equal(["abc", "1"], RestSource.TokenizeVersion("abc.1"));
        // No digit at all: unchanged.
        Assert.Equal(["latest"], RestSource.TokenizeVersion("latest"));
    }

    private static InstallResult InstallFromManifest(string yaml, PackageQuery query, bool dependenciesOnly)
    {
        var root = CreateTempDir();
        try
        {
            var manifestPath = Path.Combine(root, "manifest.yaml");
            File.WriteAllText(manifestPath, yaml);
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = root, SourceMode = SourceMode.Private });
            repo.SetAdminSetting("LocalManifestFiles", true);
            // No sources: a dependency lookup fails fast instead of reaching the network.
            foreach (var source in repo.ListSources())
                repo.RemoveSource(source.Name);

            return repo.Install(new InstallRequest
            {
                Query = query,
                ManifestPath = manifestPath,
                DependenciesOnly = dependenciesOnly,
            });
        }
        finally
        {
            DeleteTempDir(root);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "pinget-dotnet-wave4", Guid.NewGuid().ToString("N"));
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
