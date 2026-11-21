using System.Formats.Tar;
using System.IO.Compression;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class HomebrewKegRecoveryTests
{
    public static TheoryData<string, string> InterruptionPoints()
    {
        var data = new TheoryData<string, string>();
        foreach (var (scenario, kind, steps) in new[]
                 {
                     ("fresh-install", "install", new[] { "journal", "keg", "link", "receipt", "commit" }),
                     ("repair", "install", new[] { "journal", "backup", "keg", "link", "receipt", "commit" }),
                     ("upgrade", "install", new[] { "journal", "keg", "link", "receipt", "commit" }),
                     ("uninstall", "uninstall", new[] { "journal", "trash", "link", "receipt", "commit" }),
                 })
        {
            foreach (var step in steps)
                data.Add(scenario, $"{kind}-{step}");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(InterruptionPoints))]
    public void Interruption_RecoversExactPreviousOrCommittedState(string scenario, string step)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var expected = new Fixture();
        Setup(expected, scenario);
        Operation(expected, scenario);
        var committed = Snapshot(expected);

        using var fixture = new Fixture();
        Setup(fixture, scenario);
        var before = Snapshot(fixture);
        Interrupted("sample:" + step, () => Operation(fixture, scenario));
        var root = HomebrewKegStore.Prefix(fixture.Root);
        Assert.True(File.Exists(Path.Combine(root, "transaction.json")));

        // Listing recovers, as would the next operation from either runtime.
        HomebrewKegStore.ReadAll(fixture.Root);
        Assert.False(File.Exists(Path.Combine(root, "transaction.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".staging")));
        if (step.EndsWith("-commit", StringComparison.Ordinal))
        {
            Assert.Equal(committed, Snapshot(fixture));
        }
        else
        {
            Assert.Equal(before, Snapshot(fixture));
            Operation(fixture, scenario);
            Assert.Equal(committed, Snapshot(fixture));
        }
        Assert.Equal("user file", File.ReadAllText(Path.Combine(root, "bin", "unrelated")));
    }

    [Fact]
    public void InterruptedRecovery_ResumesAndOutsideChangesAreUnrecoverable()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        Setup(fixture, "upgrade");
        var before = Snapshot(fixture);
        Interrupted("sample:install-receipt", () => Operation(fixture, "upgrade"));
        Interrupted("sample:rollback-links", () => HomebrewKegStore.ReadAll(fixture.Root));
        var root = HomebrewKegStore.Prefix(fixture.Root);
        Assert.True(File.Exists(Path.Combine(root, "transaction.json")));
        var warnings = new List<string>();
        HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "dependency", "1.0"), Package("dependency", "1.0"), warnings);
        Assert.Equal("Rolled back an interrupted Homebrew install of sample", warnings[0]);
        Assert.Equal(before, Snapshot(fixture));

        Interrupted("sample:install-link", () => Operation(fixture, "upgrade"));
        var command = Path.Combine(root, "bin", "new");
        Assert.Equal("../opt/sample/bin/new", new FileInfo(command).LinkTarget);
        File.Delete(command);
        File.WriteAllText(command, "user replacement");
        var error = Assert.Throws<IOException>(() => HomebrewKegStore.ReadAll(fixture.Root));
        Assert.Contains("changed outside Pinget", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "transaction.json")));
        Assert.Equal("user replacement", File.ReadAllText(command));
        Assert.ThrowsAny<IOException>(() => Operation(fixture, "upgrade"));

        File.WriteAllText(Path.Combine(root, "transaction.json"), "{");
        var corrupt = Assert.Throws<InvalidDataException>(() => HomebrewKegStore.ReadAll(fixture.Root));
        Assert.Contains("Unrecoverable Homebrew transaction journal", corrupt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationLock_IsExclusiveAndALeftoverLockFileDoesNotBlock()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        Setup(fixture, "fresh-install");
        var root = HomebrewKegStore.Prefix(fixture.Root);
        using (new FileStream(Path.Combine(root, "operations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<IOException>(() => Operation(fixture, "fresh-install"));
            Assert.Contains("in progress", error.Message, StringComparison.Ordinal);
        }
        Assert.True(File.Exists(Path.Combine(root, "operations.lock")));
        Operation(fixture, "fresh-install");
    }

    [Fact]
    public void Upgrade_ReportsDependencyChanges()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        Setup(fixture, "upgrade");
        var warnings = new List<string>();
        HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "2.0", "sample"),
            Package("sample", "2.0", "Homebrew.dependency"), warnings);
        Assert.Equal(["Homebrew.sample now depends on: Homebrew.dependency"], warnings);
        warnings.Clear();
        HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "3.0", "sample"), Package("sample", "3.0"), warnings);
        Assert.Equal(["Homebrew.sample no longer depends on: Homebrew.dependency; those packages remain installed"], warnings);
    }

    private static void Setup(Fixture fixture, string scenario)
    {
        HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "dependency", "1.0"), Package("dependency", "1.0"), []);
        File.WriteAllText(Path.Combine(HomebrewKegStore.Prefix(fixture.Root), "bin", "unrelated"), "user file");
        if (scenario != "fresh-install")
            HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "1.0", "sample", "old"), Package("sample", "1.0"), []);
    }

    private static void Operation(Fixture fixture, string scenario)
    {
        switch (scenario)
        {
            case "fresh-install":
                HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "1.0", "sample"),
                    Package("sample", "1.0", "Homebrew.dependency"), []);
                break;
            case "repair":
                HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "1.0", "sample", "old"), Package("sample", "1.0"), []);
                break;
            case "upgrade":
                HomebrewKegStore.Install(fixture.Root, Bottle(fixture, "sample", "2.0", "sample", "new"),
                    Package("sample", "2.0", "Homebrew.dependency"), []);
                break;
            default:
                HomebrewKegStore.Uninstall(fixture.Root, "Homebrew.sample", false, []);
                break;
        }
    }

    private static void Interrupted(string point, Action action)
    {
        HomebrewKegStore.InterruptAt.Value = point;
        try
        {
            Assert.Throws<HomebrewInterruptedException>(action);
        }
        finally
        {
            HomebrewKegStore.InterruptAt.Value = null;
        }
    }

    private static List<string> Snapshot(Fixture fixture)
    {
        var root = HomebrewKegStore.Prefix(fixture.Root);
        var entries = new List<string>();
        Walk(root);
        return entries;

        void Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path);
                if (relative is ".staging" or "operations.lock")
                    continue;
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    entries.Add($"{relative} link:{info.LinkTarget}");
                }
                else if (Directory.Exists(path))
                {
                    entries.Add($"{relative} dir");
                    Walk(path);
                }
                else
                {
                    entries.Add($"{relative} file:{File.GetUnixFileMode(path)}:{File.ReadAllText(path)}");
                }
            }
        }
    }

    private static string Bottle(Fixture fixture, string formula, string version, params string[] commands)
    {
        var names = new[] { $"{formula}/{version}/lib/data" }
            .Concat(commands.Select(command => $"{formula}/{version}/bin/{command}"));
        fixture.Write(names.Select(name => (TarEntry)new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream("x"u8.ToArray()),
            Mode = (UnixFileMode)0x1ED,
        }).ToArray());
        return fixture.Archive;
    }

    private static HomebrewKegSpec Package(string formula, string version, params string[] dependencies) =>
        new("Homebrew." + formula, version, formula, formula, version, "/old", false, "test-source", "test-source", dependencies);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"pinget-keg-recovery-{Guid.NewGuid():N}");
        internal string Archive => Path.Combine(Root, "bottle.tar.gz");

        internal void Write(params TarEntry[] entries)
        {
            Directory.CreateDirectory(Root);
            using var file = File.Create(Archive);
            using var gzip = new GZipStream(file, CompressionMode.Compress);
            using var writer = new TarWriter(gzip);
            foreach (var entry in entries)
                writer.WriteEntry(entry);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
