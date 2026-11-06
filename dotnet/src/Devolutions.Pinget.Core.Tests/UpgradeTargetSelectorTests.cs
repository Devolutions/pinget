using Devolutions.Pinget.Cli;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class UpgradeTargetSelectorTests
{
    private static ListMatch Upgradeable(string id, string name) => new()
    {
        Name = name,
        Id = id,
        LocalId = $@"ARP\Machine\X64\{name}",
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        SourceName = "winget",
    };

    private static readonly ListMatch[] EdgeRows =
    [
        Upgradeable("Microsoft.Edge", "Microsoft Edge"),
        Upgradeable("Microsoft.EdgeWebView2Runtime", "Microsoft Edge WebView2 Runtime"),
    ];

    private static readonly ListMatch[] GitRows =
    [
        Upgradeable("Git.Git", "Git"),
        Upgradeable("GitHub.GitLFS", "Git LFS"),
    ];

    [Fact]
    public void ExactIdWinsOverSubstringMatches()
    {
        var selected = UpgradeTargetSelector.Select(EdgeRows, all: false, query: null, id: "Microsoft.Edge", name: null, moniker: null);

        Assert.Equal("Microsoft.Edge", Assert.Single(selected).Id);
    }

    [Fact]
    public void AmbiguousQueryIsAnErrorListingCandidates()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpgradeTargetSelector.Select(EdgeRows, all: false, query: "edge", id: null, name: null, moniker: null));

        Assert.Contains(UpgradeTargetSelector.AmbiguousMessage, ex.Message);
        Assert.Contains("Microsoft.EdgeWebView2Runtime", ex.Message);
    }

    [Fact]
    public void SingleSubstringMatchAndAllAreUpgraded()
    {
        Assert.Single(UpgradeTargetSelector.Select(GitRows[..1], all: false, query: "git", id: null, name: null, moniker: null));
        Assert.Equal(2, UpgradeTargetSelector.Select(GitRows, all: true, query: null, id: null, name: null, moniker: null).Count);
        Assert.Equal(2, UpgradeTargetSelector.Select(GitRows, all: false, query: null, id: null, name: null, moniker: null).Count);
    }

    [Fact]
    public void SelectorsMatchOnlyTheirOwnField()
    {
        // --id Git must not pick Git.Git just because its *name* is "Git".
        Assert.Throws<InvalidOperationException>(() =>
            UpgradeTargetSelector.Select(GitRows, all: false, query: null, id: "Git", name: null, moniker: null));
        // --name Git.Git must not pick Git.Git through its id.
        Assert.Throws<InvalidOperationException>(() =>
            UpgradeTargetSelector.Select(GitRows, all: false, query: null, id: null, name: "Git.Git", moniker: null));

        var byName = UpgradeTargetSelector.Select(GitRows, all: false, query: null, id: null, name: "Git", moniker: null);
        Assert.Equal("Git.Git", Assert.Single(byName).Id);

        // The query matches either field.
        Assert.Equal("Git.Git", Assert.Single(UpgradeTargetSelector.Select(GitRows, false, "Git", null, null, null)).Id);
        Assert.Equal("GitHub.GitLFS", Assert.Single(UpgradeTargetSelector.Select(GitRows, false, "GitHub.GitLFS", null, null, null)).Id);
    }

    [Fact]
    public void MonikerUpgradesSingleListedPackageAndRejectsAmbiguity()
    {
        Assert.Single(UpgradeTargetSelector.Select(GitRows[..1], all: false, query: null, id: null, name: null, moniker: "git"));
        Assert.Throws<InvalidOperationException>(() =>
            UpgradeTargetSelector.Select(GitRows, all: false, query: null, id: null, name: null, moniker: "git"));
    }

    [Fact]
    public void RowsWithoutAvailableVersionAreSkipped()
    {
        var rows = new[] { GitRows[0], GitRows[1] with { AvailableVersion = null } };

        Assert.Equal("Git.Git", Assert.Single(UpgradeTargetSelector.Select(rows, false, "git", null, null, null)).Id);
    }
}
