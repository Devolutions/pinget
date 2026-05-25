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
            "zip" when IsPortableZipInstaller(installer) => InstallPortable(installerPath, request, manifest, installer),
            "portable" => InstallPortable(installerPath, request, manifest, installer),
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

    internal static bool IsPortableZipInstaller(Installer installer) =>
        string.Equals(installer.NestedInstallerType, "portable", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs (or upgrades) a portable WinGet package natively. Mirrors the
    /// Rust install_portable behavior so the C# and Rust pinget implementations
    /// stay aligned (see AGENTS.md).
    ///
    /// Handles both <c>InstallerType: portable</c> (standalone binary) and
    /// <c>InstallerType: zip</c> with <c>NestedInstallerType: portable</c> (the
    /// shape every portable in the community winget catalog uses).
    ///
    /// Behavior follows winget's portable workflow closely enough that the
    /// resulting HKCU ARP entry is recognized by <c>winget list</c> and by
    /// pinget's own list view:
    /// <list type="bullet">
    ///   <item>Resolves the target install directory in this priority order:
    ///   <c>request.InstallLocation</c>, existing <c>InstallLocation</c> from
    ///   the registry ARP entry (so upgrades preserve a user's custom path),
    ///   then WinGet's default user portable root.</item>
    ///   <item>If an existing pinget-owned install lives in that directory
    ///   (<c>InstallDirectoryCreated=1</c>), cleans its contents before
    ///   extracting the new version.</item>
    ///   <item>Extracts the zip (or copies the standalone binary), then writes
    ///   the ARP registry entry both <c>winget list</c> and pinget read from.</item>
    /// </list>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int InstallPortable(string installerPath, InstallRequest request, Manifest manifest, Installer installer)
    {
        var sourceIdentifier = PortableSourceIdentifier(request.Query);
        var existingEntry = ReadExistingPortableEntry(manifest.Id);
        // Reuse the existing subkey name on upgrade so we don't orphan the prior
        // entry by writing to a different one.
        var subkeyName = existingEntry?.SubkeyName ?? PortableSubkeyName(manifest.Id, sourceIdentifier);

        var targetDir = ResolvePortableInstallLocation(request, existingEntry, subkeyName);

        // If we created this directory on a previous install, wipe it clean so the
        // new version doesn't co-exist with leftovers from the previous one. We
        // do not touch a directory we don't own (InstallDirectoryCreated != 1).
        var prevDirCreated = existingEntry?.InstallDirectoryCreated == 1;
        var dirExisted = Directory.Exists(targetDir);
        if (prevDirCreated && dirExisted)
            CleanDirectoryContents(targetDir);

        Directory.CreateDirectory(targetDir);
        // "We created it" is sticky across upgrades: if a previous install
        // marked it created, it still counts as created by us even when the
        // dir already existed this round.
        var installDirectoryCreated = !dirExisted || prevDirCreated;

        var installerType = installer.InstallerType ?? "";
        string portableTargetFullPath;
        if (string.Equals(installerType, "zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(installerPath, targetDir, overwriteFiles: true);
            // For nested-portable zips the binary lives at the RelativeFilePath
            // the manifest declares. Record the first one as
            // PortableTargetFullPath so winget's portable uninstall workflow can
            // identify which file to remove.
            portableTargetFullPath = installer.NestedInstallerFiles.Count > 0
                ? Path.Combine(targetDir, installer.NestedInstallerFiles[0].RelativeFilePath)
                : targetDir;
        }
        else
        {
            // Standalone portable: copy the downloaded file into targetDir using
            // its original filename. winget would optionally rename to the
            // PortableCommandAlias; pinget keeps the original name for now.
            var basename = Path.GetFileName(installerPath);
            if (string.IsNullOrWhiteSpace(basename))
                throw new InvalidOperationException("portable installer has no filename");
            portableTargetFullPath = Path.Combine(targetDir, basename);
            File.Copy(installerPath, portableTargetFullPath, overwrite: true);
        }

        // Create the Links\<alias>.exe shim so users can invoke the portable from
        // any shell, matching winget's portable workflow. Failures are non-fatal —
        // when the user lacks SeCreateSymbolicLink (no Developer Mode, not admin)
        // we still leave the package installed at install_location.
        var alias = DeterminePortableAlias(installer);
        var symlinkFullPath = string.IsNullOrEmpty(alias)
            ? null
            : TryCreatePortableSymlink(portableTargetFullPath, alias!);

        // Ensure %LOCALAPPDATA%\Microsoft\WinGet\Links is on user PATH so the
        // symlink we just created is resolvable from new shells. Tracks whether
        // we added it this round so uninstall can decide whether to take it
        // back out.
        var addedToPath = symlinkFullPath is not null && TryAddLinksDirToUserPath();

        WritePortableArpEntry(new PortableArpEntry(
            SubkeyName: subkeyName,
            InstallLocation: targetDir,
            PortableTargetFullPath: portableTargetFullPath,
            PortableSymlinkFullPath: symlinkFullPath,
            InstallDirectoryCreated: installDirectoryCreated,
            AddedToPath: addedToPath,
            SourceIdentifier: sourceIdentifier,
            Manifest: manifest));
        return 0;
    }

    private sealed record PortableArpEntry(
        string SubkeyName,
        string InstallLocation,
        string PortableTargetFullPath,
        string? PortableSymlinkFullPath,
        bool InstallDirectoryCreated,
        bool AddedToPath,
        string SourceIdentifier,
        Manifest Manifest);

    /// <summary>
    /// Picks the portable command alias from the manifest. Mirrors winget's
    /// resolution: nested-installer files first (used by zip+portable
    /// manifests), then the top-level <c>Commands</c> field (used by
    /// standalone portable manifests). Returns null when the manifest doesn't
    /// declare a command — the package still installs, just without a Links\
    /// shim.
    /// </summary>
    private static string? DeterminePortableAlias(Installer installer)
    {
        foreach (var file in installer.NestedInstallerFiles)
        {
            if (!string.IsNullOrEmpty(file.PortableCommandAlias))
                return file.PortableCommandAlias;
        }
        return installer.Commands.Count > 0 ? installer.Commands[0] : null;
    }

    private static string WingetLinksDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet", "Links");

    /// <summary>
    /// Creates (or replaces) <c>Links\&lt;alias&gt;.exe</c> as a symlink
    /// pointing at the portable's binary. Returns the link path on success.
    /// Returns null on any I/O failure (notably ERROR_PRIVILEGE_NOT_HELD when
    /// the user lacks SeCreateSymbolicLink) so the install can still
    /// complete without the shim.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryCreatePortableSymlink(string target, string alias)
    {
        try
        {
            var linksDir = WingetLinksDir();
            Directory.CreateDirectory(linksDir);
            var linkPath = Path.Combine(linksDir, $"{alias}.exe");

            // Replace any prior link/file at the path so upgrades repoint to
            // the new binary instead of leaving a stale symlink behind.
            if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget is not null)
            {
                try { File.Delete(linkPath); } catch { /* ignore */ }
            }

            File.CreateSymbolicLink(linkPath, target);
            return linkPath;
        }
        catch
        {
            // Symlink creation requires Developer Mode or admin elevation.
            // Treat any failure as non-fatal — the package files are already in
            // place, just without a Links\ shim.
            return null;
        }
    }

    /// <summary>
    /// Adds <c>Links\</c> to the user's <c>HKCU\Environment\Path</c> if not
    /// already present, then broadcasts WM_SETTINGCHANGE so explorer-spawned
    /// shells pick up the new PATH without a logoff. Returns true if PATH was
    /// actually appended this call.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryAddLinksDirToUserPath()
    {
        try
        {
            var linksDir = WingetLinksDir();
            using var envKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (envKey is null) return false;

            var existing = envKey.GetValue("Path") as string ?? "";
            var normalizedLinks = linksDir.ToLowerInvariant();
            var alreadyPresent = existing
                .Split(';')
                .Any(c => c.Trim().Equals(normalizedLinks, StringComparison.OrdinalIgnoreCase));
            if (alreadyPresent) return false;

            string newPath;
            if (existing.Length == 0)
                newPath = linksDir;
            else if (existing.EndsWith(';'))
                newPath = existing + linksDir;
            else
                newPath = existing + ";" + linksDir;

            envKey.SetValue("Path", newPath, Microsoft.Win32.RegistryValueKind.ExpandString);
            BroadcastEnvironmentChange();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            const uint HWND_BROADCAST = 0xFFFF;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            _ = NativeMethods.SendMessageTimeoutW(
                new IntPtr(unchecked((int)HWND_BROADCAST)),
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                SMTO_ABORTIFHUNG,
                5000,
                out _);
        }
        catch { /* broadcast best-effort */ }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            string lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);
    }

    /// <summary>
    /// Source identifier embedded in the ARP subkey name. We mirror winget for
    /// the community repo so winget's UninstallString can resolve the subkey
    /// and <c>winget list</c> still finds the package; everything else uses
    /// the source name verbatim.
    /// </summary>
    private static string PortableSourceIdentifier(PackageQuery query)
    {
        var source = query.Source;
        if (string.IsNullOrEmpty(source) || string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase))
            return "Microsoft.Winget.Source_8wekyb3d8bbwe";
        return source;
    }

    private static string PortableSubkeyName(string packageId, string sourceIdentifier) =>
        $"{packageId}_{sourceIdentifier}";

    private sealed record ExistingPortableEntry(string SubkeyName, string? InstallLocation, int? InstallDirectoryCreated);

    [SupportedOSPlatform("windows")]
    private static ExistingPortableEntry? ReadExistingPortableEntry(string packageId)
    {
        using var uninstall = Microsoft.Win32.Registry.CurrentUser
            .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null) return null;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var subkey = uninstall.OpenSubKey(name);
            if (subkey is null) continue;

            if (!string.Equals(subkey.GetValue("WinGetPackageIdentifier") as string, packageId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(subkey.GetValue("WinGetInstallerType") as string, "portable", StringComparison.OrdinalIgnoreCase))
                continue;

            int? installDirectoryCreated = subkey.GetValue("InstallDirectoryCreated") switch
            {
                int i => i,
                _ => null,
            };
            return new ExistingPortableEntry(
                SubkeyName: name,
                InstallLocation: subkey.GetValue("InstallLocation") as string,
                InstallDirectoryCreated: installDirectoryCreated);
        }
        return null;
    }

    private static string ResolvePortableInstallLocation(InstallRequest request, ExistingPortableEntry? existing, string subkeyName)
    {
        if (!string.IsNullOrWhiteSpace(request.InstallLocation))
            return request.InstallLocation!;
        if (!string.IsNullOrWhiteSpace(existing?.InstallLocation))
            return existing!.InstallLocation!;
        return Path.Combine(DefaultUserPortableRoot(), subkeyName);
    }

    private static string DefaultUserPortableRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet", "Packages");

    private static void CleanDirectoryContents(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    /// <summary>
    /// Writes the HKCU ARP entry winget would normally write for a portable
    /// install. Only the subset that winget's portable list view and pinget's
    /// installed-package discovery actually read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WritePortableArpEntry(PortableArpEntry entry)
    {
        using var subkey = Microsoft.Win32.Registry.CurrentUser
            .CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{entry.SubkeyName}")
            ?? throw new InvalidOperationException("failed to create portable ARP registry subkey");

        var manifest = entry.Manifest;
        subkey.SetValue("WinGetPackageIdentifier", manifest.Id);
        subkey.SetValue("WinGetSourceIdentifier", entry.SourceIdentifier);
        subkey.SetValue("WinGetInstallerType", "portable");
        subkey.SetValue("InstallLocation", entry.InstallLocation);
        subkey.SetValue("PortableTargetFullPath", entry.PortableTargetFullPath);
        if (!string.IsNullOrEmpty(entry.PortableSymlinkFullPath))
            subkey.SetValue("PortableSymlinkFullPath", entry.PortableSymlinkFullPath);
        subkey.SetValue("InstallDirectoryAddedToPath", entry.AddedToPath ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        subkey.SetValue("InstallDirectoryCreated", entry.InstallDirectoryCreated ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        subkey.SetValue("DisplayName", string.IsNullOrEmpty(manifest.Name) ? manifest.Id : manifest.Name);
        subkey.SetValue("DisplayVersion", manifest.Version);
        if (!string.IsNullOrEmpty(manifest.Publisher))
            subkey.SetValue("Publisher", manifest.Publisher);
        subkey.SetValue("UninstallString", $"winget uninstall --product-code {entry.SubkeyName}");
        subkey.SetValue("InstallDate", DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(manifest.PackageUrl))
            subkey.SetValue("URLInfoAbout", manifest.PackageUrl);
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

    [SupportedOSPlatform("windows")]
    private static int UninstallPortable(ListMatch installed, UninstallRequest request)
    {
        // Read PortableSymlinkFullPath from the ARP entry *before* we touch the
        // registry, so we can clean up winget-created shims in Links\. Without
        // this, a portable that winget originally installed (with a
        // PortableCommandAlias set in its manifest) would leave a dangling
        // symlink in %LOCALAPPDATA%\Microsoft\WinGet\Links\ once pinget
        // uninstalls it.
        var knownSymlink = ReadPortableSymlinkFullPath(installed);

        if (string.IsNullOrWhiteSpace(installed.InstallLocation))
        {
            if (request.Force)
            {
                TryRemovePortableSymlinks(knownSymlink, null);
                TryRemovePortableRegistryEntry(installed);
                return 0;
            }
            throw new InvalidOperationException($"Portable package '{installed.Name}' does not expose an install location.");
        }

        if (request.Preserve)
            return 0;

        var removed = false;
        if (Directory.Exists(installed.InstallLocation))
        {
            Directory.Delete(installed.InstallLocation, recursive: true);
            removed = true;
        }
        else if (File.Exists(installed.InstallLocation))
        {
            File.Delete(installed.InstallLocation);
            removed = true;
        }

        if (removed || request.Force)
        {
            // Once the files are gone (or the user forced past missing files),
            // drop the symlink shim and ARP entry too so the package fully
            // disappears.
            TryRemovePortableSymlinks(knownSymlink, installed.InstallLocation);
            TryRemovePortableRegistryEntry(installed);
            return 0;
        }

        throw new InvalidOperationException($"Portable package location not found: {installed.InstallLocation}");
    }

    /// <summary>
    /// Reads <c>PortableSymlinkFullPath</c> from the ARP subkey backing
    /// <paramref name="installed"/> when present. winget writes this when it
    /// created a <c>Links\&lt;alias&gt;.exe</c> shim for a portable; pinget's
    /// own install_portable doesn't currently create shims, so for
    /// pinget-installed entries this returns null.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadPortableSymlinkFullPath(ListMatch installed)
    {
        const string ArpPrefix = @"ARP\";
        if (!installed.LocalId.StartsWith(ArpPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = installed.LocalId[ArpPrefix.Length..];
        var parts = rest.Split('\\', 3);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]))
            return null;
        var hive = parts[0].Equals("User", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.Win32.Registry.CurrentUser
            : Microsoft.Win32.Registry.LocalMachine;
        try
        {
            using var subkey = hive.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{parts[2]}");
            return subkey?.GetValue("PortableSymlinkFullPath") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Removes the symlink shims winget would have created for this portable.
    /// Tries (1) the exact path from <c>PortableSymlinkFullPath</c> if known,
    /// and (2) any symlink in <c>%LOCALAPPDATA%\Microsoft\WinGet\Links\</c>
    /// whose stored target resolves under the install location. Best-effort:
    /// failures are swallowed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TryRemovePortableSymlinks(string? knownSymlink, string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(knownSymlink))
        {
            try { File.Delete(knownSymlink!); } catch { }
        }

        if (string.IsNullOrWhiteSpace(installLocation))
            return;

        try
        {
            var linksRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links");
            if (!Directory.Exists(linksRoot))
                return;
            var installPrefix = installLocation.ToLowerInvariant();
            foreach (var entry in Directory.EnumerateFiles(linksRoot))
            {
                try
                {
                    var info = new FileInfo(entry);
                    var target = info.LinkTarget;
                    if (target is null) continue;
                    if (target.ToLowerInvariant().StartsWith(installPrefix, StringComparison.Ordinal))
                    {
                        try { File.Delete(entry); } catch { }
                    }
                }
                catch { /* skip unreadable entry */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Best-effort removal of the HKCU/HKLM ARP subkey backing a portable
    /// entry. We match on LocalId (<c>ARP\&lt;scope&gt;\&lt;arch&gt;\&lt;subkey&gt;</c>) when
    /// present so we delete the exact key the list view surfaced; otherwise we
    /// walk the standard Uninstall path and match on WinGetPackageIdentifier.
    /// Failures are swallowed because the file deletion already succeeded.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void TryRemovePortableRegistryEntry(ListMatch installed)
    {
        try
        {
            const string ArpPrefix = @"ARP\";
            if (installed.LocalId.StartsWith(ArpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = installed.LocalId[ArpPrefix.Length..];
                var parts = rest.Split('\\', 3);
                if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    var hive = parts[0].Equals("User", StringComparison.OrdinalIgnoreCase)
                        ? Microsoft.Win32.Registry.CurrentUser
                        : Microsoft.Win32.Registry.LocalMachine;
                    hive.DeleteSubKeyTree(
                        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{parts[2]}",
                        throwOnMissingSubKey: false);
                    return;
                }
            }

            // Fall back: scan ARP by WinGetPackageIdentifier.
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var subkey = uninstall.OpenSubKey(name);
                    if (subkey is null) continue;
                    if (string.Equals(subkey.GetValue("WinGetPackageIdentifier") as string, installed.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        try { uninstall.DeleteSubKeyTree(name, throwOnMissingSubKey: false); } catch { }
                    }
                }
            }
        }
        catch { /* best-effort: file removal already succeeded */ }
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
