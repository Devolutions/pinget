using System.Formats.Tar;
using System.IO.Compression;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class HomebrewBottleArchiveTests
{
    [Fact]
    public void Extract_PreservesExecutableAndInternalLibraryLink()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        fixture.Write(
            new PaxTarEntry(TarEntryType.RegularFile, "sample/1.0/bin/sample")
            {
                DataStream = new MemoryStream("#!/bin/sh\nexit 0\n"u8.ToArray()),
                Mode = (UnixFileMode)0x1ED,
            },
            new PaxTarEntry(TarEntryType.SymbolicLink, "sample/1.0/bin/alias") { LinkName = "sample" });
        var files = HomebrewBottleArchive.Extract(fixture.Archive, fixture.Keg, "sample", "1.0");
        Assert.Single(files);
        Assert.True(File.GetUnixFileMode(files[0]).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal("sample", new FileInfo(Path.Combine(fixture.Keg, "bin", "alias")).LinkTarget);
    }

    [Theory]
    [InlineData("sample/1.0/../../escape", null)]
    [InlineData("sample/1.0/link", "../../escape")]
    [InlineData("different/1.0/file", null)]
    public void Extract_RejectsEscapingEntries(string name, string? target)
    {
        using var fixture = new Fixture();
        fixture.Write(target is null
            ? new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("x"u8.ToArray()) }
            : new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });
        Assert.Throws<InvalidDataException>(() => HomebrewBottleArchive.Extract(fixture.Archive, fixture.Keg, "sample", "1.0"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape")));
    }

    [Fact]
    public void Extract_RejectsEntriesUnderArchiveLinks()
    {
        using var fixture = new Fixture();
        fixture.Write(
            new PaxTarEntry(TarEntryType.Directory, "sample/1.0/target"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "sample/1.0/link") { LinkName = "target" },
            new PaxTarEntry(TarEntryType.RegularFile, "sample/1.0/link/file") { DataStream = new MemoryStream("x"u8.ToArray()) });
        Assert.Throws<InvalidDataException>(() => HomebrewBottleArchive.Extract(fixture.Archive, fixture.Keg, "sample", "1.0"));
    }

    [Fact]
    public void Relocate_TextDoesNotRelocateTheInsertedPrefixTwice()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "sample.pc");
        File.WriteAllText(path, "prefix=@@HOMEBREW_PREFIX@@\ncellar=@@HOMEBREW_CELLAR@@\nold=/old/lib\n");
        HomebrewBottleRelocation.Relocate([path], "/old/private", "/old");
        Assert.Equal("prefix=/old/private\ncellar=/old/private/Cellar\nold=/old/private/lib\n", File.ReadAllText(path));
    }

    [Fact]
    public void Relocate_KnownFixedCellarPathsIntoPrivatePrefix()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "fixed-cellar.txt");
        File.WriteAllText(path, "/opt/homebrew/Cellar/sample/1.0/bin/sample\n");
        HomebrewBottleRelocation.Relocate([path], "/private/pinget", "/opt/homebrew");
        Assert.Equal("/private/pinget/Cellar/sample/1.0/bin/sample\n", File.ReadAllText(path));
    }

    [Fact]
    public void Relocate_ReadOnlyFileRestoresModeOnSuccessAndFailure()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var path = Path.Combine(fixture.Root, "readonly.pc");
        File.WriteAllText(path, "prefix=@@HOMEBREW_PREFIX@@");
        var mode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(path, mode);
        HomebrewBottleRelocation.Relocate([path], "/private", "/old");
        Assert.Equal("prefix=/private", File.ReadAllText(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
        File.SetUnixFileMode(path, mode | UnixFileMode.UserWrite);
        File.WriteAllBytes(path, [0x7f, (byte)'E', (byte)'L', (byte)'F']);
        File.SetUnixFileMode(path, mode);
        Assert.ThrowsAny<Exception>(() => HomebrewBottleRelocation.Relocate([path], "/private", "/old"));
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Install_UpgradeCollisionPreservesPreviousKegAndReceipt()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        fixture.Write(new PaxTarEntry(TarEntryType.RegularFile, "sample/1.0/bin/sample")
        {
            DataStream = new MemoryStream("prefix=@@HOMEBREW_PREFIX@@\n"u8.ToArray()),
        });
        var warnings = new List<string>();
        var package = Package("sample", "1.0");
        HomebrewKegStore.Install(fixture.Root, fixture.Archive, package, warnings);
        HomebrewKegStore.Install(fixture.Root, fixture.Archive, package, warnings);
        var root = HomebrewKegStore.Prefix(fixture.Root);
        Assert.Equal($"prefix={root}\n", File.ReadAllText(Path.Combine(root, "Cellar", "sample", "1.0", "bin", "sample")));
        File.WriteAllText(Path.Combine(root, "bin", "unowned"), "preserve");
        fixture.Write(new PaxTarEntry(TarEntryType.RegularFile, "sample/2.0/bin/unowned")
        {
            DataStream = new MemoryStream("new"u8.ToArray()),
        });
        Assert.Throws<IOException>(() => HomebrewKegStore.Install(fixture.Root, fixture.Archive,
            Package("sample", "2.0"), warnings));
        Assert.Equal("1.0", Assert.Single(HomebrewKegStore.ReadAll(fixture.Root)).Package.PackageVersion);
        Assert.Equal("../Cellar/sample/1.0", new FileInfo(Path.Combine(root, "opt", "sample")).LinkTarget);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "bin", "unowned")));
        Assert.False(Directory.Exists(Path.Combine(root, "Cellar", "sample", "2.0")));
        HomebrewKegStore.Uninstall(fixture.Root, "Homebrew.sample", false, warnings);
        Assert.Empty(HomebrewKegStore.ReadAll(fixture.Root));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "bin", "unowned")));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Uninstall_RefusesInstalledDependentsAndModifiedCommandLinks()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new Fixture();
        var warnings = new List<string>();
        fixture.Write(new PaxTarEntry(TarEntryType.RegularFile, "dependency/1.0/lib/data")
        {
            DataStream = new MemoryStream("data"u8.ToArray()),
        });
        HomebrewKegStore.Install(fixture.Root, fixture.Archive, Package("dependency", "1.0"), warnings);
        fixture.Write(new PaxTarEntry(TarEntryType.RegularFile, "sample/1.0/bin/sample")
        {
            DataStream = new MemoryStream("data"u8.ToArray()),
        });
        HomebrewKegStore.Install(fixture.Root, fixture.Archive,
            Package("sample", "1.0") with { Dependencies = ["Homebrew.dependency"] }, warnings);
        Assert.Throws<InvalidOperationException>(() =>
            HomebrewKegStore.Uninstall(fixture.Root, "Homebrew.dependency", false, warnings));
        var command = Path.Combine(HomebrewKegStore.Prefix(fixture.Root), "bin", "sample");
        File.Delete(command);
        File.WriteAllText(command, "user-owned");
        Assert.Throws<IOException>(() => HomebrewKegStore.Uninstall(fixture.Root, "Homebrew.sample", false, warnings));
        Assert.Equal("user-owned", File.ReadAllText(command));
        Assert.Equal(2, HomebrewKegStore.ReadAll(fixture.Root).Count);
    }

    private static HomebrewKegSpec Package(string formula, string version) =>
        new("Homebrew." + formula, version, formula, formula, version, "/old", false, "test-source", "test-source", []);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"pinget-bottle-test-{Guid.NewGuid():N}");
        internal string Archive => Path.Combine(Root, "bottle.tar.gz");
        internal string Keg => Path.Combine(Root, "keg");

        internal void Write(params TarEntry[] entries)
        {
            Directory.CreateDirectory(Root);
            using var file = File.Create(Archive);
            using var gzip = new GZipStream(file, CompressionMode.Compress);
            using var writer = new TarWriter(gzip);
            foreach (var entry in entries)
                writer.WriteEntry(entry);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
