using System.Linq;

namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public abstract class PSCatalogPackage
{
    private readonly IReadOnlyList<PSPackageVersionInfo> _packageVersions;

    protected PSCatalogPackage(
        string id,
        string name,
        string source,
        string? moniker,
        IReadOnlyList<PSPackageVersionInfo>? packageVersions = null)
    {
        Id = id;
        Name = name;
        Source = source;
        Moniker = moniker;
        _packageVersions = packageVersions ?? [];
    }

    public string Id { get; }

    public string Name { get; }

    public string Source { get; }

    public string? Moniker { get; }

    public bool IsUpdateAvailable => this is PSInstalledCatalogPackage && AvailableVersions.Count > 0;

    public IReadOnlyList<string> AvailableVersions => _packageVersions.Select(version => version.Version).ToArray();

    public string CheckInstalledStatus() => this switch
    {
        PSInstalledCatalogPackage installed when installed.IsUpdateAvailable => "UpdateAvailable",
        PSInstalledCatalogPackage => "Installed",
        _ => "NotInstalled",
    };

    public PSPackageVersionInfo GetPackageVersionInfo(string version)
    {
        var match = _packageVersions.FirstOrDefault(info => string.Equals(info.Version, version, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new InvalidOperationException($"No package version info found for version '{version}'.");

        return match;
    }
}
