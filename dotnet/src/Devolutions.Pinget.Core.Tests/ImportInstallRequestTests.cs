using System.Text.Json;
using Devolutions.Pinget.Cli;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class ImportInstallRequestTests
{
    [Fact]
    public void ImportCustomSwitchesReachInstallerDispatch()
    {
        using var document = JsonDocument.Parse("""{"InitialCustomSwitches":"/custom-from-import"}""");
        var request = ImportInstallRequestFactory.Create(
            new PackageQuery { Id = "Test.Package", Exact = true },
            document.RootElement,
            acceptPackageAgreements: false,
            noUpgrade: false);

        var args = InstallerDispatch.BuildArguments(
            "exe",
            request,
            new Manifest { Id = "Test.Package", Name = "Test", Version = "1.0" },
            new Installer { InstallerType = "exe" });

        Assert.Contains("/custom-from-import", args);
    }

    [Fact]
    public void ImportOverrideArgumentsReachInstallerDispatch()
    {
        using var document = JsonDocument.Parse("""{"InitialOverrideArguments":"/override-from-import /quiet"}""");
        var request = ImportInstallRequestFactory.Create(
            new PackageQuery { Id = "Test.Package", Exact = true },
            document.RootElement,
            acceptPackageAgreements: false,
            noUpgrade: false);

        var args = InstallerDispatch.BuildArguments(
            "exe",
            request,
            new Manifest { Id = "Test.Package", Name = "Test", Version = "1.0" },
            new Installer { InstallerType = "exe" });

        Assert.Equal(["/override-from-import", "/quiet"], args);
    }

    [Fact]
    public void ImportScopeAndChannelReachPackageSelection()
    {
        using var document = JsonDocument.Parse("""{"Scope":"machine","Channel":"preview"}""");
        var request = ImportInstallRequestFactory.Create(
            new PackageQuery { Id = "Test.Package", Exact = true },
            document.RootElement,
            acceptPackageAgreements: false,
            noUpgrade: false);

        Assert.Equal("machine", request.Query.InstallScope);
        Assert.Equal("preview", request.Query.Channel);
    }

    [Fact]
    public void ImportWithoutOptionsKeepsExistingInstallRequestDefaults()
    {
        using var document = JsonDocument.Parse("""{"PackageIdentifier":"Test.Package"}""");
        var request = ImportInstallRequestFactory.Create(
            new PackageQuery { Id = "Test.Package", Exact = true },
            document.RootElement,
            acceptPackageAgreements: false,
            noUpgrade: false);

        Assert.Equal(InstallerMode.SilentWithProgress, request.Mode);
        Assert.Null(request.Custom);
        Assert.Null(request.Override);
        Assert.False(request.AcceptPackageAgreements);
        Assert.False(request.NoUpgrade);

        var manifest = new Manifest { Id = "Test.Package", Name = "Test", Version = "1.0" };
        var installer = new Installer { InstallerType = "exe" };
        var importedArgs = InstallerDispatch.BuildArguments("exe", request, manifest, installer);
        var defaultArgs = InstallerDispatch.BuildArguments(
            "exe",
            new InstallRequest { Query = request.Query, Mode = InstallerMode.SilentWithProgress },
            manifest,
            installer);

        Assert.Equal(defaultArgs, importedArgs);
    }
}
