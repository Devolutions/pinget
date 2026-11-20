using System.Text;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class HomebrewManifestTests
{
    private const string ValidManifest = """
        PackageIdentifier: Pinget.Homebrew.jq-929958fef124d633
        PackageVersion: 1.8.1_1
        PackageName: jq
        ManifestType: singleton
        ManifestVersion: 1.10.0
        Installers:
          - InstallerType: homebrew-bottle
            Architecture: arm64
            Platform: [macOS]
            InstallerUrl: https://bottles.example.invalid/jq.tar.gz
            InstallerSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
        Pinget:
          Homebrew:
            SchemaVersion: 1
            Formula: jq
            Tap: homebrew/core
            UpstreamVersion: 1.8.1
            Revision: 1
            VersionScheme: 0
            Aliases: [jqr]
            OldNames: [jq-old]
            KegOnly: false
            Bottles:
              - Url: https://bottles.example.invalid/jq.tar.gz
                Sha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
                TargetOS: macOS
                Architecture: arm64
                BottleTag: arm64_sonoma
                Rebuild: 3
                Relocation: any
                MinimumOSVersion: "14.0"
        """;

    [Fact]
    public void PackageIdentifiers_AreStableAndDoNotCollapseDistinctFormulas()
    {
        var first = HomebrewPackageIds.Create("homebrew/core", "foo+bar");
        var sameIdentity = HomebrewPackageIds.Create("Homebrew/Core", "FOO+BAR");
        var differentFormula = HomebrewPackageIds.Create("homebrew/core", "foo-bar");
        var differentTap = HomebrewPackageIds.Create("other/core", "foo+bar");

        Assert.Equal(first, sameIdentity);
        Assert.Equal("Pinget.Homebrew.foo-bar-aa8e189c75775fb4", first);
        Assert.NotEqual(first, differentFormula);
        Assert.NotEqual(first, differentTap);
        Assert.StartsWith("Pinget.Homebrew.", first, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HomebrewPackageIds.Create("homebrew", "jq"));
        Assert.Throws<ArgumentException>(() => HomebrewPackageIds.Create("homebrew/core", " jq"));
        Assert.Throws<ArgumentException>(() => HomebrewPackageIds.Create("///", "jq"));
        Assert.Throws<ArgumentException>(() => HomebrewPackageIds.Create("homebrew/core", "++"));
    }

    [Fact]
    public void Parse_PreservesHomebrewFormulaAndBottleMetadata()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(ValidManifest));

        Assert.Equal("1.8.1_1", manifest.Version);
        Assert.NotNull(manifest.Homebrew);
        Assert.Equal(1, manifest.Homebrew.SchemaVersion);
        Assert.Equal("homebrew/core", manifest.Homebrew.Tap);
        Assert.Equal(1u, manifest.Homebrew.Revision);
        Assert.Equal(["jqr"], manifest.Homebrew.Aliases);
        var bottle = Assert.Single(manifest.Homebrew.Bottles);
        Assert.Equal(new string('a', 64), bottle.Sha256);
        Assert.Equal(3u, bottle.Rebuild);
        Assert.Equal("arm64_sonoma", bottle.BottleTag);
        Assert.Equal("14.0", bottle.MinimumOSVersion);
    }

    [Fact]
    public void Parse_RejectsUnknownSchemaAndPlatformMismatchedConstraints()
    {
        var unknownSchema = ValidManifest.Replace("SchemaVersion: 1", "SchemaVersion: 2", StringComparison.Ordinal);
        var schemaError = ParseExpectingInvalid(unknownSchema, "unknown extension schema must fail");
        Assert.Contains("Unsupported Pinget.Homebrew.SchemaVersion 2", schemaError.Message, StringComparison.Ordinal);

        var mismatchedTarget = ValidManifest
            .Replace("TargetOS: macOS", "TargetOS: Linux", StringComparison.Ordinal)
            .Replace("Architecture: arm64", "Architecture: x64", StringComparison.Ordinal)
            .Replace("BottleTag: arm64_sonoma", "BottleTag: x86_64_linux", StringComparison.Ordinal);
        var targetError = ParseExpectingInvalid(mismatchedTarget, "mismatched target constraints must fail");
        Assert.Contains("macOS product-version fields cannot be used for a Linux bottle target", targetError.Message, StringComparison.Ordinal);

        var unknownTag = ValidManifest.Replace("arm64_sonoma", "arm64_unknown", StringComparison.Ordinal);
        var tagError = ParseExpectingInvalid(unknownTag, "unknown target tag must fail");
        Assert.Contains("Unsupported Homebrew bottle tag 'arm64_unknown'", tagError.Message, StringComparison.Ordinal);

        var incorrectRevisionVersion = ValidManifest.Replace("1.8.1_1", "1.8.1", StringComparison.Ordinal);
        var revisionError = ParseExpectingInvalid(incorrectRevisionVersion, "revision must be in PackageVersion");
        Assert.Contains("PackageVersion must equal Homebrew stable pkg_version '1.8.1_1'", revisionError.Message, StringComparison.Ordinal);

        var mismatchedPackageId = ValidManifest.Replace(
            "Pinget.Homebrew.jq-929958fef124d633",
            "Pinget.Homebrew.jq",
            StringComparison.Ordinal);
        var packageIdError = ParseExpectingInvalid(mismatchedPackageId, "formula ID must be canonical");
        Assert.Contains("PackageIdentifier must equal Homebrew-derived identifier", packageIdError.Message, StringComparison.Ordinal);

        var mismatchedInstaller = ValidManifest.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(
            "InstallerType: homebrew-bottle\n    Architecture: arm64",
            "InstallerType: homebrew-bottle\n    Architecture: x64",
            StringComparison.Ordinal);
        var installerError = ParseExpectingInvalid(mismatchedInstaller, "installer target mismatch must fail");
        Assert.Contains("target must match its installer Platform and Architecture", installerError.Message, StringComparison.Ordinal);
    }

    private static InvalidDataException ParseExpectingInvalid(string yaml, string scenario)
    {
        var error = Record.Exception(() => Repository.ParseYamlManifest(Encoding.UTF8.GetBytes(yaml)));
        if (error is InvalidDataException invalidData)
            return invalidData;
        throw new Xunit.Sdk.XunitException($"{scenario}; got {error?.GetType().Name ?? "no exception"}.");
    }
}
