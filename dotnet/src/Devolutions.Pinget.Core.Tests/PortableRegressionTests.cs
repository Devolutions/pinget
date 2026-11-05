using System.Runtime.Versioning;
using Devolutions.Pinget.Core;
using Microsoft.Win32;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class PortableRegressionTests
{
    [Fact]
    public void ResolvePortableScope_ExplicitRequestOverridesInstalledAndManifestScope()
    {
        Assert.Equal(
            InstallerDispatch.PortableScope.Machine,
            InstallerDispatch.ResolvePortableScope("  MaChInE  ", "user", "user"));
        Assert.Equal(
            InstallerDispatch.PortableScope.User,
            InstallerDispatch.ResolvePortableScope("UsEr", "machine", "machine"));
    }

    [Fact]
    public void ResolvePortableScope_UsesInstalledScopeBeforeManifestOnUpgrade()
    {
        Assert.Equal(
            InstallerDispatch.PortableScope.Machine,
            InstallerDispatch.ResolvePortableScope(null, "MaChInE", "user"));
        Assert.Equal(
            InstallerDispatch.PortableScope.User,
            InstallerDispatch.ResolvePortableScope(" \t", "USER", "machine"));
    }

    [Fact]
    public void ResolvePortableScope_UsesManifestWhenNoRequestOrInstalledScope()
    {
        Assert.Equal(
            InstallerDispatch.PortableScope.Machine,
            InstallerDispatch.ResolvePortableScope(null, null, "MACHINE"));
        Assert.Equal(
            InstallerDispatch.PortableScope.User,
            InstallerDispatch.ResolvePortableScope(" ", "\t", "user"));
    }

    [Fact]
    public void ResolvePortableScope_DefaultsToUserWhenScopesAreAbsentOrBlank()
    {
        Assert.Equal(
            InstallerDispatch.PortableScope.User,
            InstallerDispatch.ResolvePortableScope(null, null, null));
        Assert.Equal(
            InstallerDispatch.PortableScope.User,
            InstallerDispatch.ResolvePortableScope("", " \t", " "));
    }

    [Fact]
    public void ResolvePortableScope_RejectsInvalidSelectedScopeAtEachPrecedence()
    {
        foreach (var (requested, installed, manifest) in new[]
        {
            ("invalid-request", (string?)null, (string?)null),
            ((string?)null, "invalid-installed", (string?)null),
            ((string?)null, (string?)null, "invalid-manifest"),
        })
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => InstallerDispatch.ResolvePortableScope(requested, installed, manifest));
            Assert.Contains("Unsupported install scope", error.Message);
        }
    }

    [Fact]
    public void ApplyInstallScope_PropagatesRequestScopeToInstallerSelection()
    {
        var request = new InstallRequest
        {
            Query = new PackageQuery { Id = "Example.Tool" },
            Scope = "MACHINE",
        };

        var effective = Repository.ApplyInstallScope(request, installedScope: "user");
        var installer = Repository.SelectInstaller(
        [
            new() { InstallerType = "portable", Scope = "user" },
            new() { InstallerType = "portable", Scope = "machine" },
        ], effective.Query);

        Assert.Equal("machine", effective.Query.InstallScope);
        Assert.Equal("Example.Tool", effective.Query.Id);
        Assert.NotNull(installer);
        Assert.Equal("machine", installer!.Scope);
    }

    [Fact]
    public void ApplyInstallScope_PropagatesBothInstalledScopesOnUpgrade()
    {
        foreach (var (installedScope, expected) in new[] { ("MACHINE", "machine"), ("USER", "user") })
        {
            var request = new InstallRequest { Query = new PackageQuery { Id = "Example.Tool" } };
            var effective = Repository.ApplyInstallScope(request, installedScope);
            var installer = Repository.SelectInstaller(
            [
                new() { InstallerType = "portable", Scope = "user" },
                new() { InstallerType = "portable", Scope = "machine" },
            ], effective.Query);

            Assert.Equal(expected, effective.Query.InstallScope);
            Assert.Equal("Example.Tool", effective.Query.Id);
            Assert.NotNull(installer);
            Assert.Equal(expected, installer!.Scope);
        }
    }

    [Fact]
    public void ApplyInstallScope_UsesQueryScopeAndRejectsConflictingRequestScope()
    {
        var request = new InstallRequest
        {
            Query = new PackageQuery { InstallScope = "uSeR" },
        };

        Assert.Equal("user", Repository.ApplyInstallScope(request, "machine").Query.InstallScope);

        var conflicting = request with { Scope = "machine" };
        var error = Assert.Throws<InvalidOperationException>(
            () => Repository.ApplyInstallScope(conflicting, "user"));
        Assert.Contains("conflicting scope", error.Message);
    }

    [Fact]
    public void ApplyInstallScope_LeavesUnspecifiedScopeAvailableForManifestFallback()
    {
        var request = new InstallRequest
        {
            Query = new PackageQuery { InstallScope = " " },
        };

        var effective = Repository.ApplyInstallScope(request, installedScope: null);
        Assert.True(string.IsNullOrWhiteSpace(effective.Query.InstallScope));
        Assert.Equal(
            InstallerDispatch.PortableScope.Machine,
            InstallerDispatch.ResolvePortableScope(effective.Query.InstallScope, null, "machine"));
    }

    [Fact]
    public void ApplyInstallScope_RejectsInvalidRequestedOrInstalledScope()
    {
        var invalidRequest = new InstallRequest
        {
            Query = new PackageQuery { InstallScope = "invalid" },
        };
        var unscopedRequest = new InstallRequest { Query = new PackageQuery() };

        Assert.Throws<InvalidOperationException>(() => Repository.ApplyInstallScope(invalidRequest, "machine"));
        Assert.Throws<InvalidOperationException>(() => Repository.ApplyInstallScope(unscopedRequest, "invalid"));
    }

    [Fact]
    public void IsOwnedPortableDirectory_RetainsOwnershipForSameNormalizedLocation()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.CreateDirectory("owned");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        var equivalent = Path.Combine(target.ToUpperInvariant(), "child", "..");

        Assert.True(InstallerDispatch.IsOwnedPortableDirectory(true, equivalent, target));
    }

    [Fact]
    public void IsOwnedPortableDirectory_DoesNotTransferOwnershipToDifferentOrUnknownLocation()
    {
        using var temp = new TemporaryDirectory();
        var original = temp.CreateDirectory("owned");
        var custom = temp.CreateDirectory("owned-extra");

        Assert.False(InstallerDispatch.IsOwnedPortableDirectory(true, original, custom));
        Assert.False(InstallerDispatch.IsOwnedPortableDirectory(true, null, custom));
        Assert.False(InstallerDispatch.IsOwnedPortableDirectory(true, " \t", custom));
    }

    [Fact]
    public void IsOwnedPortableDirectory_DoesNotClaimPreviouslyUnownedLocation()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.CreateDirectory("custom");

        Assert.False(InstallerDispatch.IsOwnedPortableDirectory(false, target, target));
    }

    [Fact]
    public void AppendPortableLinksPath_PreservesRawReferencesForUserAndMachineLinks()
    {
        const string existing = @"%SystemRoot%\System32;%USERPROFILE%\bin";
        foreach (var links in new[]
        {
            @"C:\Users\Example\AppData\Local\Microsoft\WinGet\Links",
            @"C:\Program Files\WinGet\Links",
        })
        {
            var result = InstallerDispatch.AppendPortableLinksPath(existing, links, ExpandTestVariables);

            Assert.Equal(existing + ";" + links, result);
        }
    }

    [Fact]
    public void AppendPortableLinksPath_PreservesTrailingAndEmptySegments()
    {
        const string existing = @"%SystemRoot%\System32;;  C:\Tools  ;";
        const string links = @"C:\Program Files\WinGet\Links";

        Assert.Equal(
            existing + links,
            InstallerDispatch.AppendPortableLinksPath(existing, links, ExpandTestVariables));
        Assert.Equal(
            links,
            InstallerDispatch.AppendPortableLinksPath("", links, ExpandTestVariables));
    }

    [Fact]
    public void AppendPortableLinksPath_RecognizesExpandedQuotedEntryWithoutChangingRawPath()
    {
        const string existing = "  \"%ProgramFiles%\\WinGet\\Links\\\"  ;%USERPROFILE%\\bin";
        const string links = @"c:\program files\winget\links";
        var expandedInputs = new List<string>();
        string Expand(string value)
        {
            expandedInputs.Add(value);
            return ExpandTestVariables(value);
        }

        var result = InstallerDispatch.AppendPortableLinksPath(existing, links, Expand);

        Assert.Null(result);
        Assert.Contains(@"%ProgramFiles%\WinGet\Links\", expandedInputs);
    }

    [Fact]
    public void AppendPortableLinksPath_RecognizesLiteralDuplicatesButNotPrefixCollisions()
    {
        const string links = @"C:\Program Files\WinGet\Links";

        Assert.Null(InstallerDispatch.AppendPortableLinksPath(
            @"c:\program files\WINGET\LINKS", links, ExpandTestVariables));
        Assert.Equal(
            @"C:\Program Files\WinGet\Links-Extra;" + links,
            InstallerDispatch.AppendPortableLinksPath(
                @"C:\Program Files\WinGet\Links-Extra", links, ExpandTestVariables));
    }

    [Fact]
    public void AppendPortableLinksPath_RecognizesExpandedEntryWithRepeatedTrailingSeparators()
    {
        const string existing = @"%ProgramFiles%\WinGet\Links\\";
        const string links = @"C:\Program Files\WinGet\Links";

        Assert.Null(InstallerDispatch.AppendPortableLinksPath(existing, links, ExpandTestVariables));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathUpdate_PreservesExpandableKindAndRawVariablesWhenAppending()
    {
        const string existing = @"%SystemRoot%\System32;%USERPROFILE%\bin";
        const string links = @"C:\Program Files\WinGet\Links";

        var update = InstallerDispatch.PreparePortablePathUpdate(
            existing, RegistryValueKind.ExpandString, links, ExpandTestVariables)
            ?? throw new InvalidOperationException("Expected an expandable PATH update.");

        Assert.Equal(existing + ";" + links, update.Value);
        Assert.Equal(RegistryValueKind.ExpandString, update.Kind);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathUpdate_ExpandsOnlyExpandableValuesForDuplicateDetection()
    {
        const string existing = @"%ProgramFiles%\WinGet\Links";
        const string links = @"C:\Program Files\WinGet\Links";
        var expandedInputs = new List<string>();
        string Expand(string value)
        {
            expandedInputs.Add(value);
            return ExpandTestVariables(value);
        }

        var expandable = InstallerDispatch.PreparePortablePathUpdate(
            existing, RegistryValueKind.ExpandString, links, Expand);
        var plain = InstallerDispatch.PreparePortablePathUpdate(
            existing, RegistryValueKind.String, links,
            static _ => throw new InvalidOperationException("REG_SZ must not be expanded."))
            ?? throw new InvalidOperationException("Expected a plain PATH update.");

        Assert.Null(expandable);
        Assert.Contains(existing, expandedInputs);
        Assert.Equal(existing + ";" + links, plain.Value);
        Assert.Equal(RegistryValueKind.String, plain.Kind);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathUpdate_DetectsLiteralPlainStringDuplicateWithoutExpanding()
    {
        const string links = @"C:\Program Files\WinGet\Links";

        Assert.Null(InstallerDispatch.PreparePortablePathUpdate(
            @"c:\program files\WINGET\LINKS", RegistryValueKind.String, links,
            static _ => throw new InvalidOperationException("REG_SZ must not be expanded.")));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathUpdate_CreatesMissingPathAsExpandable()
    {
        const string links = @"C:\Program Files\WinGet\Links";

        var update = InstallerDispatch.PreparePortablePathUpdate(
            null, RegistryValueKind.None, links, ExpandTestVariables)
            ?? throw new InvalidOperationException("Expected a new PATH value.");

        Assert.Equal(links, update.Value);
        Assert.Equal(RegistryValueKind.ExpandString, update.Kind);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathUpdate_RejectsUnsupportedKindsAndNonStringValues()
    {
        const string links = @"C:\Program Files\WinGet\Links";

        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.PreparePortablePathUpdate(
            "existing", RegistryValueKind.MultiString, links, ExpandTestVariables));
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.PreparePortablePathUpdate(
            42, RegistryValueKind.DWord, links, ExpandTestVariables));
    }

    [Fact]
    public void RemoveTrackedPortableFiles_RemovesOnlyTrackedFilesAndEmptyDirectories()
    {
        using var temp = new TemporaryDirectory();
        var root = temp.CreateDirectory("shared");
        var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(tools, "bin")).FullName;
        var managed = Path.Combine(bin, "managed.exe");
        var unrelated = Path.Combine(tools, "notes.txt");
        File.WriteAllText(managed, "managed payload");
        File.WriteAllText(unrelated, "user notes");

        InstallerDispatch.RemoveTrackedPortableFiles(
            root,
            [Path.Combine("tools", "bin", "managed.exe")],
            [Path.Combine("tools", "bin"), "tools"]);

        Assert.False(File.Exists(managed));
        Assert.False(Directory.Exists(bin));
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(tools));
        Assert.Equal("user notes", File.ReadAllText(unrelated));
    }

    [Fact]
    public void RemoveTrackedPortableFiles_RejectsUnsafeFilePathsBeforeDeletingAnyFile()
    {
        for (var index = 0; index < 4; index++)
        {
            using var temp = new TemporaryDirectory();
            var root = temp.CreateDirectory("shared");
            var outside = temp.CreateDirectory("outside");
            var safe = Path.Combine(root, "safe.txt");
            var outsideFile = Path.Combine(outside, "outside.txt");
            File.WriteAllText(safe, "safe content");
            File.WriteAllText(outsideFile, "outside content");
            var invalid = index switch
            {
                0 => Path.Combine("..", "outside", "outside.txt"),
                1 => outsideFile,
                2 => Path.Combine("nested", "..", "safe.txt"),
                _ => safe,
            };

            var error = Assert.Throws<InvalidOperationException>(() =>
                InstallerDispatch.RemoveTrackedPortableFiles(root, ["safe.txt", invalid], []));

            Assert.Contains("Invalid portable relative path", error.Message);
            Assert.Equal("safe content", File.ReadAllText(safe));
            Assert.Equal("outside content", File.ReadAllText(outsideFile));
        }
    }

    [Fact]
    public void RemoveTrackedPortableFiles_RejectsUnsafeDirectoryPathsBeforeDeletingAnyFile()
    {
        for (var index = 0; index < 3; index++)
        {
            using var temp = new TemporaryDirectory();
            var root = temp.CreateDirectory("shared");
            var outside = temp.CreateDirectory("outside");
            var safe = Path.Combine(root, "safe.txt");
            var outsideFile = Path.Combine(outside, "outside.txt");
            File.WriteAllText(safe, "safe content");
            File.WriteAllText(outsideFile, "outside content");
            var invalid = index switch
            {
                0 => Path.Combine("..", "outside"),
                1 => outside,
                _ => root,
            };

            var error = Assert.Throws<InvalidOperationException>(() =>
                InstallerDispatch.RemoveTrackedPortableFiles(root, ["safe.txt"], [invalid]));

            Assert.Contains("Invalid portable relative path", error.Message);
            Assert.Equal("safe content", File.ReadAllText(safe));
            Assert.Equal("outside content", File.ReadAllText(outsideFile));
        }
    }

    [Fact]
    public void RemoveOwnedPortableLink_RejectsRegularFileInLinksDirectory()
    {
        using var temp = new TemporaryDirectory();
        var links = temp.CreateDirectory("machine-links");
        var install = temp.CreateDirectory("package");
        var unrelated = Path.Combine(links, "unrelated.exe");
        File.WriteAllText(unrelated, "user file");

        var error = Assert.Throws<IOException>(() =>
            InstallerDispatch.RemoveOwnedPortableLink(unrelated, links, install));

        Assert.Contains("not a symbolic link", error.Message);
        Assert.Equal("user file", File.ReadAllText(unrelated));
    }

    [Fact]
    public void RemoveOwnedPortableLink_RejectsLinksOutsideSelectedUserOrMachineRoot()
    {
        using var temp = new TemporaryDirectory();
        var machineLinks = temp.CreateDirectory("machine-links");
        var userLinks = temp.CreateDirectory("user-links");
        var install = temp.CreateDirectory("package");
        var machineFile = Path.Combine(machineLinks, "machine.exe");
        var userFile = Path.Combine(userLinks, "user.exe");
        File.WriteAllText(machineFile, "machine file");
        File.WriteAllText(userFile, "user file");

        var machineError = Assert.Throws<InvalidOperationException>(() =>
            InstallerDispatch.RemoveOwnedPortableLink(userFile, machineLinks, install));
        var userError = Assert.Throws<InvalidOperationException>(() =>
            InstallerDispatch.RemoveOwnedPortableLink(machineFile, userLinks, install));

        Assert.Contains("outside the links directory", machineError.Message);
        Assert.Contains("outside the links directory", userError.Message);
        Assert.Equal("machine file", File.ReadAllText(machineFile));
        Assert.Equal("user file", File.ReadAllText(userFile));
    }

    [Fact]
    public void RemoveOwnedPortableLink_ReturnsFalseForMissingLink()
    {
        using var temp = new TemporaryDirectory();
        var links = temp.CreateDirectory("machine-links");
        var install = temp.CreateDirectory("package");

        Assert.False(InstallerDispatch.RemoveOwnedPortableLink(Path.Combine(links, "missing.exe"), links, install));
        Assert.Empty(Directory.EnumerateFileSystemEntries(links));
    }

    [Fact]
    public void DeterminePortableAlias_UsesNestedFileStemWithoutAliasOrCommand()
    {
        var installer = new Installer
        {
            NestedInstallerFiles =
            [
                new() { RelativeFilePath = Path.Combine("tools", "bin", "runner.exe") },
            ],
        };

        Assert.Equal("runner", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_PrefersNestedFileStemOverUnrelatedCommand()
    {
        var installer = new Installer
        {
            NestedInstallerFiles =
            [
                new() { RelativeFilePath = "tools/bin/runner.v2.exe" },
            ],
            Commands = ["other-command"],
        };

        Assert.Equal("runner.v2", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_UsesExplicitAliasBeforeFileStem()
    {
        var installer = new Installer
        {
            NestedInstallerFiles =
            [
                new() { RelativeFilePath = "tools/bin/runner.exe", PortableCommandAlias = "run-tool" },
            ],
            Commands = ["other-command"],
        };

        Assert.Equal("run-tool", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_UsesFirstTargetWhenLaterFileHasAlias()
    {
        var installer = new Installer
        {
            NestedInstallerFiles =
            [
                new() { RelativeFilePath = "tools/first.exe" },
                new() { RelativeFilePath = "tools/second.exe", PortableCommandAlias = "second-alias" },
            ],
            Commands = ["other-command"],
        };

        Assert.Equal("first", InstallerDispatch.DeterminePortableAlias(installer));
    }

    [Fact]
    public void DeterminePortableAlias_UsesCommandForStandalonePortable()
    {
        var installer = new Installer { Commands = ["standalone-tool"] };

        Assert.Equal("standalone-tool", InstallerDispatch.DeterminePortableAlias(installer));
    }

    private static string ExpandTestVariables(string value) =>
        value.Replace("%SystemRoot%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", @"C:\Users\Example", StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase);

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), $"pinget-portable-regression-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string CreateDirectory(string name) =>
            Directory.CreateDirectory(Path.Combine(_path, name)).FullName;

        public void Dispose()
        {
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }
}
