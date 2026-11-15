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

    /// <summary>
    /// Compares versions like WinGet's <c>Utility::Version</c>: dot-separated parts compare by their leading
    /// integer and then by their trailing text (a part with text sorts before the same bare number), and
    /// trailing zero parts are ignored, so <c>1.0</c> equals <c>1.0.0</c>.
    /// </summary>
    internal static PSCompareResult CompareVersionStrings(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return PSCompareResult.Unknown;

        var leftParts = ParseVersionParts(left!);
        var rightParts = ParseVersionParts(right!);
        for (var i = 0; i < Math.Max(leftParts.Count, rightParts.Count); i++)
        {
            var leftPart = i < leftParts.Count ? leftParts[i] : (0UL, string.Empty);
            var rightPart = i < rightParts.Count ? rightParts[i] : (0UL, string.Empty);
            var result = CompareVersionParts(leftPart, rightPart);
            if (result != 0)
                return result < 0 ? PSCompareResult.Lesser : PSCompareResult.Greater;
        }

        return PSCompareResult.Equal;
    }

    private static List<(ulong Integer, string Other)> ParseVersionParts(string value)
    {
        var text = value.Trim();

        // Like WinGet, leading text such as "v" is dropped when a digit comes before the first separator.
        var digit = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is >= '0' and <= '9')
            {
                digit = i;
                break;
            }
        }

        var separator = text.IndexOf('.');
        if (digit > 0 && (separator < 0 || digit < separator))
            text = text.Substring(digit);

        var parts = text.Split('.').Select(ParseVersionPart).ToList();
        while (parts.Count > 0 && parts[parts.Count - 1].Integer == 0 && parts[parts.Count - 1].Other.Length == 0)
            parts.RemoveAt(parts.Count - 1);

        return parts;
    }

    private static (ulong Integer, string Other) ParseVersionPart(string part)
    {
        var trimmed = part.Trim();
        ulong integer = 0;
        var end = 0;
        while (end < trimmed.Length && trimmed[end] is >= '0' and <= '9')
        {
            var digit = (ulong)(trimmed[end] - '0');
            // An out-of-range number is kept as text, like WinGet's strtoull ERANGE handling.
            if (integer > (ulong.MaxValue - digit) / 10)
                return (0, trimmed);

            integer = (integer * 10) + digit;
            end++;
        }

        return end == 0 ? (0, trimmed) : (integer, trimmed.Substring(end).Trim());
    }

    private static int CompareVersionParts((ulong Integer, string Other) left, (ulong Integer, string Other) right)
    {
        if (left.Integer != right.Integer)
            return left.Integer < right.Integer ? -1 : 1;

        var leftBare = left.Other.Length == 0;
        var rightBare = right.Other.Length == 0;
        if (leftBare && rightBare)
            return 0;
        if (leftBare)
            return 1;
        if (rightBare)
            return -1;

        return Math.Sign(string.Compare(left.Other, right.Other, StringComparison.OrdinalIgnoreCase));
    }
}
