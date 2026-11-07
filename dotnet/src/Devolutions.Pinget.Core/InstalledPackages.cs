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

        // MSI registers UpgradeCode → ProductCode mappings under
        // HKLM\SOFTWARE\Classes\Installer\UpgradeCodes (per-machine) and
        // HKCU\Software\Microsoft\Installer\UpgradeCodes (per-user). Most
        // ARP entries don't expose UpgradeCode directly, so winget reads it
        // from here. Without it, packages like OpenJS.NodeJS.22 (correlated
        // only via UpgradeCode in the v2 index) fall back to a sibling
        // correlation (e.g. OpenJS.NodeJS.LTS) that manufactures a spurious
        // upgrade.
        var upgradeCodes = CollectMsiUpgradeCodes(machine, user);

        if (machine)
        {
            CollectArpPackages(packages, seen, upgradeCodes, Microsoft.Win32.RegistryHive.LocalMachine, "Machine", "X64",
                Microsoft.Win32.RegistryView.Registry64);
            CollectArpPackages(packages, seen, upgradeCodes, Microsoft.Win32.RegistryHive.LocalMachine, "Machine", "X86",
                Microsoft.Win32.RegistryView.Registry32);
            CollectAppModelPackages(packages, seen, Microsoft.Win32.RegistryHive.LocalMachine, "Machine",
                Microsoft.Win32.RegistryView.Registry64);
        }

        if (user)
        {
            CollectArpPackages(packages, seen, upgradeCodes, Microsoft.Win32.RegistryHive.CurrentUser, "User", "X64",
                Microsoft.Win32.RegistryView.Registry64);
            CollectAppModelPackages(packages, seen, Microsoft.Win32.RegistryHive.CurrentUser, "User",
                Microsoft.Win32.RegistryView.Registry64);
        }

        return packages;
    }

    /// <summary>
    /// Builds a ProductCode → UpgradeCode map (both in standard
    /// {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} lowercase format) by walking
    /// the MSI Installer registry. Subkeys under Installer\UpgradeCodes are
    /// named by the flipped UpgradeCode GUID, and each subkey's value names
    /// are the flipped ProductCodes registered under that UpgradeCode.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string> CollectMsiUpgradeCodes(bool includeMachine, bool includeUser)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (includeMachine)
        {
            CollectMsiUpgradeCodesFrom(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Classes\Installer\UpgradeCodes",
                Microsoft.Win32.RegistryView.Registry64,
                map);
        }
        if (includeUser)
        {
            CollectMsiUpgradeCodesFrom(
                Microsoft.Win32.RegistryHive.CurrentUser,
                @"Software\Microsoft\Installer\UpgradeCodes",
                Microsoft.Win32.RegistryView.Registry64,
                map);
        }
        return map;
    }

    [SupportedOSPlatform("windows")]
    private static void CollectMsiUpgradeCodesFrom(
        Microsoft.Win32.RegistryHive hive, string path, Microsoft.Win32.RegistryView view,
        Dictionary<string, string> map)
    {
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(path);
            if (root is null) return;
            foreach (var keyName in root.GetSubKeyNames())
            {
                var upgradeCode = UnflipPackedGuid(keyName);
                if (upgradeCode is null) continue;
                using var subkey = root.OpenSubKey(keyName);
                if (subkey is null) continue;
                foreach (var valueName in subkey.GetValueNames())
                {
                    var productCode = UnflipPackedGuid(valueName);
                    if (productCode is null) continue;
                    // First mapping wins — keeps the map deterministic if
                    // the same ProductCode is registered under multiple
                    // UpgradeCodes (rare, but possible for repackaged MSIs).
                    map.TryAdd(productCode, upgradeCode);
                }
            }
        }
        catch
        {
            // Installer hive missing or unreadable; identity correlation
            // falls back to ProductCode-only and the harness will flag a
            // mismatch if it matters.
        }
    }

    /// <summary>
    /// Converts the 32-character "packed GUID" used inside the MSI Installer
    /// registry hive back to the standard
    /// {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx} lowercase form. The packing
    /// reverses each of the 11 chunks (sized 8/4/4 then eight 2-char byte
    /// pairs) of the GUID's hex representation.
    /// </summary>
    internal static string? UnflipPackedGuid(string packed)
    {
        if (packed.Length != 32) return null;
        foreach (var c in packed)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        int[] chunks = [8, 4, 4, 2, 2, 2, 2, 2, 2, 2, 2];
        var sb = new System.Text.StringBuilder(32);
        int offset = 0;
        foreach (var size in chunks)
        {
            for (int i = size - 1; i >= 0; i--)
                sb.Append(packed[offset + i]);
            offset += size;
        }
        var r = sb.ToString().ToLowerInvariant();
        return $"{{{r[..8]}-{r[8..12]}-{r[12..16]}-{r[16..20]}-{r[20..32]}}}";
    }

    [SupportedOSPlatform("windows")]
    private static void CollectArpPackages(
        List<InstalledPackage> packages, HashSet<string> seen,
        Dictionary<string, string> upgradeCodeMap,
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

                        var displayName = ReadString(subkey, "DisplayName");
                        if (displayName is null) continue;

                        var systemComponent = subkey.GetValue("SystemComponent");
                        if (systemComponent is int sc && sc != 0) continue;

                        // WinGet's ARPHelper has no install-location filter: entries under the Windows
                        // directory (printer drivers and the like) are listed too. Only inbox MSIX
                        // packages are filtered by location.
                        var installLocation = ReadString(subkey, "InstallLocation");

                        if (ReadString(subkey, "ParentKeyName") is not null)
                            continue;

                        var version = DetermineArpVersion(
                            ReadString(subkey, "DisplayVersion"),
                            ReadDword(subkey, "VersionMajor"),
                            ReadDword(subkey, "VersionMinor"),
                            ReadDword(subkey, "MajorVersion"),
                            ReadDword(subkey, "MinorVersion"),
                            ReadDword(subkey, "Version"));
                        var publisher = ReadString(subkey, "Publisher");
                        var packageFamilyName = ReadString(subkey, "PackageFamilyName");
                        var productCode = ReadString(subkey, "ProductCode");
                        var upgradeCode = ReadString(subkey, "UpgradeCode");
                        var wingetPackageIdentifier = ReadString(subkey, "WinGetPackageIdentifier");
                        var wingetSourceIdentifier = ReadString(subkey, "WinGetSourceIdentifier");

                        var localId = $@"ARP\{scopeLabel}\{effectiveArch}\{subkeyName}";
                        // Honor the WinGet ARP signal so uninstall flows through the
                        // portable uninstall path (clean dir + registry removal) instead
                        // of delegating the UninstallString to a winget subprocess that
                        // only fully cleans up entries winget itself installed.
                        var wingetInstallerType = ReadString(subkey, "WinGetInstallerType");
                        var installerCategory =
                            wingetInstallerType is { } wit && wit.Equals("portable", StringComparison.OrdinalIgnoreCase)
                                ? "portable"
                                : localId.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase) &&
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
                        if (!productCodes.Any(code => code.Equals(subkeyName, StringComparison.OrdinalIgnoreCase)))
                            productCodes.Add(subkeyName.ToLowerInvariant());

                        var upgradeCodes = new List<string>();
                        if (!string.IsNullOrWhiteSpace(upgradeCode))
                            upgradeCodes.Add(upgradeCode);
                        if (upgradeCodes.Count == 0)
                        {
                            // ARP rarely exposes UpgradeCode directly. Recover
                            // it from the MSI Installer registry hive so
                            // identity correlation can fall back to
                            // upgradecodes2 when productcodes2 picks the wrong
                            // sibling (Node.js 24.x being attributed to
                            // OpenJS.NodeJS.LTS rather than OpenJS.NodeJS.22).
                            foreach (var code in productCodes)
                            {
                                if (upgradeCodeMap.TryGetValue(code, out var uc))
                                {
                                    upgradeCodes.Add(uc);
                                    break;
                                }
                            }
                        }

                        packages.Add(new InstalledPackage
                        {
                            Name = displayName,
                            LocalId = localId,
                            InstalledVersion = version,
                            Publisher = publisher,
                            Scope = scopeLabel,
                            InstallerCategory = installerCategory,
                            InstallLocation = installLocation,
                            WinGetPackageIdentifier = wingetPackageIdentifier,
                            WinGetSourceIdentifier = wingetSourceIdentifier,
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
                try
                {
                    using var subkey = appModelKey.OpenSubKey(subkeyName);
                    if (subkey is null)
                        continue;

                    var installLocation = ReadString(subkey, "PackageRootFolder");
                    if (IsWindowsSystemPath(installLocation))
                        continue;

                    var parsed = ParseMsixFullName(subkeyName);
                    if (parsed is null) continue;
                    if (IsMsixSplitResourcePackage(parsed.Value.ResourceId)) continue;

                    var displayName = ReadString(subkey, "DisplayName") ?? GetAppModelDisplayName(subkey);
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

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
                catch { /* skip unreadable package key */ }
            }
        }
        catch { /* AppModel registry not accessible */ }
    }

    /// <summary>Whether <paramref name="path"/> is inside the Windows directory (%SystemRoot%), where inbox MSIX packages live.</summary>
    private static bool IsWindowsSystemPath(string? path) =>
        IsPathUnderWindowsDirectory(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>
    /// Case-insensitive prefix test of <paramref name="path"/> against <paramref name="windowsDirectory"/> plus a
    /// trailing separator, so C:\WindowsApps is not treated as C:\Windows.
    /// </summary>
    internal static bool IsPathUnderWindowsDirectory(string? path, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        var prefix = windowsDirectory.Trim().TrimEnd('\\', '/') + "\\";
        if (prefix.Length == 1) return false;
        return path.Trim().Replace('/', '\\').StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mirrors WinGet's ARPHelper::DetermineVersion: DisplayVersion, then VersionMajor.VersionMinor, then
    /// MajorVersion.MinorVersion (when either part is nonzero), then the packed Version DWORD, then "Unknown".
    /// DWORD arguments are null when the value is missing or not a DWORD.
    /// </summary>
    internal static string DetermineArpVersion(
        string? displayVersion,
        uint? versionMajor,
        uint? versionMinor,
        uint? majorVersion,
        uint? minorVersion,
        uint? version)
    {
        if (!string.IsNullOrEmpty(displayVersion))
            return displayVersion;

        foreach (var (major, minor) in new[] { (versionMajor, versionMinor), (majorVersion, minorVersion) })
        {
            var majorValue = major ?? 0;
            var minorValue = minor ?? 0;
            if (majorValue != 0 || minorValue != 0)
                return $"{majorValue}.{minorValue}";
        }

        if (version is uint packed && packed != 0)
            return $"{packed >> 24}.{(packed >> 16) & 0xFF}.{packed & 0xFFFF}";

        return "Unknown";
    }

    [SupportedOSPlatform("windows")]
    private static uint? ReadDword(Microsoft.Win32.RegistryKey key, string name) =>
        // REG_DWORD values come back as int; other kinds (string, QWORD, binary) do not count.
        key.GetValue(name) is int value ? unchecked((uint)value) : null;

    [SupportedOSPlatform("windows")]
    private static string? GetAppModelDisplayName(Microsoft.Win32.RegistryKey packageKey)
    {
        using var appKey = packageKey.OpenSubKey(@"App\Capabilities");
        if (appKey is null) return null;

        return ReadString(appKey, "ApplicationDescription") ?? ReadString(appKey, "ApplicationName");
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadString(Microsoft.Win32.RegistryKey key, string name) =>
        NormalizeRegistryString(key.GetValue(name));

    /// <summary>Registry strings are trimmed and empty values treated as missing (matches the Rust read_reg_string).</summary>
    internal static string? NormalizeRegistryString(object? value) =>
        value is string text && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    internal static bool IsMsixSplitResourcePackage(string? resourceId) =>
        !string.IsNullOrWhiteSpace(resourceId)
        && resourceId.StartsWith("split.", StringComparison.OrdinalIgnoreCase);

    internal static MsixPackageFullName? ParseMsixFullName(string fullName)
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

        // The package family name is always Name_PublisherId; the resource id only appears in the full name.
        var familyName = $"{name}_{publisherHash}";

        return new MsixPackageFullName(version, resourceId, familyName);
    }

    internal readonly record struct MsixPackageFullName(string Version, string ResourceId, string FamilyName);
}
