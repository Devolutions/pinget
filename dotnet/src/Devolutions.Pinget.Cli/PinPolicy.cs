using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

/// <summary>
/// WinGet pin rules used by <c>pin add</c> and <c>upgrade</c>. Mirrors the Rust CLI helpers
/// (pin_type_for_add, pin_version_for_add, upgrade_pin_block_message).
/// </summary>
internal static class PinPolicy
{
    /// <summary>
    /// <c>--blocking</c> blocks every upgrade, <c>--version</c> gates upgrades to a version range, and a
    /// plain pin blocks implicit upgrades.
    /// </summary>
    internal static PinType PinTypeForAdd(bool blocking, string? version) =>
        blocking
            ? PinType.Blocking
            : !string.IsNullOrWhiteSpace(version)
                ? PinType.Gating
                : PinType.Pinning;

    /// <summary>
    /// The version stored with a new pin: the requested gate, <c>*</c> for blocking pins, otherwise the
    /// installed version when it is known.
    /// </summary>
    internal static string PinVersionForAdd(string? requested, bool blocking, string? installedVersion)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;
        if (blocking)
            return "*";
        return string.IsNullOrWhiteSpace(installedVersion) ||
            installedVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                ? "*"
                : installedVersion;
    }

    /// <summary>Installed version of <paramref name="packageId"/> (best effort) so <c>pin add</c> can record it.</summary>
    internal static string? InstalledVersionForPin(Repository repo, string packageId, string? sourceName)
    {
        try
        {
            var result = repo.List(new ListQuery
            {
                Id = packageId,
                Source = string.IsNullOrWhiteSpace(sourceName) ? null : sourceName,
                Exact = true,
                Count = 200,
            });
            return result.Matches
                .FirstOrDefault(match => match.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                ?.InstalledVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Message explaining why a pin stops this upgrade, or <c>null</c> when it may proceed.</summary>
    internal static string? UpgradeBlockMessage(PinRecord pin, string targetVersion, bool includePinned)
    {
        if (!Repository.PinBlocksUpgrade(pin, targetVersion, includePinned))
            return null;

        return pin.PinType switch
        {
            PinType.Blocking => $"Package is blocked by pin {pin.Version}; remove the pin before upgrading.",
            PinType.Gating => $"Version {targetVersion} is outside the pinned version range {pin.Version}; remove or update the pin before upgrading.",
            _ => $"Package is pinned at {pin.Version}; use --include-pinned or remove the pin before upgrading.",
        };
    }
}
