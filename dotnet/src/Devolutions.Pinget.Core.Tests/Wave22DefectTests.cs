using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the PowerShell module / COM interop defect fixes (wave 22).</summary>
public class Wave22DefectTests
{
    private static PSFoundCatalogPackage FoundGit() =>
        new("Git.Git", "Git", "winget", "git", "2.47.0", null);

    private static PSInstalledCatalogPackage InstalledGit() =>
        new("Git.Git", "Git version 2.40.0", "winget", null, "2.40.0", ["2.47.0"], "The Git Development Community", "Machine");

    // ── R22-3: a piped package is targeted by its id and source, not by its display name or moniker ──

    [Fact]
    public void PipedFoundPackage_IsTargetedByIdAndSourceOnly()
    {
        var query = PingetClient.BuildTargetQuery(FoundGit(), null, null, null, null, null, PSPackageFieldMatchOption.EqualsCaseInsensitive);

        Assert.Equal("Git.Git", query.Id);
        Assert.Equal("winget", query.Source);
        Assert.True(query.Exact);
        // The catalog name ("Git") is matched exactly against the installed ARP name by uninstall/repair,
        // and a moniker filter requires catalog correlation, so neither may come from the piped object.
        Assert.Null(query.Name);
        Assert.Null(query.Moniker);
    }

    [Fact]
    public void PipedPackage_UpdateListQueryUsesIdAndSourceOnly()
    {
        var query = PingetClient.BuildUpdateListQuery(FoundGit(), null, null, null, null, null, false, PSPackageFieldMatchOption.EqualsCaseInsensitive);

        Assert.Equal("Git.Git", query.Id);
        Assert.Equal("winget", query.Source);
        Assert.Null(query.Name);
        Assert.Null(query.Moniker);
        Assert.True(query.UpgradeOnly);
    }

    [Fact]
    public void ExplicitNameAndMonikerParameters_AreStillFilters()
    {
        var query = PingetClient.BuildTargetQuery(null, null, "Git", "git", null, null, PSPackageFieldMatchOption.EqualsCaseInsensitive);

        Assert.Equal("Git", query.Name);
        Assert.Equal("git", query.Moniker);
    }

    // ── R22-4: uninstall/repair of a piped found package do not pin the catalog's latest version ──

    [Fact]
    public void UninstallAndRepair_DoNotUseAFoundPackagesCatalogVersion()
    {
        // The query version of uninstall/repair is matched against the installed version, so the
        // catalog's latest version would reject an older installed copy.
        Assert.Null(PingetClient.GetInstalledVersion(FoundGit()));
        Assert.Equal("2.40.0", PingetClient.GetInstalledVersion(InstalledGit()));
        Assert.Null(PingetClient.GetInstalledVersion(null));
    }

    // ── R22-5: install, update and export accept package agreements like WinGet's COM options ──

    [Fact]
    public void InstallRequests_AcceptPackageAgreements()
    {
        var install = PingetClient.BuildInstallRequest(
            new PackageQuery { Id = "Contoso.Tool" },
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            PSPackageInstallMode.Default);
        Assert.True(install.AcceptPackageAgreements);

        var download = PingetClient.BuildDownloadRequest(new PackageQuery { Id = "Contoso.Tool" }, false, false);
        Assert.True(download.AcceptPackageAgreements);
    }

    // ── R22-6: Get-PingetPackage carries package family names and product codes on PowerShell 7 too ──

    [Fact]
    public void InstalledPackage_CarriesPackageFamilyNamesAndProductCodes()
    {
        var match = new ListMatch
        {
            Name = "Contoso Tool",
            Id = "Contoso.Tool",
            LocalId = "MSIX\\Contoso.Tool_1.0.0.0_x64__8wekyb3d8bbwe",
            InstalledVersion = "1.0.0",
            AvailableVersion = "2.0.0",
            SourceName = "winget",
            PackageFamilyNames = ["Contoso.Tool_8wekyb3d8bbwe"],
            ProductCodes = ["{11111111-2222-3333-4444-555555555555}"],
        };

        var package = PingetClient.ToPsInstalledPackage(match);

        Assert.Equal(["Contoso.Tool_8wekyb3d8bbwe"], package.PackageFamilyNames);
        Assert.Equal(["{11111111-2222-3333-4444-555555555555}"], package.ProductCodes);
        Assert.Equal(["Contoso.Tool_8wekyb3d8bbwe"], package.GetPackageVersionInfo("2.0.0").PackageFamilyNames);
    }

