using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Pinget.Core;

internal static class InstalledPackages
{
    public static List<InstalledPackage> Collect(string? scope)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return [];

        var packages = new List<InstalledPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool machine = !string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase);
        bool user = !string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase);

        if (machine)
        {
            CollectArpPackages(packages, seen, Microsoft.Win32.RegistryHive.LocalMachine, "Machine", "X64",
                Microsoft.Win32.RegistryView.Registry64);
            CollectArpPackages(packages, seen, Microsoft.Win32.RegistryHive.LocalMachine, "Machine", "X86",
                Microsoft.Win32.RegistryView.Registry32);
            CollectAppModelPackages(packages, seen, Microsoft.Win32.RegistryHive.LocalMachine, "Machine",
                Microsoft.Win32.RegistryView.Registry64);
        }

        if (user)
        {
            CollectArpPackages(packages, seen, Microsoft.Win32.RegistryHive.CurrentUser, "User", "X64",
                Microsoft.Win32.RegistryView.Registry64);
            CollectAppModelPackages(packages, seen, Microsoft.Win32.RegistryHive.CurrentUser, "User",
                Microsoft.Win32.RegistryView.Registry64);
        }

        return packages;
    }

    [SupportedOSPlatform("windows")]
    private static void CollectArpPackages(
        List<InstalledPackage> packages, HashSet<string> seen,
        Microsoft.Win32.RegistryHive hive, string scopeLabel, string archLabel,
        Microsoft.Win32.RegistryView view)
    {
        var arpPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var arpPath in arpPaths)
        {
            var effectiveArch = arpPath.Contains("WOW6432Node") ? "X86" : archLabel;
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(arpPath);
                if (uninstallKey is null) continue;

                foreach (var subkeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var subkey = uninstallKey.OpenSubKey(subkeyName);
                        if (subkey is null) continue;

                        var displayName = subkey.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        var systemComponent = subkey.GetValue("SystemComponent");
                        if (systemComponent is int sc && sc != 0) continue;

                        var installLocation = subkey.GetValue("InstallLocation") as string;
                        if (IsWindowsSystemPath(installLocation)) continue;

                        if (subkey.GetValue("ParentKeyName") is string)
                            continue;

                        var version = subkey.GetValue("DisplayVersion") as string ?? "Unknown";
                        var publisher = subkey.GetValue("Publisher") as string;
                        var packageFamilyName = subkey.GetValue("PackageFamilyName") as string;
                        var productCode = subkey.GetValue("ProductCode") as string;
                        var upgradeCode = subkey.GetValue("UpgradeCode") as string;

                        var localId = $@"ARP\{scopeLabel}\{effectiveArch}\{subkeyName}";
                        var installerCategory = localId.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) &&
                            subkey.GetValue("WindowsInstaller") is int windowsInstaller && windowsInstaller == 1
                                ? "msi"
                                : subkeyName.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)
                                    ? "msix"
                                    : "exe";

                        var dedupKey =
                            $"{localId}|{displayName.ToLowerInvariant()}|{version.ToLowerInvariant()}|{(publisher ?? "").ToLowerInvariant()}";
                        if (!seen.Add(dedupKey)) continue;

                        var packageFamilyNames = new List<string>();
                        if (!string.IsNullOrWhiteSpace(packageFamilyName))
                            packageFamilyNames.Add(packageFamilyName);

                        var productCodes = new List<string>();
                        if (!string.IsNullOrWhiteSpace(productCode))
                            productCodes.Add(productCode);
                        if (LooksLikeProductCode(subkeyName))
                            productCodes.Add(subkeyName.ToLowerInvariant());

                        var upgradeCodes = new List<string>();
                        if (!string.IsNullOrWhiteSpace(upgradeCode))
                            upgradeCodes.Add(upgradeCode);

                        packages.Add(new InstalledPackage
                        {
                            Name = displayName,
                            LocalId = localId,
                            InstalledVersion = version,
                            Publisher = publisher,
                            Scope = scopeLabel,
                            InstallerCategory = installerCategory,
                            InstallLocation = installLocation,
                            PackageFamilyNames = packageFamilyNames,
                            ProductCodes = productCodes,
                            UpgradeCodes = upgradeCodes,
                        });
                    }
                    catch { /* skip unreadable subkey */ }
                }
            }
            catch { /* skip unreadable hive path */ }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CollectAppModelPackages(
        List<InstalledPackage> packages, HashSet<string> seen,
        Microsoft.Win32.RegistryHive hive, string scopeLabel,
        Microsoft.Win32.RegistryView view)
    {
        const string appModelPackagesPath =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var appModelKey = baseKey.OpenSubKey(appModelPackagesPath);
            if (appModelKey is null) return;

            foreach (var subkeyName in appModelKey.GetSubKeyNames())
            {
                using var subkey = appModelKey.OpenSubKey(subkeyName);
                if (subkey is null)
                    continue;

                var displayName = subkey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                var installLocation = subkey.GetValue("PackageRootFolder") as string;
                if (IsWindowsSystemPath(installLocation))
                    continue;

                var parsed = ParseMsixFullName(subkeyName);
                if (parsed is null) continue;

                var localId = $@"MSIX\{subkeyName}";
                var dedupKey = $"{localId}|{displayName.ToLowerInvariant()}|{parsed.Value.Version.ToLowerInvariant()}";
                if (!seen.Add(dedupKey)) continue;

                packages.Add(new InstalledPackage
                {
                    Name = displayName,
                    LocalId = localId,
                    InstalledVersion = parsed.Value.Version,
                    Publisher = null,
                    Scope = scopeLabel,
                    InstallerCategory = "msix",
                    InstallLocation = installLocation,
                    PackageFamilyNames = [parsed.Value.FamilyName],
                });
            }
        }
        catch { /* AppModel registry not accessible */ }
    }

    private static bool IsWindowsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.Trim().StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeProductCode(string value) =>
        value.StartsWith('{') && value.EndsWith('}');

    private static (string Version, string FamilyName)? ParseMsixFullName(string fullName)
    {
        var parts = fullName.Split('_');
        if (parts.Length < 5) return null;

        var name = string.Join("_", parts.Take(parts.Length - 4));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var version = parts[^4].Trim();
        var resourceId = parts[^2].Trim();
        var publisherHash = parts[^1].Trim();
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(publisherHash))
            return null;

        var familyName = string.IsNullOrWhiteSpace(resourceId)
            ? $"{name}_{publisherHash}"
            : $"{name}_{resourceId}_{publisherHash}";

        return (version, familyName);
    }
}
