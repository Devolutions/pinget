using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class PortableLinkTests
{
    [Fact]
    public void InstalledScopePrefersArpIdentity()
    {
        var machine = new ListMatch
        {
            Name = "Example",
            Id = "Example.Tool",
            LocalId = @"ARP\Machine\X64\Example.Tool",
            InstalledVersion = "1.0",
            Scope = "user",
        };
        var user = machine with { LocalId = @"ARP\User\X64\Example.Tool", Scope = "machine" };

        Assert.Equal(InstallerDispatch.PortableScope.Machine, InstallerDispatch.InstalledPortableScope(machine));
        Assert.Equal(InstallerDispatch.PortableScope.User, InstallerDispatch.InstalledPortableScope(user));
    }

    [Fact]
    public void SharedDirectoryIsNotTreatedAsALinkTarget()
    {
        var location = Path.Combine(Path.GetTempPath(), "shared");
        var entry = new InstallerDispatch.ExistingPortableEntry(
            "Example.Tool", location, 0, location, null, null, null);

        Assert.Null(InstallerDispatch.PortableLinkTargetBoundary(entry, location));
        Assert.Null(InstallerDispatch.PortableLinkTargetBoundary(
            entry with { PortableTargetFullPath = location + Path.DirectorySeparatorChar }, location));
        Assert.Null(InstallerDispatch.PortableLinkTargetBoundary(
            entry with { PortableTargetFullPath = "tool.exe" }, location));
        var target = Path.Combine(location, "tool.exe");
        Assert.Equal(target, InstallerDispatch.PortableLinkTargetBoundary(
            entry with { PortableTargetFullPath = target }, location));
        Assert.Equal(location, InstallerDispatch.PortableLinkTargetBoundary(
            entry with { InstallDirectoryCreated = 1 }, location));
    }

    [Fact]
    public void OwnershipMatchesUnicodeCaseDifferences()
    {
        var previous = Path.Combine(Path.GetTempPath(), "Ünicode");
        var current = Path.Combine(Path.GetTempPath(), "ünicode");

        Assert.True(InstallerDispatch.IsOwnedPortableDirectory(true, previous, current));
    }

    [SkippableFact]
    public void OldAliasAndScopedSweepPreserveOtherPackagesAndScopes()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"pinget-links-{Guid.NewGuid():N}");
        var shared = Directory.CreateDirectory(Path.Combine(fixture, "shared")).FullName;
        var machineLinks = Directory.CreateDirectory(Path.Combine(fixture, "machine-links")).FullName;
        var userLinks = Directory.CreateDirectory(Path.Combine(fixture, "user-links")).FullName;
        try
        {
            var firstTarget = Path.Combine(shared, "first.exe");
            var secondTarget = Path.Combine(shared, "second.exe");
            File.WriteAllText(firstTarget, "first");
            File.WriteAllText(secondTarget, "second");
            var oldAlias = Path.Combine(machineLinks, "old.exe");
            var newAlias = Path.Combine(machineLinks, "new.exe");
            var otherAlias = Path.Combine(machineLinks, "other.exe");
            var userAlias = Path.Combine(userLinks, "user.exe");
            CreateSymlinkOrSkip(oldAlias, firstTarget);
            CreateSymlinkOrSkip(newAlias, firstTarget);
            CreateSymlinkOrSkip(otherAlias, secondTarget);
            CreateSymlinkOrSkip(userAlias, firstTarget);

            Assert.True(InstallerDispatch.RemoveOwnedPortableLink(oldAlias, machineLinks, firstTarget));
            Assert.False(File.Exists(oldAlias));
            Assert.True(File.Exists(newAlias));
            Assert.Throws<IOException>(() =>
                InstallerDispatch.RemoveOwnedPortableLink(otherAlias, machineLinks, firstTarget));

            var warnings = new List<string>();
            var redirectedRoot = Path.Combine(fixture, "redirected-links");
            CreateSymlinkOrSkip(redirectedRoot, userLinks, directory: true);
            InstallerDispatch.RemovePortableLinksInDirectory(null, firstTarget, redirectedRoot, warnings);
            Assert.Single(warnings);
            Assert.True(File.Exists(userAlias));

            warnings.Clear();
            InstallerDispatch.RemovePortableLinksInDirectory(null, firstTarget, machineLinks, warnings);

            Assert.Empty(warnings);
            Assert.False(File.Exists(newAlias));
            Assert.True(File.Exists(otherAlias));
            Assert.True(File.Exists(userAlias));
            Assert.Equal("first", File.ReadAllText(firstTarget));
            Assert.Equal("second", File.ReadAllText(secondTarget));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void CreateSymlinkOrSkip(string path, string target, bool directory = false)
    {
        try
        {
            if (directory)
                Directory.CreateSymbolicLink(path, target);
            else
                File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xFFFF) == 1314)
        {
            Skip.If(true, $"Symbolic-link privileges are unavailable: {ex.Message}");
        }
    }
}
