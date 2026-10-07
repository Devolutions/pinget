using Devolutions.Pinget.Cli;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class ImportCommandHandlerTests
{
    private static readonly ImportPackage[] MixedPackages =
    [
        new("Failing.Package", "test", "1.0"),
        new("Successful.Package", "test", "1.0"),
    ];

    [Fact]
    public void HandlerAttemptsSuccessAfterUnsuccessfulInstallerAndReturnsNonzero()
    {
        var attempted = new List<string>();

        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                return Result(package.Id, success: package.Id == "Successful.Package");
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void HandlerAttemptsSuccessAfterThrownInstallerErrorAndReturnsNonzero()
    {
        var attempted = new List<string>();

        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                if (package.Id == "Failing.Package")
                    throw new InvalidOperationException("synthetic installer error");
                return Result(package.Id, success: true);
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData("No applicable installer found")]
    [InlineData("no package matched the supplied query")]
    [InlineData("No package matched the query.")]
    public void IgnoreUnavailableOnlySuppressesClassifiedErrorsAndContinues(string errorMessage)
    {
        var attempted = new List<string>();

        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: true,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                if (package.Id == "Failing.Package")
                    throw new InvalidOperationException(errorMessage);
                return Result(package.Id, success: true);
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void IgnoreUnavailableDoesNotSuppressUnclassifiedError()
    {
        var exitCode = ImportCommandHandler.Run(
            [MixedPackages[0]],
            dryRun: false,
            ignoreUnavailable: true,
            noUpgrade: false,
            _ => false,
            _ => throw new InvalidOperationException("source configuration is invalid"));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void DryRunDoesNotInvokeInstallAndReturnsSuccess()
    {
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: true,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => throw new Xunit.Sdk.XunitException("dry-run must not query installed packages"),
            _ => throw new Xunit.Sdk.XunitException("dry-run must not install packages"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void NoOpResultDoesNotFailImport()
    {
        var exitCode = ImportCommandHandler.Run(
            [MixedPackages[0]],
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => false,
            _ => Result("Failing.Package", success: false, noOp: true));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void MissingPackageWithoutIgnoreUnavailableFailsAndContinues()
    {
        var attempted = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                if (package.Id == "Failing.Package")
                    throw new InvalidOperationException("no package matched the supplied query");
                return Result(package.Id, success: true);
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void IgnoreUnavailableDoesNotSuppressUnsuccessfulInstaller()
    {
        var attempted = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: true,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                return Result(package.Id, success: package.Id == "Successful.Package");
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void NoUpgradeSkipsInstalledPackageAndContinues()
    {
        var attempted = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: true,
            package => package.Id == "Failing.Package",
            package =>
            {
                attempted.Add(package.Id);
                return Result(package.Id, success: true);
            });

        Assert.Equal(new[] { "Successful.Package" }, attempted);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void NoUpgradeLookupFailureDoesNotStopLaterPackages()
    {
        var attempted = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: true,
            noUpgrade: true,
            package => package.Id == "Failing.Package"
                ? throw new InvalidOperationException("source configuration is invalid")
                : false,
            package =>
            {
                attempted.Add(package.Id);
                return Result(package.Id, success: true);
            });

        Assert.Equal(new[] { "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void LaterNoOpDoesNotClearEarlierFailure()
    {
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: false,
            noUpgrade: false,
            _ => false,
            package => Result(package.Id, success: false, noOp: package.Id == "Successful.Package"));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void LaterIgnoredUnavailablePackageDoesNotClearEarlierFailure()
    {
        var attempted = new List<string>();
        var exitCode = ImportCommandHandler.Run(
            MixedPackages,
            dryRun: false,
            ignoreUnavailable: true,
            noUpgrade: false,
            _ => false,
            package =>
            {
                attempted.Add(package.Id);
                if (package.Id == "Successful.Package")
                    throw new InvalidOperationException("no package matched the supplied query");
                return Result(package.Id, success: false);
            });

        Assert.Equal(new[] { "Failing.Package", "Successful.Package" }, attempted);
        Assert.Equal(1, exitCode);
    }

    private static InstallResult Result(string packageId, bool success, bool noOp = false) => new()
    {
        PackageId = packageId,
        Version = "1.0",
        InstallerPath = string.Empty,
        InstallerType = "exe",
        ExitCode = success ? 0 : 1,
        Success = success,
        NoOp = noOp,
    };
}
