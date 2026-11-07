using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the portable / archive / repair / process-launch defect fixes (wave 7).</summary>
public class Wave7DefectTests
{
    private static Manifest SimpleManifest(string id = "Contoso.Tool", string version = "1.0.0") =>
        Repository.ParseYamlManifest(Encoding.UTF8.GetBytes($"""
            PackageIdentifier: {id}
            PackageVersion: {version}
            PackageName: {id}
            Publisher: Contoso
            ManifestType: merged
            ManifestVersion: 1.10.0
            Installers: []
            """));

    private static string CreateTempDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pinget-wave7-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static NestedInstallerFile Nested(string path, string? alias = null) =>
        new() { RelativeFilePath = path, PortableCommandAlias = alias };

    private static InstallerDispatch.ExistingPortableEntry OwnedEntry(string location, string[]? files, string[] directories) =>
        new(
            SubkeyName: "Contoso.Tool_Microsoft.Winget.Source_8wekyb3d8bbwe",
            InstallLocation: location,
            InstallDirectoryCreated: 1,
            PortableTargetFullPath: Path.Combine(location, "tool.exe"),
            PortableSymlinkFullPath: null,
            Files: files,
            Directories: directories);

    private static ListMatch Match(string? installerCategory, params string[] productCodes) => new()
    {
        Name = "Contoso App",
        Id = "Contoso.App",
        LocalId = @"ARP\Machine\X64\{11111111-2222-3333-4444-555555555555}",
        InstalledVersion = "1.0.0",
        InstallerCategory = installerCategory,
        ProductCodes = [.. productCodes],
    };

    // ── R7-1: portable uninstall keeps user data unless --purge ──

