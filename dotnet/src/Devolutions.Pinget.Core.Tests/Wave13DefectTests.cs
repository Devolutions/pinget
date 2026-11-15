using System.Text;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

/// <summary>Regression tests for the installer execution and dispatch defect fixes (wave 13).</summary>
public class Wave13DefectTests
{
    private static Manifest SimpleManifest() =>
        Repository.ParseYamlManifest(Encoding.UTF8.GetBytes("""
            PackageIdentifier: Contoso.App
            PackageVersion: 2.0.0
            PackageName: Contoso App
            Publisher: Contoso
            ManifestType: merged
            ManifestVersion: 1.10.0
            Installers: []
            """));

    private static InstallRequest Request(InstallerMode mode, string? location = null) =>
        new() { Query = new PackageQuery(), Mode = mode, InstallLocation = location };

    private static List<string> Args(string installerType, InstallRequest request, Installer installer) =>
        InstallerDispatch.BuildArguments(installerType, request, SimpleManifest(), installer, "setup.bin", includeMsiInstallTarget: false);

    private static ListMatch Match(string name, string id, string localId) => new()
    {
        Name = name,
        Id = id,
        LocalId = localId,
        InstalledVersion = "1.0.0",
        SourceName = "winget",
    };

    // ── R13-1: an interactive uninstall falls back to QuietUninstallString ──

    [Fact]
    public void InteractiveUninstall_FallsBackToTheQuietCommand()
    {
        Assert.Equal(("unins.exe /SILENT", true),
            InstallerDispatch.SelectArpUninstallCommand(InstallerMode.Interactive, "unins.exe /SILENT", null));
        Assert.Equal(("unins.exe", false),
            InstallerDispatch.SelectArpUninstallCommand(InstallerMode.Interactive, "unins.exe /SILENT", "unins.exe"));
        Assert.Equal(("unins.exe /SILENT", true),
            InstallerDispatch.SelectArpUninstallCommand(InstallerMode.Silent, "unins.exe /SILENT", "unins.exe"));
    }

    // ── R13-2: blank ARP uninstall strings are not run as an empty, always-successful command ──

    [Fact]
    public void BlankUninstallStrings_AreIgnored()
    {
        Assert.Equal(("unins.exe", false),
            InstallerDispatch.SelectArpUninstallCommand(InstallerMode.Silent, "   ", "unins.exe"));
        Assert.Equal(("unins.exe /S", true),
            InstallerDispatch.SelectArpUninstallCommand(InstallerMode.Interactive, "unins.exe /S", ""));
        Assert.Null(InstallerDispatch.SelectArpUninstallCommand(InstallerMode.SilentWithProgress, "", " "));
    }

    // ── R13-3: direct MSI execution accepts dotted property names, like WinGet ──

