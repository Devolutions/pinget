namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSPackageVersionInfo
{
    public PSPackageVersionInfo(
        string version,
        string id,
        string displayName,
        string? publisher,
        string? channel,
        IReadOnlyList<string> packageFamilyNames,
        IReadOnlyList<string> productCodes)
    {
        Version = version;
        Id = id;
        DisplayName = displayName;
        Publisher = publisher ?? string.Empty;
        Channel = channel ?? string.Empty;
        PackageFamilyNames = packageFamilyNames.ToArray();
        ProductCodes = productCodes.ToArray();
    }

    internal string Version { get; }

    public string DisplayName { get; }

    public string Id { get; }

    public string Publisher { get; }

    public string Channel { get; }

    public string[] PackageFamilyNames { get; }

    public string[] ProductCodes { get; }

    public PSCompareResult CompareToVersion(string version) => CompareVersionStrings(Version, version);

    internal static PSCompareResult CompareVersionStrings(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return PSCompareResult.Unknown;

        if (System.Version.TryParse(NormalizeVersion(left), out var leftVersion) &&
            System.Version.TryParse(NormalizeVersion(right), out var rightVersion))
        {
            var result = leftVersion.CompareTo(rightVersion);
            return result switch
            {
                < 0 => PSCompareResult.Lesser,
                > 0 => PSCompareResult.Greater,
                _ => PSCompareResult.Equal,
            };
        }

        var lexical = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return lexical switch
        {
            < 0 => PSCompareResult.Lesser,
            > 0 => PSCompareResult.Greater,
            _ => PSCompareResult.Equal,
        };
    }

    private static string NormalizeVersion(string value) => value.Trim().TrimStart('v', 'V');
}
