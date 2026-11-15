using System.Text;
using Microsoft.Data.Sqlite;
using Pinget.Publisher;
using Xunit;

namespace Devolutions.Pinget.Publisher.Tests;

/// <summary>Wave 21 publisher regression tests, mirroring <c>wave21_tests.rs</c>.</summary>
public sealed class Wave21DefectTests
{
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Manifest(string root, string installers, string locale = "en-US") =>
        $"PackageIdentifier: Pinget.Wave21\nPackageVersion: 1.0.0\nPackageLocale: {locale}\nPackageName: Wave Twenty-One\n"
        + $"Publisher: Pinget\nLicense: MIT\nShortDescription: Wave 21\n{root}Installers:\n{installers}"
        + "ManifestType: singleton\nManifestVersion: 1.9.0\n";

    private static string Installer(string extra) =>
        "  - Architecture: x64\n    InstallerType: exe\n    InstallerUrl: https://example.invalid/a.exe\n    "
        + $"InstallerSha256: {Hash}\n{extra}";

    private static ScanResult Scan(string yaml)
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "manifest.yaml"), yaml, Utf8);
        return ManifestScanner.Scan(temp.Path);
    }

    private static IndexedPackage ScanSingle(string yaml) => Assert.Single(Scan(yaml).Packages);

    private static string MergedManifest(string yaml) => Utf8.GetString(Assert.Single(Scan(yaml).Manifests).Content);

    [Fact]
    public void AgreementFields_KeepTheirSchemaStringText()
    {
        var merged = MergedManifest(Manifest(
            "Agreements:\n  - AgreementLabel: 1.10\n    Agreement: 0x10\n", Installer(string.Empty)));
        Assert.Contains("\"Agreements\":[{\"Agreement\":\"0x10\",\"AgreementLabel\":\"1.10\"}]", merged);
    }

    [Fact]
    public void InstallerCommands_ReplaceRootCommands()
    {
        var overriding = ScanSingle(Manifest("Commands:\n  - root\n", Installer("    Commands:\n      - own\n")));
        Assert.Equal(new[] { "own" }, overriding.Commands);

        var inheriting = ScanSingle(Manifest(
            "Commands:\n  - root\n", Installer("    Commands:\n      - own\n") + Installer(string.Empty)));
        Assert.Equal(new[] { "own", "root" }, inheriting.Commands);
    }

    [Fact]
    public void ArpDisplayNames_AreAlsoIndexedWithTheirArchitecture()
    {
        var package = ScanSingle(Manifest(
            string.Empty, Installer("    AppsAndFeaturesEntries:\n      - DisplayName: Contoso App (x64)\n")));
        Assert.Contains("contosoapp", package.NormalizedNames);
        Assert.Contains("contosoapp(X64)", package.NormalizedNames);
        Assert.DoesNotContain(package.NormalizedNames, name => name.StartsWith("wavetwentyone(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Tags: foo\n")]
    [InlineData("Tags:\n  key: value\n")]
    [InlineData("Tags:\n  - [nested]\n")]
    [InlineData("Commands: \"\"\n")]
    public void StringListFields_ThatAreNotSequencesOfStrings_AreRejectedLikeRust(string root)
    {
        var error = Assert.Throws<PublisherException>(() => Scan(Manifest(root, Installer(string.Empty))));
        Assert.Contains("must be a sequence of strings", error.Message);
    }

    [Fact]
    public void NullStringListField_IsAnEmptySequenceLikeRust()
    {
        var merged = MergedManifest(Manifest("Tags:\n", Installer(string.Empty)));
        Assert.Contains("\"Tags\":[]", merged);
    }

    [Fact]
    public void CatalogTermsAndArpValues_AreTrimmedLikeWinGet()
    {
        var package = ScanSingle(Manifest(
            "Tags:\n  - ' padded '\n",
            Installer("    Commands:\n      - \"\\tcmd \"\n    AppsAndFeaturesEntries:\n      - DisplayVersion: ' 1.2 '\n        "
                + "ProductCode: ' {ABC} '\n        UpgradeCode: ' {DEF} '\n")));
        Assert.Equal(new[] { "padded" }, package.Tags);
        Assert.Equal(new[] { "cmd" }, package.Commands);
        Assert.Equal(new[] { "{abc}" }, package.ProductCodes);
        Assert.Equal(new[] { "{def}" }, package.UpgradeCodes);
        Assert.Equal(("1.2", "1.2"), (package.ArpMinimumVersion, package.ArpMaximumVersion));
    }

    [Fact]
    public void PackageRow_TrimsNameAndMonikerAndOmitsAnEmptyMoniker()
    {
        var padded = ScanSingle(Manifest("Moniker: ' spaced '\n", Installer(string.Empty))
            .Replace("PackageName: Wave Twenty-One\n", "PackageName: ' Spaced Name '\n", StringComparison.Ordinal));
        Assert.Equal(("Spaced Name", "spaced"), (padded.Name, padded.Moniker));

        var empty = ScanSingle(Manifest("Moniker: ''\n", Installer(string.Empty)));
        Assert.Null(empty.Moniker);
    }

    [Fact]
    public void PackageLocale_LongerThan35Characters_IsRejectedLikeRust()
    {
        const string longLocale = "en-US-abcdefgh-abcdefgh-abcdefgh-abcd";
        Assert.True(longLocale.Length > 35);
        var error = Assert.Throws<PublisherException>(() => Scan(Manifest(string.Empty, Installer(string.Empty), longLocale)));
        Assert.Contains("PackageLocale", error.Message);

        Assert.Single(Scan(Manifest(string.Empty, Installer(string.Empty), "en-US-abcdefgh")).Packages);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pinget-publisher-wave21-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
