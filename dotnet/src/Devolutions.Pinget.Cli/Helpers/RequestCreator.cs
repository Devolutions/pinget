using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class RequestCreator
{
    internal static InstallRequest Install(
    PackageQuery query,
    string? manifestPath,
    InstallerMode mode,
    string? logPath,
    string? custom,
    string? overrideArgs,
    string? installLocation,
    bool skipDependencies,
    bool dependenciesOnly,
    bool acceptPackageAgreements,
    bool force,
    string? rename,
    bool uninstallPrevious,
    bool ignoreSecurityHash,
    string? dependencySource,
    bool noUpgrade) =>
    new()
    {
        Query = query,
        ManifestPath = manifestPath,
        Mode = mode,
        LogPath = logPath,
        Custom = custom,
        Override = overrideArgs,
        InstallLocation = installLocation,
        SkipDependencies = skipDependencies,
        DependenciesOnly = dependenciesOnly,
        AcceptPackageAgreements = acceptPackageAgreements,
        Force = force,
        Rename = rename,
        UninstallPrevious = uninstallPrevious,
        IgnoreSecurityHash = ignoreSecurityHash,
        DependencySource = dependencySource,
        NoUpgrade = noUpgrade,
    };

    internal static RepairRequest Repair(
        PackageQuery query,
        string? manifestPath,
        string? productCode,
        InstallerMode mode,
        string? logPath,
        bool acceptPackageAgreements,
        bool force,
        bool ignoreSecurityHash) =>
        new()
        {
            Query = query,
            ManifestPath = manifestPath,
            ProductCode = productCode,
            Mode = mode,
            LogPath = logPath,
            AcceptPackageAgreements = acceptPackageAgreements,
            Force = force,
            IgnoreSecurityHash = ignoreSecurityHash,
        };
}
