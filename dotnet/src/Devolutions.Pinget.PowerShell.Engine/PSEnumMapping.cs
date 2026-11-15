using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Engine;

/// <summary>Maps PowerShell-facing enums to the values the Pinget cores understand.</summary>
internal static class PSEnumMapping
{
    /// <summary>
    /// Maps <c>-Scope</c> to the core install scope like WinGet's <c>PSEnumHelpers.ToPackageInstallScope</c>:
    /// the cores only know <c>user</c> and <c>machine</c>, and <c>Any</c> means no scope filter.
    /// </summary>
    internal static string? ToInstallScope(PSPackageInstallScope scope)
    {
        switch (scope)
        {
            case PSPackageInstallScope.User:
            case PSPackageInstallScope.UserOrUnknown:
                return "user";
            case PSPackageInstallScope.System:
            case PSPackageInstallScope.SystemOrUnknown:
                return "machine";
            default:
                return null;
        }
    }

    /// <summary>Pinget queries are either exact (case-insensitive) or substring matches.</summary>
    internal static bool UsesExactMatch(PSPackageFieldMatchOption matchOption) =>
        matchOption == PSPackageFieldMatchOption.Equals || matchOption == PSPackageFieldMatchOption.EqualsCaseInsensitive;

    /// <summary>
    /// The cores only match exactly (case-insensitive) or by substring, so WinGet's case-sensitive
    /// <c>Equals</c> and prefix <c>StartsWithCaseInsensitive</c> are applied here to the results of the
    /// <c>-Id</c>, <c>-Name</c> and <c>-Moniker</c> filters.
    /// </summary>
    internal static IEnumerable<T> FilterByMatchOption<T>(
        IEnumerable<T> packages,
        PSPackageFieldMatchOption matchOption,
        string? id,
        string? name,
        string? moniker)
        where T : PSCatalogPackage
    {
        Func<string?, string, bool>? matches = matchOption switch
        {
            PSPackageFieldMatchOption.Equals => (value, filter) => string.Equals(value, filter, StringComparison.Ordinal),
            PSPackageFieldMatchOption.StartsWithCaseInsensitive => (value, filter) =>
                value is not null && value.StartsWith(filter, StringComparison.OrdinalIgnoreCase),
            _ => null,
        };

        if (matches is null)
            return packages;

        return packages.Where(package =>
            Passes(package.Id, id) && Passes(package.Name, name) && Passes(package.Moniker, moniker));

        bool Passes(string? value, string? filter) => string.IsNullOrWhiteSpace(filter) || matches(value, filter!);
    }
}
