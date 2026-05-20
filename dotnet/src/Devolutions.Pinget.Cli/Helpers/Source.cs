using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class Source
{
    internal static string ResolveAddValue(string? positionalValue, string? optionValue, string label)
    {
        if (!string.IsNullOrWhiteSpace(positionalValue) &&
            !string.IsNullOrWhiteSpace(optionValue) &&
            !string.Equals(positionalValue, optionValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Conflicting source {label} values were provided.");
        }

        if (!string.IsNullOrWhiteSpace(optionValue))
            return optionValue;

        if (!string.IsNullOrWhiteSpace(positionalValue))
            return positionalValue;

        throw new InvalidOperationException($"source add requires a {label}.");
    }

    internal static SourceKind ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "rest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.Rest;
        }

        if (string.Equals(value, "preindexed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.PreIndexed;
        }

        throw new InvalidOperationException($"Unsupported source type: {value}");
    }

    internal static string FormatType(SourceKind kind) => kind switch
    {
        SourceKind.Rest => "Microsoft.Rest",
        SourceKind.PreIndexed => "Microsoft.PreIndexed.Package",
        _ => kind.ToString(),
    };

}