    [Fact]
    public void DirectMsi_AcceptsDottedPropertyNames()
    {
        var parsed = InstallerDispatch.ParseMsiDirectArguments(
            ["/quiet", "MY.PROPERTY=1", "_PRIVATE=2", @"INSTALL.DIR=C:\Program Files\Contoso"]);
        Assert.Equal(@"MY.PROPERTY=1 _PRIVATE=2 INSTALL.DIR=""C:\Program Files\Contoso""", parsed.Properties);
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.ParseMsiDirectArguments([".BAD=1"]));
        Assert.Throws<InvalidOperationException>(() => InstallerDispatch.ParseMsiDirectArguments(["=1"]));
    }

    // ── R13-4: the requested mode's switch comes from the manifest, then the type defaults ──

    [Fact]
    public void RequestedMode_UsesTypeDefaultBeforeTheOtherModeSwitch()
    {
        var msi = new Installer { Switches = new InstallerSwitches { SilentWithProgress = "/passive" } };
        Assert.Equal(["/quiet", "/norestart"], Args("msi", Request(InstallerMode.Silent), msi));
        Assert.Equal(["/passive"], Args("msi", Request(InstallerMode.SilentWithProgress), msi));

        var inno = new Installer { Switches = new InstallerSwitches { Silent = "/VERYSILENT" } };
        Assert.Equal(["/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
            Args("inno", Request(InstallerMode.SilentWithProgress), inno));

        // A generic exe has no defaults, so the other mode's switch is still used.
        var exe = new Installer { Switches = new InstallerSwitches { Silent = "--quiet" } };
        Assert.Equal(["--quiet"], Args("exe", Request(InstallerMode.SilentWithProgress), exe));
    }

    // ── R13-6: --uninstall-previous only applies to an update ──

    [Fact]
    public void UninstallPrevious_OnlyAppliesToUpdates()
    {
        var install = Request(InstallerMode.Silent) with { UninstallPrevious = true };
        var installer = new Installer();
        Assert.False(Repository.UninstallsPreviousVersion(install, installer, isUpdate: false));
        Assert.True(Repository.UninstallsPreviousVersion(install, installer, isUpdate: true));

        var behavior = new Installer { UpgradeBehavior = "uninstallPrevious" };
        var plain = Request(InstallerMode.Silent);
        Assert.False(Repository.UninstallsPreviousVersion(plain, behavior, isUpdate: false));
        Assert.True(Repository.UninstallsPreviousVersion(plain, behavior, isUpdate: true));
        Assert.False(Repository.UninstallsPreviousVersion(plain, installer, isUpdate: true));
    }

    // ── R13-7: the Upgrade switch is dropped once the previous version was uninstalled ──

    [Fact]
    public void UpgradeSwitch_IsDroppedAfterUninstallingThePreviousVersion()
    {
        Assert.True(Repository.UpgradeSwitchApplies(isUpdate: true, uninstallsPrevious: false));
        Assert.False(Repository.UpgradeSwitchApplies(isUpdate: true, uninstallsPrevious: true));
        Assert.False(Repository.UpgradeSwitchApplies(isUpdate: false, uninstallsPrevious: false));
    }

    // ── R13-8: the downloaded installer is removed after a successful run only ──

    [Fact]
    public void DownloadedInstaller_IsRemovedAfterSuccessOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pinget-wave13-remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var installerPath = Path.Combine(root, "setup.exe");
            InstallResult Result(bool success) => new()
            {
                PackageId = "Contoso.App",
                Version = "2.0.0",
                InstallerPath = installerPath,
                InstallerType = "exe",
                ExitCode = success ? 0 : 1,
                Success = success,
                NoOp = false,
            };

            File.WriteAllText(installerPath, "installer");
            Repository.RemoveInstallerAfterSuccess(Result(false));
            Assert.True(File.Exists(installerPath), "a failed installer is kept");

            var succeeded = Result(true);
            Repository.RemoveInstallerAfterSuccess(succeeded);
            Assert.False(File.Exists(installerPath), "a successful installer is removed");
            Assert.Empty(succeeded.Warnings);

            var again = Result(true);
            Repository.RemoveInstallerAfterSuccess(again);
            Assert.Empty(again.Warnings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── R13-9: the InstallLocation switch is only added when --location is supplied ──

    [Fact]
    public void InstallLocationSwitch_RequiresALocation()
    {
        var installer = new Installer { Switches = new InstallerSwitches { Silent = "/S", InstallLocation = "/ALLUSERS" } };
        Assert.Equal(["/S"], Args("exe", Request(InstallerMode.Silent), installer));
        Assert.Equal(["/S", "/ALLUSERS"], Args("exe", Request(InstallerMode.Silent, @"C:\Apps\Contoso"), installer));
        Assert.Equal(["/S", @"/D=C:\Apps\Contoso"], Args("nullsoft", Request(InstallerMode.Silent, @"C:\Apps\Contoso"), new Installer()));
    }

    // ── R13-10: an MSIX package never matches a same-named ARP entry ──

    [Fact]
    public void MsixPackages_DoNotMatchArpEntriesByName()
    {
        var msix = Match("Windows Terminal", "Microsoft.WindowsTerminal",
            @"MSIX\Microsoft.WindowsTerminal_1.21.0.0_x64__8wekyb3d8bbwe");
        Assert.False(InstallerDispatch.RegistryEntryMatchesInstalledPackage("Windows Terminal", "Windows Terminal", null, msix));

        var other = Match("Windows Terminal", "Contoso.Terminal", "Contoso.Terminal");
        Assert.True(InstallerDispatch.RegistryEntryMatchesInstalledPackage("Windows Terminal", "Windows Terminal", null, other));
    }
}
