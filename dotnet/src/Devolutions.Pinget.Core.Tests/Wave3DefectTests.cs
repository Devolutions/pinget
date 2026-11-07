using Devolutions.Pinget.Cli;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class Wave3DefectTests
{
    private static ListMatch PinnedMatch(string availableVersion) => new()
    {
        Name = "Contoso Tool",
        Id = "Contoso.Tool",
        LocalId = @"ARP\User\X64\Contoso.Tool",
        InstalledVersion = "1.0.0",
        AvailableVersion = availableVersion,
        SourceName = "winget",
    };

    private static PinRecord Pin(PinType pinType, string version, string sourceId = "winget") => new()
    {
        PackageId = "Contoso.Tool",
        Version = version,
        SourceId = sourceId,
        PinType = pinType,
    };

    // I1: a Pinning pin blocks implicit upgrades regardless of its recorded version.
    [Fact]
    public void PinningPin_BlocksImplicitUpgrade_RegardlessOfVersion()
    {
        var match = PinnedMatch("2.0.0");
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Pinning, "1.0.0")]));
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Pinning, "2.0.0")]));
        Assert.True(Repository.IsUpgradeBlockedByPin(match, [Pin(PinType.Pinning, "*", "")]));
    }

    [Fact]
    public void IncludePinned_BypassesOnlyPinningPins()
    {
        Assert.False(Repository.PinBlocksUpgrade(Pin(PinType.Pinning, "1.0.0"), "2.0.0", includePinned: true));
        Assert.True(Repository.PinBlocksUpgrade(Pin(PinType.Blocking, "*"), "2.0.0", includePinned: true));
        Assert.True(Repository.PinBlocksUpgrade(Pin(PinType.Gating, "1.*"), "2.0.0", includePinned: true));
        Assert.False(Repository.PinBlocksUpgrade(Pin(PinType.Gating, "2.*"), "2.0.0", includePinned: true));
    }

    // I3: gating patterns compare version parts, mirroring WinGet's GatedVersion.
    [Theory]
    [InlineData("1.2", "1.2.*", true)]
    [InlineData("1.2.0", "1.2.*", true)]
    [InlineData("1.2.5", "1.2.*", true)]
    [InlineData("1.2.5.7", "1.2.*", true)]
    [InlineData("1", "1.0.*", true)]
    [InlineData("1.20", "1.2.*", false)]
    [InlineData("1.3.0", "1.2.*", false)]
    [InlineData("1.2.0", "1.2", true)]
    [InlineData("01.2", "1.2", true)]
    [InlineData("1.2.1", "1.2", false)]
    [InlineData("9.9", "*", true)]
    public void GatingPattern_ComparesVersionParts(string version, string pattern, bool expected)
    {
        Assert.Equal(expected, Repository.VersionMatchesPinPattern(version, pattern));
    }

    [Fact]
    public void GatingPin_BlocksImplicitUpgradeOutsideRange()
    {
        var pins = new[] { Pin(PinType.Gating, "1.2.*") };
        Assert.True(Repository.IsUpgradeBlockedByPin(PinnedMatch("1.20.0"), pins));
        Assert.False(Repository.IsUpgradeBlockedByPin(PinnedMatch("1.2.7"), pins));
    }

    // I1/I2: pin add kinds and stored versions (parity with the Rust CLI helpers).
    [Fact]
    public void PinAdd_WithoutVersionIsPinning_WithVersionIsGating()
    {
        Assert.Equal(PinType.Pinning, PinPolicy.PinTypeForAdd(false, null));
        Assert.Equal(PinType.Pinning, PinPolicy.PinTypeForAdd(false, "  "));
        Assert.Equal(PinType.Gating, PinPolicy.PinTypeForAdd(false, "1.2.*"));
        Assert.Equal(PinType.Blocking, PinPolicy.PinTypeForAdd(true, "1.2.*"));
        Assert.Equal(PinType.Blocking, PinPolicy.PinTypeForAdd(true, null));
    }

    [Fact]
    public void PinAdd_RecordsInstalledVersionByDefault()
    {
        Assert.Equal("1.4.0", PinPolicy.PinVersionForAdd(null, false, "1.4.0"));
        Assert.Equal("*", PinPolicy.PinVersionForAdd(null, false, "Unknown"));
        Assert.Equal("*", PinPolicy.PinVersionForAdd(null, false, null));
        Assert.Equal("*", PinPolicy.PinVersionForAdd(null, true, "1.4.0"));
        Assert.Equal("1.2.*", PinPolicy.PinVersionForAdd("1.2.*", false, "1.4.0"));
    }

    [Fact]
    public void ExplicitUpgrade_HonorsBlockingAndGatingPins()
    {
        Assert.Null(PinPolicy.UpgradeBlockMessage(Pin(PinType.Pinning, "1.0.0"), "2.0.0", includePinned: true));
        Assert.NotNull(PinPolicy.UpgradeBlockMessage(Pin(PinType.Pinning, "1.0.0"), "2.0.0", includePinned: false));
        Assert.NotNull(PinPolicy.UpgradeBlockMessage(Pin(PinType.Blocking, "*"), "2.0.0", includePinned: true));
        Assert.Null(PinPolicy.UpgradeBlockMessage(Pin(PinType.Gating, "1.2.*"), "1.2.9", includePinned: true));
        var message = PinPolicy.UpgradeBlockMessage(Pin(PinType.Gating, "1.2.*"), "1.3.0", includePinned: true);
        Assert.NotNull(message);
        Assert.Contains("1.3.0", message);
        Assert.Contains("1.2.*", message);
    }

    // I8 parity: source-less pins and case-insensitive source names apply.
    [Fact]
    public void ApplicablePin_MatchesSourceCaseInsensitivelyAndSourceless()
    {
        var match = PinnedMatch("2.0.0");
        Assert.NotNull(Repository.FindApplicablePin(match, [Pin(PinType.Blocking, "*", "WinGet")]));
        Assert.NotNull(Repository.FindApplicablePin(match, [Pin(PinType.Blocking, "*", "")]));
        Assert.Null(Repository.FindApplicablePin(match, [Pin(PinType.Blocking, "*", "msstore")]));
    }

    // I4: settings JSON with BOM, comments and trailing commas (WinGet's default settings.json shape).
    [Fact]
    public void Settings_AcceptCommentsTrailingCommasAndBom()
    {
        const string text = "﻿{\n    \"$schema\": \"https://aka.ms/winget-settings.schema.json\",\n\n" +
            "    // For documentation on these settings, see: https://aka.ms/winget-settings\n" +
            "    // \"source\": {\n    //    \"autoUpdateIntervalInMinutes\": 5\n    // },\n" +
            "    \"visual\": { \"progressBar\": \"rainbow\", },\n    /* block\n       comment */\n" +
            "    \"url\": \"http://example.com/a//b/*c*/\",\n" +
            "    \"quote\": \"say \\\"hi\\\", // not a comment\",\n" +
            "    \"list\": [1, 2, ],\n}\n";
        var value = SettingsStoreManager.ParseJsonObject(text);
        Assert.Equal("rainbow", value["visual"]!["progressBar"]!.GetValue<string>());
        Assert.Equal("http://example.com/a//b/*c*/", value["url"]!.GetValue<string>());
        Assert.Equal("say \"hi\", // not a comment", value["quote"]!.GetValue<string>());
        Assert.Equal(2, value["list"]!.AsArray().Count);
        Assert.False(value.ContainsKey("source"));
    }

    [Fact]
    public void LoadJsonObject_ReadsCommentedSettingsFileWithBom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pinget-wave3-settings-{Guid.NewGuid():N}.json");
        try
        {
            var body = System.Text.Encoding.UTF8.GetBytes("{\n  // comment\n  \"telemetry\": { \"disable\": true, },\n}\n");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. body]);
            var loaded = SettingsStoreManager.LoadJsonObject(path);
            Assert.True(loaded["telemetry"]!["disable"]!.GetValue<bool>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    // I5 parity: --source alone is not a filter, so bulk-upgrade hiding still applies.
    [Fact]
    public void UpgradeFilter_SourceOnlyQueryStillHidesRequireExplicitUpgrade()
    {
        var pkg = new InstalledPackage
        {
            Name = "Edge",
            LocalId = @"ARP\Machine\X64\Edge",
            InstalledVersion = "100.0",
            Scope = "Machine",
            InstallerCategory = "exe",
            Correlated = new SearchMatch
            {
                SourceName = "winget",
                SourceKind = SourceKind.PreIndexed,
                Id = "Microsoft.Edge",
                Name = "Microsoft Edge",
                Version = "110.0",
            },
            CorrelatedRequiresExplicitUpgrade = true,
        };

        Assert.False(Repository.InstalledPackageMatchesUpgradeFilterForTesting(
            pkg, new ListQuery { UpgradeOnly = true, Source = "winget" }));
    }

    // I13: --exact disables loose correlation; tag-only queries never use it.
    [Fact]
    public void LooseListCorrelation_RespectsExactAndNeedsTextFilter()
    {
        Assert.True(Repository.AllowLooseListCorrelation(new ListQuery { Query = "git" }));
        Assert.False(Repository.AllowLooseListCorrelation(new ListQuery { Query = "git", Exact = true }));
        Assert.False(Repository.AllowLooseListCorrelation(new ListQuery { Tag = "git" }));
    }

    // I12: MSIX family name is Name_PublisherId even when a resource id is present.
    [Fact]
    public void MsixFamilyName_ExcludesResourceId()
    {
        var parsed = InstalledPackages.ParseMsixFullName("Foo_1.0.0.0_neutral_~_8wekyb3d8bbwe");
        Assert.NotNull(parsed);
        Assert.Equal("Foo_8wekyb3d8bbwe", parsed.Value.FamilyName);
        Assert.Equal("~", parsed.Value.ResourceId);
        Assert.Equal("1.0.0.0", parsed.Value.Version);

        parsed = InstalledPackages.ParseMsixFullName("Foo.Bar_2.1.0.0_x64_en-us_abcdefghijklm");
        Assert.Equal("Foo.Bar_abcdefghijklm", parsed!.Value.FamilyName);
    }

    // I6: registry strings are trimmed and empty values treated as missing (parity with Rust read_reg_string).
    [Fact]
    public void RegistryStrings_AreTrimmedAndEmptyIsMissing()
    {
        Assert.Equal("1.2.3", InstalledPackages.NormalizeRegistryString(" 1.2.3 "));
        Assert.Null(InstalledPackages.NormalizeRegistryString("   "));
        Assert.Null(InstalledPackages.NormalizeRegistryString(""));
        Assert.Null(InstalledPackages.NormalizeRegistryString(null));
        Assert.Null(InstalledPackages.NormalizeRegistryString(1));
    }
}
