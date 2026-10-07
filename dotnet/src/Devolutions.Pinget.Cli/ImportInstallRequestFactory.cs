using System.Text.Json;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal static class ImportInstallRequestFactory
{
    internal static InstallRequest Create(
        PackageQuery query,
        JsonElement package,
        bool acceptPackageAgreements,
        bool noUpgrade) =>
        new()
        {
            Query = query with
            {
                Channel = GetString(package, "Channel") ?? query.Channel,
                InstallScope = GetString(package, "Scope") ?? query.InstallScope,
            },
            Mode = InstallerMode.SilentWithProgress,
            Custom = GetString(package, "InitialCustomSwitches"),
            Override = GetString(package, "InitialOverrideArguments"),
            AcceptPackageAgreements = acceptPackageAgreements,
            NoUpgrade = noUpgrade,
        };

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
