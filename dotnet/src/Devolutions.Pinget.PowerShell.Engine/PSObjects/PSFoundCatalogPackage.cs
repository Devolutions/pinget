namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSFoundCatalogPackage : PSCatalogPackage
{
    public PSFoundCatalogPackage(string id, string name, string source, string? moniker, string? version, string? match)
        : base(id, name, source, moniker, CreatePackageVersions(id, name, version))
    {
        Version = version;
        Match = match;
    }

    public string? Version { get; }

    public string? Match { get; }

    private static IReadOnlyList<PSPackageVersionInfo> CreatePackageVersions(string id, string name, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return [];

        return
        [
            new PSPackageVersionInfo(version, id, name, null, null, [], []),
        ];
    }
}
