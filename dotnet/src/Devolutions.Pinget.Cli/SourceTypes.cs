using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal static class SourceTypes
{
    /// <summary>
    /// Parses <c>source add --type</c>. Like WinGet (RepositorySource.cpp), an omitted type means
    /// <c>Microsoft.PreIndexed.Package</c>.
    /// </summary>
    internal static SourceKind Parse(string? value)
    {
        if (string.Equals(value, "rest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.Rest;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "preindexed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.PreIndexed;
        }

        throw new InvalidOperationException($"Unsupported source type: {value}");
    }
}
