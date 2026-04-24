using System.Linq;

namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSInstalledCatalogPackage : PSCatalogPackage
{
    public PSInstalledCatalogPackage(
        string id,
        string name,
        string source,
        string? moniker,
        string installedVersion,
        IReadOnlyList<string> availableVersions,
        string? publisher,
        string? scope,
        IReadOnlyList<string>? packageFamilyNames = null,
        IReadOnlyList<string>? productCodes = null)
        : base(id, name, source, moniker, CreatePackageVersions(id, name, publisher, availableVersions, packageFamilyNames, productCodes))
    {
        InstalledVersion = installedVersion;
        Publisher = publisher;
        Scope = scope;
        PackageFamilyNames = packageFamilyNames ?? [];
        ProductCodes = productCodes ?? [];
    }

    public string InstalledVersion { get; }

    public string? Publisher { get; }

    public string? Scope { get; }

    public IReadOnlyList<string> PackageFamilyNames { get; }

    public IReadOnlyList<string> ProductCodes { get; }

    public PSCompareResult CompareToVersion(string version) => PSPackageVersionInfo.CompareVersionStrings(InstalledVersion, version);

    private static IReadOnlyList<PSPackageVersionInfo> CreatePackageVersions(
        string id,
        string name,
        string? publisher,
        IReadOnlyList<string> availableVersions,
        IReadOnlyList<string>? packageFamilyNames,
        IReadOnlyList<string>? productCodes)
    {
        return availableVersions
            .Select(version => new PSPackageVersionInfo(
                version,
                id,
                name,
                publisher,
                null,
                packageFamilyNames ?? [],
                productCodes ?? []))
            .ToList();
    }
}