    // ── R22-9: a found package's version info carries the catalog channel ──

    [Fact]
    public void FoundPackage_VersionInfoCarriesTheChannel()
    {
        var match = new SearchMatch
        {
            SourceName = "winget",
            SourceKind = SourceKind.PreIndexed,
            Id = "Contoso.Tool.Beta",
            Name = "Contoso Tool Beta",
            Version = "2.0.0",
            Channel = "beta",
        };

        var package = PingetClient.ToPsFoundPackage(match);

        Assert.Equal("beta", package.GetPackageVersionInfo("2.0.0").Channel);
    }

    // ── R22-7: CompareToVersion uses WinGet version semantics ──

    [Theory]
    [InlineData("1.0", "1.0.0", PSCompareResult.Equal)]
    [InlineData("1.0.0.0", "1", PSCompareResult.Equal)]
    [InlineData("1.0.0-beta", "1.0.0", PSCompareResult.Lesser)]
    [InlineData("2.0.1.0.5", "2.0.1.0.10", PSCompareResult.Lesser)]
    [InlineData("1.10", "1.9", PSCompareResult.Greater)]
    [InlineData("v2.0", "2.0", PSCompareResult.Equal)]
    public void CompareToVersion_UsesWinGetVersionSemantics(string installed, string other, PSCompareResult expected)
    {
        var package = new PSInstalledCatalogPackage("Contoso.Tool", "Contoso Tool", "winget", null, installed, [], null, null);

        Assert.Equal(expected, package.CompareToVersion(other));
    }

    // ── R22-8: -MatchOption Equals is case-sensitive and StartsWithCaseInsensitive is a prefix match ──

    [Fact]
    public void MatchOption_EqualsIsCaseSensitive()
    {
        var packages = new[]
        {
            new PSFoundCatalogPackage("Contoso.Tool", "Contoso Tool", "winget", null, "1.0", null),
            new PSFoundCatalogPackage("contoso.tool", "contoso tool", "other", null, "1.0", null),
        };

        var filtered = PSEnumMapping.FilterByMatchOption(packages, PSPackageFieldMatchOption.Equals, "Contoso.Tool", null, null).ToList();

        Assert.Equal(["winget"], filtered.Select(package => package.Source).ToArray());
    }

    [Fact]
    public void MatchOption_StartsWithIsAPrefixMatch()
    {
        var packages = new[]
        {
            new PSFoundCatalogPackage("Microsoft.PowerToys", "PowerToys", "winget", null, "1.0", null),
            new PSFoundCatalogPackage("Contoso.Microsoft.Bridge", "Bridge", "winget", null, "1.0", null),
        };

        var filtered = PSEnumMapping.FilterByMatchOption(packages, PSPackageFieldMatchOption.StartsWithCaseInsensitive, "microsoft", null, null).ToList();

        Assert.Equal(["Microsoft.PowerToys"], filtered.Select(package => package.Id).ToArray());
    }

    [Theory]
    [InlineData(PSPackageFieldMatchOption.EqualsCaseInsensitive)]
    [InlineData(PSPackageFieldMatchOption.ContainsCaseInsensitive)]
    public void MatchOption_OtherOptionsAreLeftToTheCore(PSPackageFieldMatchOption matchOption)
    {
        var packages = new[]
        {
            new PSFoundCatalogPackage("Contoso.Tool", "Contoso Tool", "winget", null, "1.0", null),
            new PSFoundCatalogPackage("contoso.tool.extra", "Extra", "winget", null, "1.0", null),
        };

        Assert.Equal(2, PSEnumMapping.FilterByMatchOption(packages, matchOption, "contoso.tool", null, null).Count());
    }
}
