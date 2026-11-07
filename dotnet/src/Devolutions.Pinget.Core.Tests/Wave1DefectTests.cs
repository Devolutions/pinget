using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Devolutions.Pinget.Core;
using Microsoft.Win32;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the installer-execution defect fixes (wave 1).</summary>
public class Wave1DefectTests
{
    private static Manifest SimpleManifest() => Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
        PackageIdentifier: Contoso.Tool
        PackageVersion: 1.2.3
        PackageName: Contoso Tool
        Publisher: Contoso
        ManifestType: merged
        ManifestVersion: 1.10.0
        Installers:
          - InstallerType: exe
            InstallerUrl: https://example.test/setup.exe
            InstallerSha256: ABC123
        """));

    private static ListMatch Match(string id, string localId) => new()
    {
        Name = id,
        Id = id,
        LocalId = localId,
        InstalledVersion = "1.0.0",
    };

    // ── D1: InstallerSuccessCodes / ExpectedReturnCodes / reboot codes ──

    [Fact]
    public void YamlManifest_ParsesSuccessAndExpectedReturnCodesWithRootInheritance()
    {
        var manifest = Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Contoso.Tool
            PackageVersion: 1.2.3
            PackageName: Contoso Tool
            ManifestType: merged
            ManifestVersion: 1.10.0
            InstallerSuccessCodes:
              - 1
              - -2147024891
            ExpectedReturnCodes:
              - InstallerReturnCode: 1641
                ReturnResponse: rebootRequiredToFinish
            Installers:
              - InstallerType: exe
                InstallerUrl: https://example.test/a.exe
                InstallerSha256: ABC123
              - InstallerType: exe
                InstallerUrl: https://example.test/b.exe
                InstallerSha256: DEF456
                InstallerSuccessCodes: [42]
                ExpectedReturnCodes:
                  - InstallerReturnCode: 5
                    ReturnResponse: cancelledByUser
            """));

        Assert.Equal([1L, -2147024891L], manifest.Installers[0].InstallerSuccessCodes);
        var expected = Assert.Single(manifest.Installers[0].ExpectedReturnCodes);
        Assert.Equal(1641, expected.InstallerReturnCode);
        Assert.Equal("rebootRequiredToFinish", expected.ReturnResponse);
        Assert.Equal([42L], manifest.Installers[1].InstallerSuccessCodes);
        Assert.Equal(5, Assert.Single(manifest.Installers[1].ExpectedReturnCodes).InstallerReturnCode);
    }

    [Fact]
    public void RestManifest_ParsesSuccessCodesWithRootInheritance()
    {
        using var doc = JsonDocument.Parse("""
            {
              "Data": {
                "PackageIdentifier": "Contoso.Tool",
                "Versions": [
                  {
                    "PackageVersion": "1.2.3",
                    "DefaultLocale": { "PackageLocale": "en-US", "Publisher": "Contoso", "PackageName": "Contoso Tool" },
                    "InstallerSuccessCodes": [7],
                    "Installers": [
                      { "Architecture": "x64", "InstallerType": "exe", "InstallerUrl": "https://example.test/a.exe", "InstallerSha256": "AA" },
                      {
                        "Architecture": "x86", "InstallerType": "exe", "InstallerUrl": "https://example.test/b.exe", "InstallerSha256": "BB",
                        "InstallerSuccessCodes": [8],
                        "ExpectedReturnCodes": [{ "InstallerReturnCode": 3010, "ReturnResponse": "rebootRequiredToFinish" }]
                      }
                    ]
                  }
                ]
              }
            }
            """);

        var (manifest, _) = RestSource.ParseRestManifest(doc.RootElement, "Contoso.Tool", "1.2.3", "");
        Assert.Equal([7L], manifest.Installers[0].InstallerSuccessCodes);
        Assert.Empty(manifest.Installers[0].ExpectedReturnCodes);
        Assert.Equal([8L], manifest.Installers[1].InstallerSuccessCodes);
        Assert.Equal(3010, Assert.Single(manifest.Installers[1].ExpectedReturnCodes).InstallerReturnCode);
    }

    [Fact]
    public void ClassifyExitCode_HonorsSuccessCodesAndRebootCodes()
    {
        var plain = new Installer();
        Assert.Equal((true, false), InstallerDispatch.ClassifyExitCode(plain, "exe", 0));
        Assert.Equal((false, false), InstallerDispatch.ClassifyExitCode(plain, "exe", 1));

        var withCodes = new Installer { InstallerSuccessCodes = [1, 2147942405] };
        Assert.Equal((true, false), InstallerDispatch.ClassifyExitCode(withCodes, "exe", 1));
        // Codes above int.MaxValue in the manifest match the signed HRESULT exit code.
        Assert.Equal((true, false), InstallerDispatch.ClassifyExitCode(withCodes, "exe", unchecked((int)2147942405u)));

        // 3010 is a reboot-required success for MSI-family installers only.
        foreach (var installerType in new[] { "msi", "WIX", "burn" })
            Assert.Equal((true, true), InstallerDispatch.ClassifyExitCode(plain, installerType, 3010));
        Assert.Equal((false, false), InstallerDispatch.ClassifyExitCode(plain, "exe", 3010));
        // WinGet treats 1641 (reboot initiated) as a failure unless the manifest says otherwise.
        Assert.Equal((false, false), InstallerDispatch.ClassifyExitCode(plain, "msi", 1641));

        var withExpected = new Installer
        {
            ExpectedReturnCodes =
            [
                new ExpectedReturnCode { InstallerReturnCode = 1641, ReturnResponse = "RebootRequiredToFinish" },
                new ExpectedReturnCode { InstallerReturnCode = 3010, ReturnResponse = "rebootRequiredForInstall" },
            ],
        };
        Assert.Equal((true, true), InstallerDispatch.ClassifyExitCode(withExpected, "exe", 1641));
        // A manifest entry overrides the installer type's default known code.
        Assert.Equal((false, false), InstallerDispatch.ClassifyExitCode(withExpected, "msi", 3010));
    }

    // ── D2: ARP local id resolves to the exact hive/view/subkey ──

    [Fact]
    public void ArpUninstallKeyCandidates_UseExactHiveViewAndSubkey()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        Assert.Equal(
            [new InstallerDispatch.ArpUninstallKeyCandidate(true, false, $@"{uninstall}\Git_is1")],
            InstallerDispatch.GetArpUninstallKeyCandidates(@"ARP\Machine\X64\Git_is1"));
        Assert.Equal(
            [new InstallerDispatch.ArpUninstallKeyCandidate(true, true, $@"{uninstall}\Git_is1")],
            InstallerDispatch.GetArpUninstallKeyCandidates(@"ARP\Machine\X86\Git_is1"));
        Assert.Equal(
            [new InstallerDispatch.ArpUninstallKeyCandidate(false, false, $@"{uninstall}\{{GUID}}")],
            InstallerDispatch.GetArpUninstallKeyCandidates(@"arp\user\x64\{GUID}"));

        var userX86 = InstallerDispatch.GetArpUninstallKeyCandidates(@"ARP\User\X86\Tool");
        Assert.NotNull(userX86);
        Assert.All(userX86!, candidate => Assert.False(candidate.Machine));
        Assert.Equal(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Tool", userX86![0].KeyPath);

        foreach (var invalid in new[]
                 {
                     @"MSIX\Contoso.App_1.0.0.0_x64__abc",
                     @"ARP\Machine\X64\",
                     @"ARP\Machine\X64",
                     @"ARP\Other\X64\Tool",
                     @"ARP\Machine\Arm\Tool",
                     @"ARP\Machine\X64\a\b",
                 })
        {
            Assert.Null(InstallerDispatch.GetArpUninstallKeyCandidates(invalid));
        }
    }

    // ── D3: installer file names from URLs / --rename ──

    [Theory]
    [InlineData("https://example.test/dl/setup.exe?x=1#frag", "exe", "setup.exe")]
    [InlineData("https://example.test/dl/My%20Setup.EXE", "inno", "My Setup.EXE")]
    [InlineData("https://example.test/dl/tool.msi", "wix", "tool.msi")]
    [InlineData("https://example.test/dl/app.msixbundle", "msix", "app.msixbundle")]
    [InlineData("https://example.test/dl/payload.bin", "msi", "payload.bin.msi")]
    [InlineData("https://example.test/download.php?id=1", "exe", "download.php.exe")]
    [InlineData("https://example.test/rg", "portable", "rg.exe")]
    [InlineData("https://example.test/archive", "zip", "archive.zip")]
    [InlineData("https://example.test/font.ttf", "font", "font.ttf")]
    [InlineData("https://example.test/?id=1", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/..", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/%2E%2E", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/..%5C..%5Cevil.exe", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/C:%5CWindows%5Cevil.exe", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/a%2Fb.exe", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/evil%00.exe", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/CON.exe", "exe", "Contoso.Tool.1.2.3.exe")]
    [InlineData("https://example.test/dl/name.", "exe", "Contoso.Tool.1.2.3.exe")]
    public void InstallerDownloadFileName_RejectsUnsafeUrlNamesAndFixesExtension(string url, string installerType, string expected)
    {
        Assert.Equal(expected, Repository.InstallerDownloadFileName(url, null, installerType, SimpleManifest()));
    }

    [Fact]
    public void InstallerDownloadFileName_ValidatesRename()
    {
        Assert.Equal("custom.exe", Repository.InstallerDownloadFileName("https://example.test/setup.exe", "custom.exe", "exe", SimpleManifest()));
        foreach (var invalid in new[] { "", "..", @"..\evil.exe", "../evil.exe", @"C:\evil.exe", "a:b.exe", "NUL" })
        {
            Assert.Throws<InvalidOperationException>(() =>
                Repository.InstallerDownloadFileName("https://example.test/setup.exe", invalid, "exe", SimpleManifest()));
        }
    }

    // ── D4: cmd.exe arguments for ARP uninstall strings ──

    [Fact]
    public void BuildCmdUninstallArguments_WrapsLineForCmdSSwitch()
    {
        Assert.Equal(
            "/D /S /C \"\"C:\\Program Files (x86)\\Tool\\unins000.exe\" /SILENT\"",
            InstallerDispatch.BuildCmdUninstallArguments("\"C:\\Program Files (x86)\\Tool\\unins000.exe\" /SILENT"));
    }

    // ── D6: --all-versions keeps the first failure ──

    [Fact]
    public void UninstallEachMatch_ReportsFirstFailureForAllVersions()
    {
        var matches = new[]
        {
            Match("A", @"ARP\Machine\X64\A1"),
            Match("A", @"ARP\Machine\X64\A2"),
            Match("A", @"ARP\Machine\X64\A3"),
        };

        var codes = new Queue<int>([1603, 0, 0]);
        var calls = 0;
        Assert.Equal(1603, InstallerDispatch.UninstallEachMatch(matches, true, _ => { calls++; return codes.Dequeue(); }));
        Assert.Equal(3, calls);

        codes = new Queue<int>([0, 5, 7]);
        Assert.Equal(5, InstallerDispatch.UninstallEachMatch(matches, true, _ => codes.Dequeue()));

        calls = 0;
        Assert.Equal(9, InstallerDispatch.UninstallEachMatch(matches, false, _ => { calls++; return 9; }));
        Assert.Equal(1, calls);
    }

    // ── D7: --uninstall-previous targets the installed package ──

    [Fact]
    public void UninstallPreviousRequest_TargetsInstalledPackageWithoutVersion()
    {
        var request = Repository.CreateUninstallPreviousRequest(Match("Contoso.Tool", @"ARP\Machine\X64\Contoso.Tool"));
        Assert.Equal("Contoso.Tool", request.Query.Id);
        Assert.True(request.Query.Exact);
        Assert.Null(request.Query.Version);
        Assert.Null(request.Query.Query);
        Assert.Null(request.ProductCode);
        Assert.Equal(InstallerMode.Silent, request.Mode);
        Assert.False(request.Preserve || request.Purge);

        Repository.EnsurePreviousVersionUninstalled("Contoso.Tool", 0);
        var ex = Assert.Throws<InvalidOperationException>(() => Repository.EnsurePreviousVersionUninstalled("Contoso.Tool", 1603));
        Assert.Contains("1603", ex.Message);
    }

    // ── D8: NSIS /D= stays unquoted and last ──

    [Fact]
    public void NsisInstallLocation_IsLastAndUnquoted()
    {
        var request = new InstallRequest
        {
            Query = new PackageQuery(),
            Mode = InstallerMode.Silent,
            Custom = "/NCRC",
            InstallLocation = @"C:\Program Files\My  Tool",
        };
        var args = InstallerDispatch.BuildArguments("nullsoft", request, SimpleManifest(), new Installer { InstallerType = "nullsoft" }, "setup.exe");
        Assert.Equal(["/S", "/NCRC", @"/D=C:\Program Files\My  Tool"], args);
        Assert.Equal(@"/S /NCRC /D=C:\Program Files\My  Tool", InstallerDispatch.JoinArguments(args));

        // A manifest template that quotes the token is still passed unquoted.
        var quoted = new Installer
        {
            InstallerType = "exe",
            Switches = new InstallerSwitches { InstallLocation = "/D=\"<INSTALLPATH>\"" },
        };
        args = InstallerDispatch.BuildArguments("exe", request with { Custom = null }, SimpleManifest(), quoted, "setup.exe");
        Assert.Equal(@"/D=C:\Program Files\My  Tool", args[^1]);
        // A generic exe gets no guessed silent switch (wave 7, R7-15), so the location is the only argument.
        Assert.Equal(@"/D=C:\Program Files\My  Tool", InstallerDispatch.JoinArguments(args));
    }

    // ── D12: missing InstallerSha256 ──

    [Fact]
    public void MissingInstallerHash_RequiresOverrideExceptForMsStore()
    {
        var exe = new Installer { InstallerType = "exe" };
        var ex = Assert.Throws<InvalidOperationException>(() => Repository.GetInstallerExpectedHash(exe, false));
        Assert.Contains("InstallerSha256", ex.Message);
        Assert.Null(Repository.GetInstallerExpectedHash(exe, true));
        Assert.Throws<InvalidOperationException>(() => Repository.GetInstallerExpectedHash(exe with { Sha256 = "  " }, false));
        Assert.Equal("ABC", Repository.GetInstallerExpectedHash(exe with { Sha256 = "ABC" }, false));
        Assert.Null(Repository.GetInstallerExpectedHash(new Installer { InstallerType = "MSStore" }, false));
    }

    [Fact]
    public void DownloadInstaller_RejectsMissingHashWithoutOverride()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "pinget-dotnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);
        try
        {
            var manifestPath = Path.Combine(appRoot, "manifest.yaml");
            File.WriteAllText(manifestPath, """
                PackageIdentifier: Test.Package
                PackageVersion: 1.0.0
                PackageName: Test Package
                ManifestType: merged
                ManifestVersion: 1.10.0
                Installers:
                  - InstallerType: exe
                    InstallerUrl: https://example.invalid/installer.exe
                """);

            using var repo = Repository.Open(new RepositoryOptions { AppRoot = appRoot });
            repo.SetAdminSetting("LocalManifestFiles", true);
            var downloads = Path.Combine(appRoot, "downloads");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                repo.DownloadInstaller(new InstallRequest { Query = new PackageQuery(), ManifestPath = manifestPath }, downloads));
            Assert.Contains("InstallerSha256", ex.Message);
            Assert.False(Directory.Exists(downloads) && Directory.EnumerateFileSystemEntries(downloads).Any());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(appRoot, recursive: true);
        }
    }

    // ── D14: <LOGPATH>/<INSTALLPATH> templates ──

    [Fact]
    public void SwitchTemplates_ReplaceTokensCaseInsensitivelyAndDropOnlyUnfilledTokens()
    {
        Assert.Equal("/log \"C:\\l.log\"", InstallerDispatch.PopulateSwitchTemplate("/log \"<logpath>\"", @"C:\l.log", null));
        Assert.Equal(@"INSTALLDIR=D:\x", InstallerDispatch.PopulateSwitchTemplate("INSTALLDIR=<InstallPath>", null, @"D:\x"));
        Assert.Null(InstallerDispatch.PopulateSwitchTemplate("/log <LOGPATH>", null, @"D:\x"));
        Assert.Null(InstallerDispatch.PopulateSwitchTemplate("/log <LOGPATH>", "  ", null));
        Assert.Equal("/VERBOSELOG", InstallerDispatch.PopulateSwitchTemplate("/VERBOSELOG", null, null));

        var installer = new Installer
        {
            InstallerType = "exe",
            Switches = new InstallerSwitches { Silent = "/q", Log = "/VERBOSELOG", InstallLocation = "/DIR=<installpath>" },
        };
        var request = new InstallRequest { Query = new PackageQuery(), Mode = InstallerMode.Silent };
        Assert.Equal(["/q", "/VERBOSELOG"], InstallerDispatch.BuildArguments("exe", request, SimpleManifest(), installer, "setup.exe"));
        Assert.Equal(
            ["/q", "/VERBOSELOG", @"/DIR=D:\Apps"],
            InstallerDispatch.BuildArguments("exe", request with { InstallLocation = @"D:\Apps" }, SimpleManifest(), installer, "setup.exe"));
    }

    // ── D9: portable uninstall with --preserve still unregisters ──

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PortableUninstallPreserve_KeepsFilesButRemovesArpEntry()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var unique = $"PingetWave1Preserve_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var installDir = Path.Combine(Path.GetTempPath(), unique);
        Directory.CreateDirectory(installDir);
        var payload = Path.Combine(installDir, "tool.exe");
        File.WriteAllText(payload, "payload");
        var keyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{unique}";
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue("DisplayName", unique);
            key.SetValue("WinGetInstallerType", "portable");
            key.SetValue("InstallLocation", installDir);
        }

        bool keyStillExists;
        bool payloadKept;
        int exitCode;
        try
        {
            var installed = Match(unique, $@"ARP\User\X64\{unique}") with
            {
                InstallerCategory = "portable",
                InstallLocation = installDir,
                Scope = "user",
            };
            exitCode = InstallerDispatch.Uninstall(installed, new UninstallRequest { Query = new PackageQuery(), Preserve = true }, []);
        }
        finally
        {
            using (var check = Registry.CurrentUser.OpenSubKey(keyPath))
                keyStillExists = check is not null;
            if (keyStillExists)
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            payloadKept = File.Exists(payload);
            Directory.Delete(installDir, recursive: true);
        }

        Assert.Equal(0, exitCode);
        Assert.True(payloadKept, "--preserve must keep the package files");
        Assert.False(keyStillExists, "--preserve must still remove the ARP entry");
    }
}
