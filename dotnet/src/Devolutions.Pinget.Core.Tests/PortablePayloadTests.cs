using System.IO.Compression;
using System.Text;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public sealed class PortablePayloadTests
{
    [Fact]
    public void SharedDirectoryRejectsCollisionsAndRemovesOnlyTrackedFiles()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"pinget-payload-{Guid.NewGuid():N}");
        var target = Path.Combine(fixture, "shared");
        var bin = Path.Combine(target, "bin");
        var archivePath = Path.Combine(fixture, "package.zip");
        Directory.CreateDirectory(bin);
        try
        {
            var unrelated = Path.Combine(target, "keep.txt");
            var binary = Path.Combine(bin, "tool.exe");
            File.WriteAllText(unrelated, "user data");
            File.WriteAllText(binary, "existing file");
            CreateArchive(archivePath, "bin/tool.exe", "docs/manual.txt");

            var payload = InstallerDispatch.ReadPortablePayload(archivePath, isZip: true);
            Assert.Throws<IOException>(() => InstallerDispatch.PlanPortablePayload(payload, target, false, [], []));
            Assert.Equal("existing file", File.ReadAllText(binary));

            File.Delete(binary);
            var createdDirectories = InstallerDispatch.PlanPortablePayload(payload, target, false, [], []);
            ZipFile.ExtractToDirectory(archivePath, target);
            Assert.Equal("package", File.ReadAllText(binary));
            Assert.True(File.Exists(Path.Combine(target, "docs", "manual.txt")));

            InstallerDispatch.RemoveTrackedPortableFiles(target, payload.Files, createdDirectories);

            Assert.Equal("user data", File.ReadAllText(unrelated));
            Assert.False(File.Exists(binary));
            Assert.True(Directory.Exists(bin));
            Assert.False(Directory.Exists(Path.Combine(target, "docs")));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public void UpgradeCanReplaceAnOwnedFileInASharedDirectory()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"pinget-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixture);
        try
        {
            File.WriteAllText(Path.Combine(fixture, "tool.exe"), "old");
            var archive = Path.Combine(fixture, "package.zip");
            CreateArchive(archive, "tool.exe");
            var payload = InstallerDispatch.ReadPortablePayload(archive, isZip: true);

            InstallerDispatch.PlanPortablePayload(payload, fixture, false, ["tool.exe"], []);

            Assert.Equal("old", File.ReadAllText(Path.Combine(fixture, "tool.exe")));
            ZipFile.ExtractToDirectory(archive, fixture, overwriteFiles: true);
            Assert.Equal("package", File.ReadAllText(Path.Combine(fixture, "tool.exe")));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("folder/CON.txt")]
    public void InvalidArchivePathsAreRejected(string name)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"pinget-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixture);
        try
        {
            var archive = Path.Combine(fixture, "package.zip");
            CreateArchive(archive, name);
            Assert.Throws<InvalidOperationException>(() => InstallerDispatch.ReadPortablePayload(archive, isZip: true));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public void CaseInsensitiveDuplicateArchivePathsAreRejected()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"pinget-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixture);
        try
        {
            var archive = Path.Combine(fixture, "package.zip");
            CreateArchive(archive, "Tool.exe", "tool.exe");
            Assert.Throws<InvalidOperationException>(() => InstallerDispatch.ReadPortablePayload(archive, isZip: true));
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static void CreateArchive(string path, params string[] names)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names)
        {
            using var stream = archive.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes("package"));
        }
    }
}
