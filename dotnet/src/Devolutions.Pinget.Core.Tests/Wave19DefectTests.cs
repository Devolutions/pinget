using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Devolutions.Pinget.Core;
using Microsoft.Win32;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the portable install and archive defect fixes (wave 19).</summary>
public class Wave19DefectTests
{
    private static string UniqueName(string label) => $"PingetWave19{label}_{Environment.ProcessId}_{Guid.NewGuid():N}";

    private static string CreateTempDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), UniqueName(label));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static Installer ZipInstaller(string nestedType, params string[] files) => new()
    {
        InstallerType = "zip",
        NestedInstallerType = nestedType,
        NestedInstallerFiles = [.. files.Select(file => new NestedInstallerFile { RelativeFilePath = file })],
    };

    private static Manifest TestManifest(string id) =>
        Repository.ParseYamlManifest(Encoding.UTF8.GetBytes($"""
            PackageIdentifier: {id}
            PackageVersion: 1.2.3
            PackageName: Contoso Tool
            Publisher: Contoso
            PublisherSupportUrl: https://contoso.example/support
            ManifestType: merged
            ManifestVersion: 1.10.0
            Installers: []
            """));

    // ── W19-1: an archive portable without NestedInstallerFiles is rejected ──

    [Fact]
    public void ArchivePortableWithoutNestedFiles_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            InstallerDispatch.DeterminePortableLinks(ZipInstaller("portable"), isArchive: true, null, null));
        Assert.Contains("NestedInstallerFiles", error.Message);

        Assert.Single(InstallerDispatch.DeterminePortableLinks(ZipInstaller("portable", "bin/tool.exe"), isArchive: true, null, null));
    }

    // ── W19-2: a non-portable nested installer must be a single file ──

    [Fact]
    public void NestedInstallerTarget_RejectsMultipleFilesForSingleInstallerTypes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            InstallerDispatch.NestedArchiveInstallerTarget(ZipInstaller("exe", "setup.exe", "other.exe")));

        Assert.Equal(("msi", "setup.msi"), InstallerDispatch.NestedArchiveInstallerTarget(ZipInstaller("msi", "setup.msi")));
    }

    // ── W19-3: superscript COM/LPT device names are reserved ──

    [Theory]
    [InlineData("COM\u00b9.exe")]
    [InlineData("bin/lpt\u00b2")]
    [InlineData("Com\u00b3.txt")]
    [InlineData("LPT\u00b9")]
    [InlineData("COM1.exe")]
    public void SuperscriptDeviceNames_AreRejectedInPortablePaths(string path)
    {
        Assert.Throws<InvalidOperationException>(() =>
            InstallerDispatch.DeterminePortableLinks(ZipInstaller("portable", path), isArchive: true, null, null));
    }

    [Theory]
    [InlineData("COM10.exe")]
    [InlineData("COM0")]
    [InlineData("computer.exe")]
    [InlineData("com\u00b9x.exe")]
    public void OrdinaryNames_AreNotReserved(string path)
    {
        Assert.Single(InstallerDispatch.DeterminePortableLinks(ZipInstaller("portable", path), isArchive: true, null, null));
    }

    // ── W19-8: a local manifest portable is attributed to *DefaultSource ──

    [Fact]
    public void LocalManifestPortable_UsesDefaultSourceIdentifier()
    {
        var request = new InstallRequest { Query = new PackageQuery(), ManifestPath = @"C:\manifests\contoso.tool.yaml" };
        var source = InstallerDispatch.PortableInstallSourceIdentifier(request);
        Assert.Equal("*DefaultSource", source);
        // MakeSuitablePathPart turns the '*' into '_', as WinGet's product code does.
        Assert.Equal("Contoso.Tool__DefaultSource", InstallerDispatch.PortableSubkeyName("Contoso.Tool", source));

        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallerDispatch.PortableInstallSourceIdentifier(new InstallRequest { Query = new PackageQuery { Source = "winget" } }));
    }

    // ── W19-6: removing an install directory from PATH ──

    [Fact]
    public void RemovePortablePathEntry_DropsOnlyTheMatchingDirectory()
    {
        const string dir = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Contoso.Tool_src";
        var existing = $@"C:\Windows;{dir}\;%LOCALAPPDATA%\Other;{dir.ToUpperInvariant()}";
        Assert.Equal(@"C:\Windows;%LOCALAPPDATA%\Other", InstallerDispatch.RemovePortablePathEntry(existing, dir, value => value));

        // A sibling with the same prefix is not the same directory.
        Assert.Null(InstallerDispatch.RemovePortablePathEntry($@"{dir}-2;C:\Windows", dir, value => value));
        // An expandable reference to the directory is recognized.
        Assert.Equal(@"C:\Windows", InstallerDispatch.RemovePortablePathEntry(@"%PKG%;C:\Windows", dir, value => value.Replace("%PKG%", dir)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PreparePortablePathRemoval_KeepsValueKindAndRawVariables()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string dir = @"C:\Pkg\Tool";
        var update = InstallerDispatch.PreparePortablePathRemoval(
            $@"%SystemRoot%;{dir};%USERPROFILE%\bin", RegistryValueKind.ExpandString, dir, value => value);
        Assert.NotNull(update);
        Assert.Equal(RegistryValueKind.ExpandString, update.Value.Kind);
        Assert.Equal(@"%SystemRoot%;%USERPROFILE%\bin", update.Value.Value);

        Assert.Null(InstallerDispatch.PreparePortablePathRemoval(null, RegistryValueKind.ExpandString, dir));
    }

    // ── Registry-backed tests (test-specific HKCU uninstall subkeys, always removed) ──

    [SupportedOSPlatform("windows")]
    private sealed class TestArpKey : IDisposable
    {
        public TestArpKey(string label)
        {
            Name = UniqueName(label);
            KeyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Name}";
        }

        public string Name { get; }
        public string KeyPath { get; }

        public RegistryKey Create() => Registry.CurrentUser.CreateSubKey(KeyPath);
        public RegistryKey? Open() => Registry.CurrentUser.OpenSubKey(KeyPath);

        public ListMatch Match(string? installLocation) => new()
        {
            Name = Name,
            Id = Name,
            LocalId = $@"ARP\User\X64\{Name}",
            InstalledVersion = "1.0.0",
            Scope = "user",
            InstallerCategory = "portable",
            InstallLocation = installLocation,
        };

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteTestArpEntry(TestArpKey key, string location, string target, Manifest manifest, Installer installer)
    {
        var symlink = Path.Combine(location, "links", "tool.exe");
        InstallerDispatch.WritePortableArpEntry(
            new InstallerDispatch.PortableArpEntry(
                SubkeyName: key.Name,
                InstallLocation: location,
                PortableTargetFullPath: target,
                PortableSymlinkFullPath: symlink,
                Symlinks: [symlink],
                InstallDirectoryCreated: true,
                InstallDirectoryAddedToPath: false,
                Files: ["tool.exe"],
                Directories: [],
                SourceIdentifier: "Microsoft.Winget.Source_8wekyb3d8bbwe",
                Registry32: false,
                Manifest: manifest,
                Installer: installer),
            InstallerDispatch.PortableScope.User);
    }

    // ── W19-4: pinget writes WinGet's portable ARP value names and the target hash ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PortableArpEntry_UsesWinGetValueNamesAndTargetHash()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("ArpNames");
        using var key = new TestArpKey("ArpNames");
        try
        {
            var target = Path.Combine(temp, "tool.exe");
            File.WriteAllText(target, "portable payload");
            using (var legacy = key.Create())
                legacy.SetValue("PortableTargetFullPath", @"C:\old\tool.exe");
            WriteTestArpEntry(key, temp, target, TestManifest(key.Name), new Installer());

            using var subkey = key.Open();
            Assert.NotNull(subkey);
            Assert.Equal(target, subkey.GetValue("TargetFullPath"));
            Assert.Equal(Path.Combine(temp, "links", "tool.exe"), subkey.GetValue("SymlinkFullPath"));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("portable payload"))).ToLowerInvariant(),
                subkey.GetValue("SHA256"));
            Assert.Null(subkey.GetValue("PortableTargetFullPath"));
            Assert.Null(subkey.GetValue("PortableSymlinkFullPath"));
            Assert.Equal(0, subkey.GetValue("InstallDirectoryAddedToPath"));
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    // ── W19-7: the ARP identity comes from AppsAndFeaturesEntries, like WinGet ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PortableArpEntry_PrefersAppsAndFeaturesIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("ArpIdentity");
        using var key = new TestArpKey("ArpIdentity");
        try
        {
            var target = Path.Combine(temp, "tool.exe");
            File.WriteAllText(target, "tool");
            var manifest = TestManifest(key.Name);
            var installer = new Installer
            {
                AppsAndFeaturesEntries =
                [
                    new AppsAndFeaturesEntry { DisplayName = "Contoso Tool (x64)", Publisher = "Contoso Ltd.", DisplayVersion = "1.2.3.0" },
                ],
            };
            WriteTestArpEntry(key, temp, target, manifest, installer);
            using (var subkey = key.Open())
            {
                Assert.NotNull(subkey);
                Assert.Equal("Contoso Tool (x64)", subkey.GetValue("DisplayName"));
                Assert.Equal("1.2.3.0", subkey.GetValue("DisplayVersion"));
                Assert.Equal("Contoso Ltd.", subkey.GetValue("Publisher"));
                Assert.Equal("https://contoso.example/support", subkey.GetValue("HelpLink"));
            }

            // Without AppsAndFeaturesEntries the manifest identity is used.
            WriteTestArpEntry(key, temp, target, manifest, new Installer());
            using (var subkey = key.Open())
            {
                Assert.NotNull(subkey);
                Assert.Equal("Contoso Tool", subkey.GetValue("DisplayName"));
                Assert.Equal("1.2.3", subkey.GetValue("DisplayVersion"));
                Assert.Equal("Contoso", subkey.GetValue("Publisher"));
            }
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    // ── W19-5: pinget reads WinGet's TargetFullPath when uninstalling ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallOfWinGetInstalledPortable_RemovesItsTarget()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("WinGetTarget");
        using var key = new TestArpKey("WinGetTarget");
        try
        {
            var location = Path.Combine(temp, "Contoso.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe");
            Directory.CreateDirectory(location);
            var target = Path.Combine(location, "tool.exe");
            File.WriteAllText(target, "binary");
            using (var subkey = key.Create())
            {
                // The values WinGet itself writes for a standalone portable.
                subkey.SetValue("WinGetInstallerType", "portable");
                subkey.SetValue("InstallLocation", location);
                subkey.SetValue("TargetFullPath", target);
                subkey.SetValue("InstallDirectoryCreated", 1, RegistryValueKind.DWord);
            }

            var warnings = new List<string>();
            var exitCode = InstallerDispatch.Uninstall(key.Match(location), new UninstallRequest { Query = new PackageQuery() }, warnings);

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(target), "the WinGet-recorded binary must be removed: " + string.Join("; ", warnings));
            Assert.False(Directory.Exists(location), "the now-empty created directory must be removed");
            using var check = key.Open();
            Assert.Null(check);
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    // ── W19-10: an already-deleted install directory still uninstalls ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void UninstallWithMissingInstallLocation_UnregistersWithoutForce()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("MissingLocation");
        try
        {
            var location = Path.Combine(temp, "gone");
            using (var key = new TestArpKey("MissingLocation"))
            {
                using (var subkey = key.Create())
                {
                    subkey.SetValue("WinGetInstallerType", "portable");
                    subkey.SetValue("InstallLocation", location);
                    subkey.SetValue("InstallDirectoryCreated", 1, RegistryValueKind.DWord);
                }
                Assert.Equal(0, InstallerDispatch.Uninstall(key.Match(location), new UninstallRequest { Query = new PackageQuery() }, []));
                using var check = key.Open();
                Assert.Null(check);
            }

            // No install location at all behaves the same way.
            using (var key = new TestArpKey("NoLocation"))
            {
                using (var subkey = key.Create())
                    subkey.SetValue("WinGetInstallerType", "portable");
                Assert.Equal(0, InstallerDispatch.Uninstall(key.Match(null), new UninstallRequest { Query = new PackageQuery() }, []));
                using var check = key.Open();
                Assert.Null(check);
            }
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    // ── W19-9: a failed fresh portable install is rolled back ──

    /// <summary>
    /// A zip whose <c>tool.exe</c> extracts fine but whose <c>data.bin</c> holds an invalid deflate stream, so
    /// extraction fails after <c>tool.exe</c> has been written.
    /// </summary>
    private static void WriteCorruptZip(string path)
    {
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var tool = archive.CreateEntry("tool.exe").Open())
                tool.Write(Encoding.UTF8.GetBytes("tool binary"));
            using var data = archive.CreateEntry("data.bin").Open();
            data.Write(Encoding.UTF8.GetBytes(new string('x', 256)));
        }

        var bytes = File.ReadAllBytes(path);
        var name = Encoding.ASCII.GetBytes("data.bin");
        for (var offset = 0; offset + 46 <= bytes.Length; offset++)
        {
            if (BitConverter.ToUInt32(bytes, offset) != 0x02014b50)
                continue;
            var nameLength = BitConverter.ToUInt16(bytes, offset + 28);
            if (!bytes.AsSpan(offset + 46, nameLength).SequenceEqual(name))
                continue;
            var localHeader = (int)BitConverter.ToUInt32(bytes, offset + 42);
            var dataStart = localHeader + 30 + BitConverter.ToUInt16(bytes, localHeader + 26) + BitConverter.ToUInt16(bytes, localHeader + 28);
            // Deflate method in both headers and a first block of the reserved type 3.
            bytes[offset + 10] = 8;
            bytes[offset + 11] = 0;
            bytes[localHeader + 8] = 8;
            bytes[localHeader + 9] = 0;
            bytes[dataStart] = 0xFF;
            File.WriteAllBytes(path, bytes);
            return;
        }
        throw new InvalidOperationException("data.bin was not found in the test archive.");
    }

    [SupportedOSPlatform("windows")]
    private static void RunFailingInstall(string temp, string target)
    {
        var installerPath = Path.Combine(temp, "tool.zip");
        WriteCorruptZip(installerPath);
        var request = new InstallRequest { Query = new PackageQuery(), InstallLocation = target };
        Assert.ThrowsAny<Exception>(() => InstallerDispatch.Execute(
            installerPath, "zip", request, TestManifest(UniqueName("Rollback")), ZipInstaller("portable", "tool.exe"), []));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void FailedFreshPortableInstall_RemovesTheDirectoryItCreated()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("RollbackNew");
        try
        {
            var target = Path.Combine(temp, "install");
            RunFailingInstall(temp, target);
            Assert.False(Directory.Exists(target), "a failed fresh install must not leave a partial payload behind");
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void FailedFreshPortableInstallIntoExistingDirectory_KeepsOtherFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory("RollbackExisting");
        try
        {
            var target = Path.Combine(temp, "install");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.txt"), "user data");
            RunFailingInstall(temp, target);
            Assert.False(File.Exists(Path.Combine(target, "tool.exe")), "the partial payload must be removed");
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")), "files that were already there must survive");
        }
        finally
        {
            DeleteDirectory(temp);
        }
    }
}