    [Fact]
    public void PortableUninstall_WithoutPurge_RemovesOnlyTrackedFilesAndKeepsUserData()
    {
        var root = CreateTempDirectory("uninstall-keep");
        try
        {
            var location = Path.Combine(root, "Contoso.Tool");
            WriteFile(Path.Combine(location, "tool.exe"), "binary");
            WriteFile(Path.Combine(location, "lib", "helper.dll"), "helper");
            WriteFile(Path.Combine(location, "settings.ini"), "user settings");
            WriteFile(Path.Combine(location, "data", "profile.json"), "user data");
            var entry = OwnedEntry(location, ["tool.exe", @"lib\helper.dll"], ["lib"]);

            var warnings = new List<string>();
            InstallerDispatch.RemovePortableDirectory(location, entry, purge: false, warnings);

            Assert.False(File.Exists(Path.Combine(location, "tool.exe")));
            Assert.False(Directory.Exists(Path.Combine(location, "lib")));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(location, "settings.ini")));
            Assert.True(File.Exists(Path.Combine(location, "data", "profile.json")));
            Assert.Contains(warnings, warning => warning.Contains("--purge"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUninstall_RemovesOwnedDirectoryOnceEmpty()
    {
        var root = CreateTempDirectory("uninstall-empty");
        try
        {
            var location = Path.Combine(root, "Contoso.Tool");
            WriteFile(Path.Combine(location, "tool.exe"), "binary");

            var warnings = new List<string>();
            InstallerDispatch.RemovePortableDirectory(location, OwnedEntry(location, ["tool.exe"], []), purge: false, warnings);

            Assert.False(Directory.Exists(location));
            Assert.Empty(warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUninstall_WithPurge_RemovesOwnedDirectory()
    {
        var root = CreateTempDirectory("uninstall-purge");
        try
        {
            var location = Path.Combine(root, "Contoso.Tool");
            WriteFile(Path.Combine(location, "tool.exe"), "binary");
            WriteFile(Path.Combine(location, "settings.ini"), "user settings");

            InstallerDispatch.RemovePortableDirectory(location, OwnedEntry(location, ["tool.exe"], []), purge: true, []);

            Assert.False(Directory.Exists(location));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PortableUninstall_OfUntrackedOwnedDirectory_RemovesOnlyTheTarget()
    {
        var root = CreateTempDirectory("uninstall-legacy");
        try
        {
            var location = Path.Combine(root, "Contoso.Tool");
            WriteFile(Path.Combine(location, "tool.exe"), "binary");
            WriteFile(Path.Combine(location, "settings.ini"), "user settings");

            InstallerDispatch.RemovePortableDirectory(location, OwnedEntry(location, null, []), purge: false, []);

            Assert.False(File.Exists(Path.Combine(location, "tool.exe")));
            Assert.True(File.Exists(Path.Combine(location, "settings.ini")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── R7-2: portable upgrade removes only the previous version's obsolete files ──

    [Fact]
    public void PortableUpgrade_RemovesOnlyObsoleteTrackedFiles()
    {
        var root = CreateTempDirectory("upgrade");
        try
        {
            var target = Path.Combine(root, "Contoso.Tool");
            WriteFile(Path.Combine(target, "tool.exe"), "v2");
            WriteFile(Path.Combine(target, "old", "legacy.dll"), "v1 only");
            WriteFile(Path.Combine(target, "config.json"), "user config");
            var payload = new InstallerDispatch.PortablePayload(["tool.exe"], []);

            InstallerDispatch.RemoveObsoletePortableFiles(target, payload, ["tool.exe", @"old\legacy.dll"], ["old"]);

            Assert.Equal("v2", File.ReadAllText(Path.Combine(target, "tool.exe")));
            Assert.False(Directory.Exists(Path.Combine(target, "old")));
            Assert.Equal("user config", File.ReadAllText(Path.Combine(target, "config.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── R7-3: zip with a non-portable nested installer ──

    [Fact]
    public void NestedArchiveInstallerTarget_ResolvesTypeAndPath()
    {
        var installer = new Installer
        {
            InstallerType = "zip",
            NestedInstallerType = "MSI",
            NestedInstallerFiles = [Nested("bin/setup.msi")],
        };
        var (nestedType, relativePath) = InstallerDispatch.NestedArchiveInstallerTarget(installer);
        Assert.Equal("msi", nestedType);
        Assert.Equal(@"bin\setup.msi", relativePath);
        Assert.Equal("msi", InstallerDispatch.EffectiveInstallerType(installer, "zip"));
    }

    [Fact]
    public void NestedArchiveInstallerTarget_FailsWithoutNestedFilesOrType()
    {
        var missingFiles = new Installer { InstallerType = "zip", NestedInstallerType = "exe" };
        var ex = Assert.Throws<InvalidOperationException>(() => InstallerDispatch.NestedArchiveInstallerTarget(missingFiles));
        Assert.Contains("NestedInstallerFiles", ex.Message);

        var missingType = new Installer { InstallerType = "zip", NestedInstallerFiles = [Nested("setup.exe")] };
        ex = Assert.Throws<InvalidOperationException>(() => InstallerDispatch.NestedArchiveInstallerTarget(missingType));
        Assert.Contains("NestedInstallerType", ex.Message);

        var escaping = new Installer { InstallerType = "zip", NestedInstallerType = "exe", NestedInstallerFiles = [Nested("../setup.exe")] };
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.NestedArchiveInstallerTarget(escaping));

        var nestedZip = new Installer { InstallerType = "zip", NestedInstallerType = "zip", NestedInstallerFiles = [Nested("inner.zip")] };
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.NestedArchiveInstallerTarget(nestedZip));
    }

    [Fact]
    public void EffectiveInstallerType_KeepsPortableZipAndOtherTypes()
    {
        Assert.Equal("zip", InstallerDispatch.EffectiveInstallerType(new Installer { NestedInstallerType = "portable" }, "zip"));
        Assert.Equal("exe", InstallerDispatch.EffectiveInstallerType(new Installer(), "exe"));
    }

    [Fact]
    public void NestedArchiveInstaller_FailsCleanlyWhenNestedFileIsMissing()
    {
        var root = CreateTempDirectory("nested-missing");
        try
        {
            var archivePath = Path.Combine(root, "package.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("readme.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("no installer here");
            }
            var installer = new Installer
            {
                InstallerType = "zip",
                NestedInstallerType = "exe",
                NestedInstallerFiles = [Nested("setup.exe")],
            };
            var extractRoot = Path.Combine(Path.GetTempPath(), "pinget-install");
            var prefix = $"extracted-{Environment.ProcessId}-";
            var before = Directory.Exists(extractRoot)
                ? Directory.GetDirectories(extractRoot, prefix + "*").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];

            var ex = Assert.Throws<InvalidOperationException>(() => InstallerDispatch.InstallNestedArchiveInstaller(
                archivePath, new InstallRequest { Query = new PackageQuery() }, SimpleManifest("Contoso.Zipped"), installer, []));

            Assert.Contains("not present", ex.Message);
            var leaked = Directory.Exists(extractRoot)
                ? Directory.GetDirectories(extractRoot, prefix + "*").Where(path => !before.Contains(path)).ToArray()
                : [];
            Assert.Empty(leaked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── R7-4: system tools are launched from System32 by absolute path ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SystemTools_ResolveToAbsoluteSystem32Paths()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")!;
        foreach (var (tool, expected) in new[]
                 {
                     (InstallerDispatch.MsiexecExe, @"System32\msiexec.exe"),
                     (InstallerDispatch.CmdExe, @"System32\cmd.exe"),
                     (InstallerDispatch.WindowsPowerShellExe, @"System32\WindowsPowerShell\v1.0\powershell.exe"),
                 })
        {
            var path = InstallerDispatch.SystemToolPath(tool);
            Assert.True(Path.IsPathFullyQualified(path), path);
            Assert.Equal(Path.Combine(systemRoot, expected), path, ignoreCase: true);
            Assert.True(File.Exists(path), path);
        }
    }

    // ── R7-6: MSI repair uses msiexec /f instead of a no-op reinstall ──

    [Fact]
    public void MsiRepair_UsesTheInstalledProductCode()
    {
        Assert.Equal("{11111111-2222-3333-4444-555555555555}",
            Repository.MsiRepairProductCode(Match("msi", "Contoso.App", "{11111111-2222-3333-4444-555555555555}")));
        Assert.Null(Repository.MsiRepairProductCode(Match("exe", "{11111111-2222-3333-4444-555555555555}")));
        Assert.Null(Repository.MsiRepairProductCode(Match("msi", "Contoso.App")));
    }

    [Fact]
    public void MsiRepairArguments_MatchWinget()
    {
        const string code = "{11111111-2222-3333-4444-555555555555}";
        Assert.Equal(["/f", code, "/quiet", "/norestart"], InstallerDispatch.BuildMsiRepairArguments(code, InstallerMode.Silent, null));
        Assert.Equal(["/f", code, "/passive", "/norestart"], InstallerDispatch.BuildMsiRepairArguments(code, InstallerMode.SilentWithProgress, null));
        Assert.Equal(["/f", code, "/log", @"C:\logs\repair.log"], InstallerDispatch.BuildMsiRepairArguments(code, InstallerMode.Interactive, @"C:\logs\repair.log"));
    }

    // ── R7-7: the install flow downloads the installer it already selected ──

    [Fact]
    public void FetchInstaller_UsesTheSuppliedInstallerWithoutResolvingAgain()
    {
        var appRoot = CreateTempDirectory("fetch");
        try
        {
            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            var request = new InstallRequest { Query = new PackageQuery { Id = "Not.In.Any.Source", Exact = true } };
            var ex = Assert.Throws<InvalidOperationException>(() => repo.FetchInstaller(
                request, SimpleManifest("Contoso.Selected", "2.0.0"), new Installer { InstallerType = "exe" }, null,
                Path.Combine(appRoot, "downloads")));
            Assert.Contains("Installer has no URL", ex.Message);
        }
        finally
        {
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    // ── R7-8: every nested portable file gets a command link ──

    [Fact]
    public void PortableLinks_CoverEveryNestedFile()
    {
        var installer = new Installer
        {
            InstallerType = "zip",
            NestedInstallerType = "portable",
            NestedInstallerFiles = [Nested("bin/foo.exe"), Nested("bin/foo-cli.exe", "fcli")],
        };
        var links = InstallerDispatch.DeterminePortableLinks(installer, isArchive: true, null, null);
        Assert.Equal(
            [
                new InstallerDispatch.PortableCommandLink("foo", @"bin\foo.exe"),
                new InstallerDispatch.PortableCommandLink("fcli", @"bin\foo-cli.exe"),
            ],
            links);

        var escaping = installer with { NestedInstallerFiles = [Nested("bin/foo.exe"), Nested("../evil.exe")] };
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.DeterminePortableLinks(escaping, isArchive: true, null, null));
    }

    // ── R7-9: standalone portable alias falls back to the file name; --rename names the alias ──

    [Fact]
    public void StandalonePortableLink_UsesRenameThenCommandThenFileName()
    {
        var noCommands = new Installer { InstallerType = "portable" };
        static string Alias(Installer installer, string? rename)
        {
            var link = Assert.Single(InstallerDispatch.DeterminePortableLinks(installer, isArchive: false, rename, "tool.exe"));
            Assert.Null(link.RelativePath);
            return link.Alias;
        }

        Assert.Equal("tool", Alias(noCommands, null));
        Assert.Equal("t2", Alias(noCommands, "t2"));
        Assert.Equal("t2", Alias(noCommands, "t2.EXE"));

        var withCommand = noCommands with { Commands = ["toolctl"] };
        Assert.Equal("toolctl", Alias(withCommand, null));
        Assert.Equal("t2", Alias(withCommand, "t2"));
    }

    [Fact]
    public void PortableInstallDownloads_KeepTheirOriginalName()
    {
        var request = new InstallRequest { Query = new PackageQuery(), Rename = "t2" };
        Assert.Null(Repository.InstallDownloadRename(request, "portable"));
        Assert.Equal("t2", Repository.InstallDownloadRename(request, "exe"));
    }

    // ── R7-12: machine-scope x86 portables use the x86 root and registry view ──

    [Fact]
    public void X86MachinePortables_UseTheX86Layout()
    {
        var x86 = new Installer { Architecture = "x86" };
        var x64 = new Installer { Architecture = "x64" };
        Assert.True(InstallerDispatch.PortableUsesX86MachineLayout(InstallerDispatch.PortableScope.Machine, x86));
        Assert.False(InstallerDispatch.PortableUsesX86MachineLayout(InstallerDispatch.PortableScope.User, x86));
        Assert.False(InstallerDispatch.PortableUsesX86MachineLayout(InstallerDispatch.PortableScope.Machine, x64));
    }

    [Fact]
    public void X86MachinePortableRoot_IsUnderProgramFilesX86()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinGet", "Packages"),
            InstallerDispatch.DefaultPortableRoot(InstallerDispatch.PortableScope.Machine, x86MachineLayout: true));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages"),
            InstallerDispatch.DefaultPortableRoot(InstallerDispatch.PortableScope.Machine, x86MachineLayout: false));
    }

    // ── R7-15: no guessed switches for generic exe installers ──

    [Theory]
    [InlineData(InstallerMode.Silent)]
    [InlineData(InstallerMode.SilentWithProgress)]
    public void GenericExeInstallers_GetNoDefaultSwitches(InstallerMode mode)
    {
        Assert.Empty(InstallerDispatch.BuildArguments("exe", mode, new Installer()));
        Assert.NotEmpty(InstallerDispatch.BuildArguments("inno", mode, new Installer()));
    }
}
