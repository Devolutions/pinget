using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Pinget.Core;

internal static class InstallerDispatch
{
    public static int Execute(string installerPath, string installerType, InstallRequest request, Manifest manifest, Installer installer)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Installing packages is only supported on Windows");

        return installerType.ToLowerInvariant() switch
        {
            "msi" or "wix" => RunMsi(installerPath, request, manifest, installer),
            "msix" or "appx" => RunMsix(installerPath),
            "zip" when ShouldDelegatePortableZipInstall(request, manifest, installer) => InstallPortableWithWinget(request, manifest),
            "zip" => ExtractZip(installerPath),
            _ => RunExe(installerPath, installerType, request, manifest, installer)
        };
    }

    [SupportedOSPlatform("windows")]
    public static int Uninstall(ListMatch installed, UninstallRequest request)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Uninstalling packages is only supported on Windows");

        if (request.Purge && request.Preserve)
            throw new InvalidOperationException("--purge and --preserve cannot be used together.");

        if (string.Equals(installed.InstallerCategory, "portable", StringComparison.OrdinalIgnoreCase))
            return UninstallPortable(installed, request);

        if ((request.Purge || request.Preserve) && !request.Force)
            throw new InvalidOperationException("--purge and --preserve are currently only supported for portable packages.");

        if (TryUninstallArp(installed, request, out var exitCode))
            return exitCode;

        if (TryUninstallMsix(installed, out exitCode))
            return exitCode;

        throw new InvalidOperationException($"No uninstall command found for installed package '{installed.Name}' ({installed.LocalId})");
    }

    [SupportedOSPlatform("windows")]
    private static bool TryUninstallArp(ListMatch installed, UninstallRequest request, out int exitCode)
    {
        exitCode = 0;

        var hives = new[]
        {
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64),
        };

        var arpPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var (hive, view) in hives)
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
            foreach (var arpPath in arpPaths)
            {
                using var key = baseKey.OpenSubKey(arpPath);
                if (key is null) continue;

                foreach (var subkeyName in key.GetSubKeyNames())
                {
                    using var subkey = key.OpenSubKey(subkeyName);
                    if (subkey is null) continue;

                    var displayName = subkey.GetValue("DisplayName") as string ?? "";
                    var productCode = subkey.GetValue("ProductCode") as string;
                    if (!RegistryEntryMatchesInstalledPackage(subkeyName, displayName, productCode, installed))
                        continue;

                    if (TryRunMsiUninstall(installed, subkeyName, productCode, request, out exitCode))
                        return true;

                    var quietUninstallCmd = subkey.GetValue("QuietUninstallString") as string;
                    var uninstallCmd = (request.Mode == InstallerMode.Interactive
                        ? subkey.GetValue("UninstallString") as string
                        : quietUninstallCmd ?? subkey.GetValue("UninstallString") as string)
                        ?? throw new InvalidOperationException("No uninstall command found in registry");

                    var psi = new ProcessStartInfo("cmd", $"/C {BuildUninstallCommand(uninstallCmd, request.Mode, quietUninstallCmd is not null, request.LogPath)}")
                    {
                        UseShellExecute = false,
                    };

                    using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start uninstaller");
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                    return true;
                }
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryUninstallMsix(ListMatch installed, out int exitCode)
    {
        exitCode = 0;

        if (!installed.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) &&
            installed.PackageFamilyNames.Count == 0)
            return false;

        var localFullName = installed.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)
            ? installed.LocalId[@"MSIX\".Length..]
            : null;
        var msixPsi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{BuildMsixUninstallScript(localFullName, installed.PackageFamilyNames)}\"")
        {
            UseShellExecute = false,
        };
        using var msixProc = Process.Start(msixPsi) ?? throw new InvalidOperationException("Failed to start Remove-AppxPackage");
        msixProc.WaitForExit();
        exitCode = msixProc.ExitCode;
        return true;
    }

    private static int RunMsi(string path, InstallRequest request, Manifest manifest, Installer installer)
    {
        var psi = new ProcessStartInfo("msiexec") { UseShellExecute = false };
        foreach (var arg in BuildArguments("msi", request, manifest, installer, path))
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run msiexec");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int RunMsix(string path)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"Add-AppxPackage -Path '{path}'\"")
        {
            UseShellExecute = false
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run Add-AppxPackage");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int ExtractZip(string path)
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(path, target, overwriteFiles: true);
        return 0;
    }

    private static bool ShouldDelegatePortableZipInstall(InstallRequest request, Manifest manifest, Installer installer) =>
        installer.Commands.Count > 0 &&
        !string.IsNullOrWhiteSpace(request.Query.Id ?? manifest.Id);

    internal static List<string> BuildWingetPortableInstallArguments(InstallRequest request, Manifest manifest)
    {
        var packageId = request.Query.Id ?? manifest.Id;
        var args = new List<string>
        {
            "install",
            "--id",
            packageId,
            "--exact",
            "--accept-source-agreements",
            "--disable-interactivity",
        };

        if (!string.IsNullOrWhiteSpace(request.Query.Source))
        {
            args.Add("--source");
            args.Add(request.Query.Source!);
        }

        if (!string.IsNullOrWhiteSpace(request.Query.Version))
        {
            args.Add("--version");
            args.Add(request.Query.Version!);
        }

        if (!string.IsNullOrWhiteSpace(request.Query.InstallScope))
        {
            args.Add("--scope");
            args.Add(request.Query.InstallScope!);
        }

        if (request.AcceptPackageAgreements)
            args.Add("--accept-package-agreements");

        if (request.Mode == InstallerMode.Silent || request.Mode == InstallerMode.SilentWithProgress)
            args.Add("--silent");

        return args;
    }

    private static int InstallPortableWithWinget(InstallRequest request, Manifest manifest)
    {
        var psi = new ProcessStartInfo("winget") { UseShellExecute = false };
        foreach (var arg in BuildWingetPortableInstallArguments(request, manifest))
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start winget for portable install");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int RunExe(string path, string installerType, InstallRequest request, Manifest manifest, Installer installer)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = false };
        foreach (var arg in BuildArguments(installerType, request, manifest, installer, path))
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run installer");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    internal static List<string> BuildArguments(string installerType, InstallerMode mode, Installer installer)
        => BuildArguments(
            installerType,
            new InstallRequest { Query = new PackageQuery(), Mode = mode },
            new Manifest { Id = "Test.Package", Name = "Test Package", Version = "1.0.0" },
            installer);

    internal static List<string> BuildArguments(string installerType, InstallRequest request, Manifest manifest, Installer installer, string? installerPath = null)
    {
        if (!string.IsNullOrWhiteSpace(request.Override))
            return SplitArguments(request.Override!);

        var normalizedType = installerType.ToLowerInvariant();
        var args = new List<string>();

        if (normalizedType is "msi" or "wix")
        {
            args.Add("/i");
            args.Add(installerPath ?? throw new InvalidOperationException("Installer path is required for MSI arguments."));
        }

        var experienceSwitch = request.Mode switch
        {
            InstallerMode.Interactive => installer.Switches.Interactive,
            InstallerMode.SilentWithProgress => installer.Switches.SilentWithProgress ?? installer.Switches.Silent,
            InstallerMode.Silent => installer.Switches.Silent ?? installer.Switches.SilentWithProgress,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(experienceSwitch))
            experienceSwitch = DefaultExperienceSwitch(normalizedType, request.Mode);

        AppendSwitch(args, experienceSwitch);
        AppendSwitch(args, ResolveTemplate(installer.Switches.Log, DefaultLogSwitch(normalizedType), request.LogPath));
        AppendSwitch(args, installer.Switches.Custom);
        AppendSwitch(args, request.Custom);
        AppendSwitch(args, ResolveTemplate(installer.Switches.InstallLocation, DefaultInstallLocationSwitch(normalizedType), request.InstallLocation));

        return args;
    }

    private static List<string> SplitArguments(string value)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in value)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }

    internal static string? GetArpSubkeyName(string localId)
    {
        if (!localId.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = localId.Split('\\', 4);
        return parts.Length == 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3] : null;
    }

    internal static bool RegistryEntryMatchesInstalledPackage(
        string subkeyName,
        string displayName,
        string? productCode,
        ListMatch installed)
    {
        var arpSubkeyName = GetArpSubkeyName(installed.LocalId);
        if (!string.IsNullOrWhiteSpace(arpSubkeyName) &&
            subkeyName.Equals(arpSubkeyName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (installed.ProductCodes.Any(code => code.Equals(subkeyName, StringComparison.OrdinalIgnoreCase) ||
                                               (!string.IsNullOrWhiteSpace(productCode) &&
                                                code.Equals(productCode, StringComparison.OrdinalIgnoreCase))))
            return true;

        return displayName.Equals(installed.Name, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildUninstallCommand(string uninstallCommand, bool silent, bool hasQuietUninstallCommand)
        => BuildUninstallCommand(uninstallCommand, silent ? InstallerMode.Silent : InstallerMode.Interactive, hasQuietUninstallCommand, null);

    internal static string BuildUninstallCommand(string uninstallCommand, InstallerMode mode, bool hasQuietUninstallCommand, string? logPath)
    {
        var command = PopulateTemplate(uninstallCommand, logPath, null);

        if (mode == InstallerMode.Interactive || hasQuietUninstallCommand)
            return command;

        if (command.Contains("winget uninstall", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("winget.exe uninstall", StringComparison.OrdinalIgnoreCase))
            return command;

        if (command.Contains("/quiet", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("/passive", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("/verysilent", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("/silent", StringComparison.OrdinalIgnoreCase) ||
            command.Contains(" /s", StringComparison.OrdinalIgnoreCase))
            return command;

        return $"{command} /S";
    }

    internal static string BuildMsixUninstallScript(string? packageFullName, IReadOnlyList<string> packageFamilyNames)
    {
        var fullNameLiteral = packageFullName is null ? "$null" : $"'{packageFullName.Replace("'", "''")}'";
        var familyArray = packageFamilyNames.Count == 0
            ? "@()"
            : "@(" + string.Join(", ", packageFamilyNames.Select(name => $"'{name.Replace("'", "''")}'")) + ")";

        return "$fullName = " + fullNameLiteral + "; " +
               "$familyNames = " + familyArray + "; " +
               "$targets = Get-AppxPackage | Where-Object { " +
               "(($fullName -ne $null) -and $_.PackageFullName -eq $fullName) -or " +
               "($familyNames.Count -gt 0 -and ($familyNames -contains $_.PackageFamilyName)) }; " +
               "if (-not $targets) { exit 1 }; " +
               "$targets | Remove-AppxPackage";
    }

    private static bool TryRunMsiUninstall(ListMatch installed, string subkeyName, string? productCode, UninstallRequest request, out int exitCode)
    {
        exitCode = 0;
        var uninstallCode = installed.ProductCodes
            .Concat([productCode, subkeyName])
            .FirstOrDefault(IsProductCodeLike);
        if (string.IsNullOrWhiteSpace(uninstallCode))
            return false;

        var psi = new ProcessStartInfo("msiexec") { UseShellExecute = false };
        foreach (var arg in BuildMsiUninstallArguments(uninstallCode!, request))
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run msiexec uninstall");
        proc.WaitForExit();
        exitCode = proc.ExitCode;
        return true;
    }

    private static List<string> BuildMsiUninstallArguments(string productCode, UninstallRequest request)
    {
        var args = new List<string> { "/x", productCode };
        switch (request.Mode)
        {
            case InstallerMode.Silent:
                args.Add("/quiet");
                args.Add("/norestart");
                break;
            case InstallerMode.SilentWithProgress:
                args.Add("/passive");
                args.Add("/norestart");
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.LogPath))
        {
            args.Add("/log");
            args.Add(request.LogPath!);
        }

        return args;
    }

    private static int UninstallPortable(ListMatch installed, UninstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(installed.InstallLocation))
        {
            if (request.Force)
                return 0;
            throw new InvalidOperationException($"Portable package '{installed.Name}' does not expose an install location.");
        }

        if (request.Preserve)
            return 0;

        if (Directory.Exists(installed.InstallLocation))
        {
            Directory.Delete(installed.InstallLocation, recursive: true);
            return 0;
        }

        if (File.Exists(installed.InstallLocation))
        {
            File.Delete(installed.InstallLocation);
            return 0;
        }

        if (request.Force)
            return 0;

        throw new InvalidOperationException($"Portable package location not found: {installed.InstallLocation}");
    }

    private static string? DefaultExperienceSwitch(string installerType, InstallerMode mode) => mode switch
    {
        InstallerMode.Interactive => null,
        InstallerMode.SilentWithProgress => installerType switch
        {
            "inno" => "/SP- /SILENT /SUPPRESSMSGBOXES /NORESTART",
            "burn" or "wix" or "msi" => "/passive /norestart",
            "nullsoft" or "nsis" => "/S",
            _ => "/SILENT",
        },
        InstallerMode.Silent => installerType switch
        {
            "inno" => "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            "burn" or "wix" or "msi" => "/quiet /norestart",
            "nullsoft" or "nsis" => "/S",
            _ => "/S",
        },
        _ => null,
    };

    private static string? DefaultLogSwitch(string installerType) => installerType switch
    {
        "burn" or "wix" or "msi" => "/log \"<LOGPATH>\"",
        "inno" => "/LOG=\"<LOGPATH>\"",
        _ => null,
    };

    private static string? DefaultInstallLocationSwitch(string installerType) => installerType switch
    {
        "burn" or "wix" or "msi" => "TARGETDIR=\"<INSTALLPATH>\"",
        "nullsoft" or "nsis" => "/D=<INSTALLPATH>",
        "inno" => "/DIR=\"<INSTALLPATH>\"",
        _ => null,
    };

    private static string? ResolveTemplate(string? manifestValue, string? fallback, string? replacementValue)
    {
        var template = !string.IsNullOrWhiteSpace(manifestValue) ? manifestValue : fallback;
        if (string.IsNullOrWhiteSpace(template))
            return null;

        if (template.Contains("<LOGPATH>", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(replacementValue))
            return null;
        if (template.Contains("<INSTALLPATH>", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(replacementValue))
            return null;

        return PopulateTemplate(template, replacementValue, replacementValue);
    }

    private static string PopulateTemplate(string template, string? logPath, string? installPath)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
            template = template.Replace("<LOGPATH>", logPath, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(installPath))
            template = template.Replace("<INSTALLPATH>", installPath, StringComparison.OrdinalIgnoreCase);
        return template;
    }

    private static void AppendSwitch(List<string> args, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            args.AddRange(SplitArguments(value));
    }

    private static bool IsProductCodeLike(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith("{", StringComparison.Ordinal) &&
           value.EndsWith("}", StringComparison.Ordinal);
}
