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
}
