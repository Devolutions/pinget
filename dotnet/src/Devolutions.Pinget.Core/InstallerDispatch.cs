using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Devolutions.Pinget.Core;

internal static class InstallerDispatch
{
    internal enum PortableScope
    {
        User,
        Machine,
    }

    internal static PortableScope ResolvePortableScope(string? requestedScope, string? installedScope, string? installerScope)
    {
        var scope = new[] { requestedScope, installedScope, installerScope }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        if (scope is null || scope.Equals("user", StringComparison.OrdinalIgnoreCase))
            return PortableScope.User;
        if (scope.Equals("machine", StringComparison.OrdinalIgnoreCase))
            return PortableScope.Machine;
        throw new InvalidOperationException($"Unsupported install scope '{scope}'.");
    }

    public static int Execute(string installerPath, string installerType, InstallRequest request, Manifest manifest, Installer installer, List<string>? warnings = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Installing packages is only supported on Windows");

        warnings ??= [];
        return installerType.ToLowerInvariant() switch
        {
            "msi" or "wix" => RunMsi(installerPath, request, manifest, installer),
            "msix" or "appx" => RunMsix(installerPath),
            "msstore" => RunMsStore(request, manifest, installer),
            "zip" when IsPortableZipInstaller(installer) => InstallPortable(installerPath, request, manifest, installer,
                ResolvePortableScope(request.Query.InstallScope ?? request.Scope, null, installer.Scope), warnings),
            "portable" => InstallPortable(installerPath, request, manifest, installer,
                ResolvePortableScope(request.Query.InstallScope ?? request.Scope, null, installer.Scope), warnings),
            "zip" => ExtractZip(installerPath, ResolvePortableScope(request.Query.InstallScope ?? request.Scope, null, installer.Scope)),
            _ => RunExe(installerPath, installerType, request, manifest, installer)
        };
    }

    [SupportedOSPlatform("windows")]
    public static int Uninstall(ListMatch installed, UninstallRequest request, List<string>? warnings = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Uninstalling packages is only supported on Windows");

        if (request.Purge && request.Preserve)
            throw new InvalidOperationException("--purge and --preserve cannot be used together.");

        warnings ??= [];
        if (string.Equals(installed.InstallerCategory, "portable", StringComparison.OrdinalIgnoreCase))
            return UninstallPortable(installed, request, warnings);

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

                    if (IsMsiArpEntry(subkey.GetValue("WindowsInstaller")) &&
                        TryRunMsiUninstall(installed, subkeyName, productCode, request, out exitCode))
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
        if (ShouldRunMsiDirect(request, installer))
        {
            var args = BuildArguments("msi", request, manifest, installer, path, includeMsiInstallTarget: false);
            return RunMsiDirect(path, args);
        }

        var shellArgs = BuildArguments("msi", request, manifest, installer, path, includeMsiInstallTarget: true);
        return RunShellExecute("msiexec.exe", shellArgs, ShouldElevateMsiShellExecute(installer));
    }

    internal static bool ShouldRunMsiDirect(InstallRequest request, Installer installer) =>
        request.Mode == InstallerMode.Silent &&
        !ShouldElevateMsiShellExecute(installer);

    internal static bool ShouldElevateMsiShellExecute(Installer installer) =>
        string.Equals(installer.Scope, "machine", StringComparison.OrdinalIgnoreCase);

    private static int RunShellExecute(string path, IReadOnlyList<string> args, bool runAs = false)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Arguments = JoinArguments(args),
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (runAs)
            psi.Verb = "runas";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch installer");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    private static int RunMsiDirect(string path, IReadOnlyList<string> args)
    {
        var parsed = ParseMsiDirectArguments(args);

        if (parsed.LogPath is null)
        {
            _ = MsiEnableLog(0, null, 0);
        }
        else
        {
            var logResult = MsiEnableLog(parsed.LogMode, parsed.LogPath, parsed.LogAttributes);
            if (logResult != 0)
                throw new InvalidOperationException($"Failed to enable MSI logging (Win32 error {logResult}).");
        }

        MsiSetInternalUI(parsed.UiLevel, IntPtr.Zero);
        return unchecked((int)MsiInstallProduct(path, string.IsNullOrEmpty(parsed.Properties) ? null : parsed.Properties));
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

    private static int RunMsStore(InstallRequest request, Manifest manifest, Installer installer)
    {
        var productId = !string.IsNullOrWhiteSpace(installer.MSStoreProductIdentifier)
            ? installer.MSStoreProductIdentifier!
            : manifest.Id;
        var effectiveScope = request.Query.InstallScope ?? installer.Scope;
        var isMachine = string.Equals(effectiveScope, "machine", StringComparison.OrdinalIgnoreCase);
        var isSilent = request.Mode == InstallerMode.Silent;
        var script = BuildMsStoreInstallScript(productId, isMachine, isSilent, request.Force);

        var psi = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run MSStore installer via PowerShell");
        proc.WaitForExit();
        return proc.ExitCode;
    }

    internal static string BuildMsStoreInstallScript(string productId, bool isMachine, bool isSilent, bool force)
    {
        var escapedProductId = productId.Replace("'", "''");
        var machineLiteral = isMachine ? "$true" : "$false";
        var silentLiteral = isSilent ? "$true" : "$false";
        var forceLiteral = force ? "$true" : "$false";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $productId = '{{escapedProductId}}'
            $isMachine = {{machineLiteral}}
            $isSilent = {{silentLiteral}}
            $force = {{forceLiteral}}
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
            })[0]
            function Await-WinRt($op, [Type]$resultType) {
                $asTask = $asTaskGeneric.MakeGenericMethod($resultType)
                $task = $asTask.Invoke($null, @($op))
                $task.GetAwaiter().GetResult()
            }
            $mgr = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager, Windows.ApplicationModel, ContentType=WindowsRuntime]::new()
            [void][Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementResult, Windows.ApplicationModel, ContentType=WindowsRuntime]
            [void][Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementStatus, Windows.ApplicationModel, ContentType=WindowsRuntime]
            [void][Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem, Windows.ApplicationModel, ContentType=WindowsRuntime]
            [void][Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState, Windows.ApplicationModel, ContentType=WindowsRuntime]
            [void][Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallationToastNotificationMode, Windows.ApplicationModel, ContentType=WindowsRuntime]
            if (Await-WinRt ($mgr.IsStoreBlockedByPolicyAsync('Microsoft.WindowsStore', 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US')) ([bool])) {
                [Console]::Error.WriteLine('The Microsoft Store client is blocked by policy.')
                exit -1978334975
            }
            if (-not (Await-WinRt ($mgr.GetIsAppAllowedToInstallAsync($productId)) ([bool]))) {
                [Console]::Error.WriteLine('The Microsoft Store app is blocked by policy.')
                exit -1978334974
            }
            try {
                if ($isMachine) {
                    [void](Await-WinRt ($mgr.GetFreeDeviceEntitlementAsync($productId, '', '')) ([Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementResult]))
                } else {
                    $entitlement = Await-WinRt ($mgr.GetFreeUserEntitlementAsync($productId, '', '')) ([Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementResult])
                    if ($null -eq $entitlement -or $entitlement.Status -eq [Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementStatus]::NoStoreAccount) {
                        [void](Await-WinRt ($mgr.GetFreeDeviceEntitlementAsync($productId, '', '')) ([Windows.ApplicationModel.Store.Preview.InstallControl.GetEntitlementResult]))
                    }
                }
            } catch {}
            $options = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallOptions, Windows.ApplicationModel, ContentType=WindowsRuntime]::new()
            $options.AllowForcedAppRestart = $force
            if ($isSilent) {
                $noToast = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallationToastNotificationMode]::NoToast
                $options.InstallInProgressToastNotificationMode = $noToast
                $options.CompletedInstallToastNotificationMode = $noToast
            }
            if ($isMachine) {
                $options.InstallForAllUsers = $true
            }
            $itemsType = [System.Collections.Generic.IReadOnlyList[Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallItem]]
            $items = Await-WinRt ($mgr.StartProductInstallAsync($productId, '', 'WinGetCli', '', $options)) $itemsType
            $action = 'None'
            foreach ($item in $items) {
                $state = $item.GetCurrentStatus().InstallState
                if ($state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Canceled -or
                    $state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Error) {
                    $action = 'Cancel'
                    break
                }
                if ($state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Paused -or
                    $state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::PausedLowBattery -or
                    $state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::PausedWiFiRecommended -or
                    $state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::PausedWiFiRequired -or
                    $state -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::ReadyToDownload) {
                    $action = 'Restart'
                }
            }
            if ($action -eq 'Cancel') {
                $mgr.Cancel($productId)
                for ($i = 0; $i -lt 50; $i++) {
                    Start-Sleep -Milliseconds 200
                    $stillPresent = $false
                    foreach ($active in $mgr.AppInstallItems) {
                        if ($active.ProductId -ieq $productId) { $stillPresent = $true; break }
                    }
                    if (-not $stillPresent) { break }
                }
                $items = Await-WinRt ($mgr.StartProductInstallAsync($productId, '', 'WinGetCli', '', $options)) $itemsType
            } elseif ($action -eq 'Restart') {
                $mgr.Restart($productId)
                $items = Await-WinRt ($mgr.StartProductInstallAsync($productId, '', 'WinGetCli', '', $options)) $itemsType
            }
            if ($isSilent) {
                $noToast = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallationToastNotificationMode]::NoToast
                foreach ($item in $items) {
                    $item.InstallInProgressToastNotificationMode = $noToast
                    $item.CompletedInstallToastNotificationMode = $noToast
                }
            }
            $maxProgress = 100 * $items.Count
            $currentProgress = 0
            while ($currentProgress -lt $maxProgress) {
                $currentProgress = 0
                $allCompleted = $true
                foreach ($item in $items) {
                    $status = $item.GetCurrentStatus()
                    $currentProgress += [int]$status.PercentComplete
                    if ($status.InstallState -ne [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Completed) {
                        $allCompleted = $false
                    }
                    $err = $status.ErrorCode
                    if ($null -ne $err -and $err.Value -lt 0) {
                        try { $mgr.Cancel($productId) } catch {}
                        exit $err.Value
                    }
                    if ($status.InstallState -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Canceled -or
                        $status.InstallState -eq [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallState]::Error) {
                        try { $mgr.Cancel($productId) } catch {}
                        exit 1
                    }
                }
                if ($allCompleted -or $currentProgress -ge $maxProgress) { break }
                Start-Sleep -Milliseconds 100
            }
            if ($isMachine) {
                try {
                    $pkgMgr = [Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime]::new()
                    $provisioned = $pkgMgr.FindProvisionedPackages()
                    foreach ($item in $items) {
                        $familyName = $item.PackageFamilyName
                        if ([string]::IsNullOrWhiteSpace($familyName)) { continue }
                        $already = $false
                        foreach ($p in $provisioned) {
                            if ($p.Id.FamilyName -eq $familyName) { $already = $true; break }
                        }
                        if (-not $already) {
                            $provOp = $pkgMgr.ProvisionPackageForAllUsersAsync($familyName)
                            while ($provOp.Status -eq [Windows.Foundation.AsyncStatus, Windows.Foundation, ContentType=WindowsRuntime]::Started) {
                                Start-Sleep -Milliseconds 100
                            }
                        }
                    }
                } catch {}
            }
            exit 0
            """;
    }

    private static int ExtractZip(string path, PortableScope scope)
    {
        var target = scope == PortableScope.Machine
        ? Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.ProgramFiles),
            "WinGet",
            "Programs"
          )
        : Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.LocalApplicationData),
            "Programs"
          );
        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(path, target, overwriteFiles: true);
        return 0;
    }

    internal static bool IsPortableZipInstaller(Installer installer) =>
        string.Equals(installer.NestedInstallerType, "portable", StringComparison.OrdinalIgnoreCase);

    private static string PortableKnownFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"The {folder} known folder is unavailable.");
        return path;
    }

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
    /// resulting user or machine ARP entry is recognized by <c>winget list</c>
    /// and by pinget's own list view:
    /// <list type="bullet">
    ///   <item>Resolves the target install directory in this priority order:
    ///   <c>request.InstallLocation</c>, existing <c>InstallLocation</c> from
    ///   the registry ARP entry (so upgrades preserve a user's custom path),
    ///   then WinGet's default portable root for the selected scope.</item>
    ///   <item>If an existing pinget-owned install lives in that directory
    ///   (<c>InstallDirectoryCreated=1</c>), cleans its contents before
    ///   extracting the new version.</item>
    ///   <item>Extracts the zip (or copies the standalone binary), then writes
    ///   the ARP registry entry both <c>winget list</c> and pinget read from.</item>
    /// </list>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int InstallPortable(string installerPath, InstallRequest request, Manifest manifest, Installer installer, PortableScope scope, List<string> warnings)
    {
        var sourceIdentifier = PortableSourceIdentifier(request.Query);
        var existingEntry = ReadExistingPortableEntry(manifest.Id, scope);
        // Reuse the existing subkey name on upgrade so we don't orphan the prior
        // entry by writing to a different one.
        var subkeyName = existingEntry?.SubkeyName ?? PortableSubkeyName(manifest.Id, sourceIdentifier);
        if (subkeyName.Contains('\\') || subkeyName.Contains('/') || NormalizePortableRelativePath(subkeyName) != subkeyName)
            throw new InvalidOperationException($"Invalid portable package identifier: {subkeyName}");

        if (string.IsNullOrWhiteSpace(request.InstallLocation) &&
            !string.IsNullOrWhiteSpace(existingEntry?.InstallLocation) &&
            !Path.IsPathFullyQualified(existingEntry.InstallLocation))
            throw new InvalidOperationException("The previous portable install location is relative; specify an absolute install location.");
        var targetDir = Path.GetFullPath(ResolvePortableInstallLocation(request, existingEntry, subkeyName, scope));

        // If we created this directory on a previous install, wipe it clean so the
        // new version doesn't co-exist with leftovers from the previous one. We
        // do not touch a directory we don't own (InstallDirectoryCreated != 1).
        var prevDirCreated = IsOwnedPortableDirectory(
            existingEntry?.InstallDirectoryCreated == 1, existingEntry?.InstallLocation, targetDir);
        var dirExisted = Directory.Exists(targetDir);
        var installerType = installer.InstallerType ?? "";
        var payload = ReadPortablePayload(installerPath, string.Equals(installerType, "zip", StringComparison.OrdinalIgnoreCase));
        var nestedPath = string.Equals(installerType, "zip", StringComparison.OrdinalIgnoreCase) && installer.NestedInstallerFiles.Count > 0
            ? NormalizePortableRelativePath(installer.NestedInstallerFiles[0].RelativeFilePath)
            : null;
        if (nestedPath is not null && !payload.Files.Contains(nestedPath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Portable target '{nestedPath}' is not present in the installer archive.");
        var sameExistingLocation = IsOwnedPortableDirectory(true, existingEntry?.InstallLocation, targetDir);
        IReadOnlyList<string> previousFiles = sameExistingLocation ? existingEntry?.Files ?? [] : [];
        var previousDirectories = sameExistingLocation ? existingEntry?.Directories ?? [] : [];
        if (sameExistingLocation && !prevDirCreated && existingEntry?.Files is null)
        {
            if (!string.IsNullOrWhiteSpace(existingEntry?.PortableTargetFullPath) &&
                Path.IsPathFullyQualified(existingEntry.PortableTargetFullPath))
            {
                var previousTarget = Path.GetRelativePath(targetDir, existingEntry.PortableTargetFullPath);
                if (previousTarget != ".")
                    previousFiles = [NormalizePortableRelativePath(previousTarget)];
            }
            warnings.Add("The previous portable install did not track all files; unrecognized files in its existing directory will be preserved.");
        }
        var createdDirectories = PlanPortablePayload(payload, targetDir, prevDirCreated, previousFiles, previousDirectories);

        if (prevDirCreated && PathStartsWithCaseInsensitive(Path.GetFullPath(installerPath), Path.GetFullPath(targetDir)))
            throw new InvalidOperationException("The portable installer is inside the directory that would be cleaned.");
        if (prevDirCreated && dirExisted)
            CleanDirectoryContents(targetDir);

        Directory.CreateDirectory(targetDir);
        // "We created it" is sticky across upgrades: if a previous install
        // marked it created, it still counts as created by us even when the
        // dir already existed this round.
        var installDirectoryCreated = !dirExisted || prevDirCreated;

        string portableTargetFullPath;
        if (string.Equals(installerType, "zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(installerPath, targetDir, overwriteFiles: true);
            // For nested-portable zips the binary lives at the RelativeFilePath
            // the manifest declares. Record the first one as
            // PortableTargetFullPath so winget's portable uninstall workflow can
            // identify which file to remove.
            portableTargetFullPath = nestedPath is not null
                ? Path.Combine(targetDir, nestedPath)
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

        if (!prevDirCreated && sameExistingLocation && previousFiles.Count > 0)
        {
            var currentFiles = new HashSet<string>(payload.Files, StringComparer.OrdinalIgnoreCase);
            RemoveTrackedPortableFiles(targetDir, previousFiles.Where(file => !currentFiles.Contains(file)).ToArray(), []);
        }

        // Create the Links\<alias>.exe shim so users can invoke the portable from
        // any shell, matching winget's portable workflow. Failures are non-fatal —
        // when the user lacks SeCreateSymbolicLink (no Developer Mode, not admin)
        // we still leave the package installed at install_location.
        var alias = DeterminePortableAlias(installer);
        var symlinkFullPath = string.IsNullOrEmpty(alias)
            ? null
            : TryCreatePortableSymlink(portableTargetFullPath, alias!, scope, existingEntry, warnings);

        if (!string.IsNullOrWhiteSpace(existingEntry?.PortableSymlinkFullPath) &&
            !string.Equals(existingEntry.PortableSymlinkFullPath, symlinkFullPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                RemoveOwnedPortableLink(existingEntry.PortableSymlinkFullPath, WingetLinksDir(scope),
                    PortableLinkTargetBoundary(existingEntry, existingEntry.InstallLocation));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                       InvalidOperationException or System.Security.SecurityException)
            {
                warnings.Add($"Could not remove the previous portable command link: {ex.Message}");
            }
        }

        // Add the scope's links directory to PATH when a command link exists.
        var addedToPath = symlinkFullPath is not null && TryAddLinksDirToPath(scope, warnings);

        WritePortableArpEntry(
            new PortableArpEntry(
                SubkeyName: subkeyName,
                InstallLocation: targetDir,
                PortableTargetFullPath: portableTargetFullPath,
                PortableSymlinkFullPath: symlinkFullPath,
                InstallDirectoryCreated: installDirectoryCreated,
                AddedToPath: addedToPath,
                Files: payload.Files,
                Directories: createdDirectories,
                SourceIdentifier: sourceIdentifier,
                Manifest: manifest
            ),
            scope
        );

        CleanupPreviousPortableInstall(existingEntry, targetDir, warnings);
        return 0;
    }

    private static void CleanupPreviousPortableInstall(ExistingPortableEntry? previous, string currentLocation, List<string> warnings)
    {
        var oldLocation = previous?.InstallLocation;
        if (string.IsNullOrWhiteSpace(oldLocation))
            return;
        if (!Path.IsPathFullyQualified(oldLocation))
        {
            warnings.Add($"The previous portable directory '{oldLocation}' is relative and was preserved; remove any remaining package files manually.");
            return;
        }
        if (IsOwnedPortableDirectory(true, oldLocation, currentLocation) ||
            !Directory.Exists(oldLocation))
            return;

        var oldPath = Path.GetFullPath(oldLocation);
        var newPath = Path.GetFullPath(currentLocation);
        if (PathStartsWithCaseInsensitive(oldPath, newPath) || PathStartsWithCaseInsensitive(newPath, oldPath))
        {
            warnings.Add($"The previous portable directory '{oldLocation}' overlaps the new location and was preserved; remove any remaining package files manually.");
            return;
        }
        try
        {
            RemovePortableDirectory(oldLocation, previous, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            warnings.Add($"Could not clean the previous portable directory '{oldLocation}': {ex.Message}");
        }
    }

    internal sealed record PortablePayload(IReadOnlyList<string> Files, IReadOnlyList<string> Directories);

    internal static PortablePayload ReadPortablePayload(string installerPath, bool isZip)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string path, bool isDirectory)
        {
            var normalized = NormalizePortableRelativePath(path);
            if (isDirectory)
                directories.Add(normalized);
            else if (!files.Add(normalized))
                throw new InvalidOperationException($"The portable installer contains duplicate files: {normalized}");

            var parent = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(parent))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        if (isZip)
        {
            using var archive = ZipFile.OpenRead(installerPath);
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidOperationException($"Portable installer contains a symbolic link: {entry.FullName}");
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var name = entry.FullName.TrimEnd('/', '\\');
                if (isDirectory && name == ".")
                    continue;
                AddPath(name, isDirectory);
            }
        }
        else
        {
            AddPath(Path.GetFileName(installerPath), false);
        }

        if (files.Count == 0)
            throw new InvalidOperationException("The portable installer contains no files.");
        if (files.Overlaps(directories))
            throw new InvalidOperationException("The portable installer contains conflicting file and directory paths.");
        return new PortablePayload(files.ToArray(), directories.ToArray());
    }

    internal static IReadOnlyList<string> PlanPortablePayload(
        PortablePayload payload, string targetDir, bool directoryOwned,
        IReadOnlyList<string> previousFiles, IReadOnlyList<string> previousDirectories)
    {
        var previousFileSet = new HashSet<string>(previousFiles.Select(NormalizePortableRelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in payload.Files)
        {
            var destination = SafePortablePath(targetDir, relativePath);
            if (!directoryOwned && Directory.Exists(destination))
                throw new IOException($"Portable file conflicts with an existing directory: {destination}");
            if (!directoryOwned && File.Exists(destination) && !previousFileSet.Contains(relativePath))
                throw new IOException($"Portable file conflicts with an existing file: {destination}");
        }

        var createdDirectories = new HashSet<string>(previousDirectories.Select(NormalizePortableRelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in payload.Directories)
        {
            var directory = SafePortablePath(targetDir, relativePath);
            if (!directoryOwned && File.Exists(directory))
                throw new IOException($"Portable directory conflicts with an existing file: {directory}");
            if (!Directory.Exists(directory))
                createdDirectories.Add(relativePath);
        }
        return createdDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizePortableRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.StartsWith('\\') ||
            path.StartsWith('/') || path.Contains(':'))
            throw new InvalidOperationException($"Invalid portable relative path: {path}");
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".").ToArray();
        if (parts.Length == 0 || parts.Any(part => part == ".." ||
                              part.EndsWith(' ') || part.EndsWith('.') ||
                              part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                              part.Any(ch => ch <= '\u001f' || "<>\"|?*".Contains(ch)) ||
                              IsReservedPortableName(part)))
            throw new InvalidOperationException($"Invalid portable relative path: {path}");
        return Path.Combine(parts);
    }

    private static bool IsReservedPortableName(string part)
    {
        var name = part.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Length == 4 &&
               (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               name[3] is >= '1' and <= '9';
    }

    private static string SafePortablePath(string root, string relativePath)
    {
        var normalized = NormalizePortableRelativePath(relativePath);
        var rootPath = Path.GetFullPath(root);
        var current = rootPath;
        var parts = normalized.Split(Path.DirectorySeparatorChar);
        foreach (var part in new[] { "" }.Concat(parts))
        {
            if (part.Length > 0)
                current = Path.Combine(current, part);
            EnsureNotPortableReparsePoint(current);
        }
        return current;
    }

    private static void EnsureNotPortableReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Portable path contains a symbolic link or reparse point: {path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static void RemoveTrackedPortableFiles(string root, IReadOnlyList<string> files, IReadOnlyList<string> directories)
    {
        var filePaths = files.Select(path => SafePortablePath(root, path)).ToArray();
        var directoryPaths = directories.Select(path => SafePortablePath(root, path))
            .OrderByDescending(path => path.Length).ToArray();
        foreach (var path in filePaths)
        {
            if (Directory.Exists(path))
                throw new IOException($"Tracked portable file is now a directory: {path}");
        }
        foreach (var path in filePaths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        foreach (var path in directoryPaths)
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
    }

    private sealed record PortableArpEntry(
        string SubkeyName,
        string InstallLocation,
        string PortableTargetFullPath,
        string? PortableSymlinkFullPath,
        bool InstallDirectoryCreated,
        bool AddedToPath,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Directories,
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
    internal static string? DeterminePortableAlias(Installer installer)
    {
        foreach (var file in installer.NestedInstallerFiles)
        {
            if (!string.IsNullOrEmpty(file.PortableCommandAlias))
                return file.PortableCommandAlias;
            else if (!string.IsNullOrEmpty(file.RelativeFilePath))
                return Path.GetFileNameWithoutExtension(file.RelativeFilePath);
        }
        return installer.Commands.Count > 0 ? installer.Commands[0] : null;
    }

    private static string WingetLinksDir(PortableScope scope) => scope == PortableScope.Machine
        ? Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.ProgramFiles),
            "WinGet", "Links")
        : Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links");

    /// <summary>
    /// Creates (or replaces) <c>Links\&lt;alias&gt;.exe</c> as a symlink
    /// pointing at the portable's binary. Returns the link path on success.
    /// Returns null on any I/O failure (notably ERROR_PRIVILEGE_NOT_HELD when
    /// the user lacks SeCreateSymbolicLink) so the install can still
    /// complete without the shim.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryCreatePortableSymlink(
        string target, string alias, PortableScope scope, ExistingPortableEntry? existingEntry, List<string> warnings)
    {
        try
        {
            var linksDir = WingetLinksDir(scope);
            Directory.CreateDirectory(linksDir);
            EnsureNotPortableReparsePoint(linksDir);
            if (alias.Contains('\\') || alias.Contains('/') || NormalizePortableRelativePath(alias) != alias)
                throw new InvalidOperationException($"Invalid portable command alias: {alias}");
            var linkPath = Path.Combine(linksDir, $"{alias}.exe");

            if (File.Exists(linkPath) || new FileInfo(linkPath).LinkTarget is not null)
            {
                if (!string.Equals(linkPath, existingEntry?.PortableSymlinkFullPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The portable command link already exists: {linkPath}");
                RemoveOwnedPortableLink(linkPath, linksDir,
                    PortableLinkTargetBoundary(existingEntry, existingEntry?.InstallLocation));
            }

            File.CreateSymbolicLink(linkPath, target);
            return linkPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            warnings.Add($"Could not create the portable command link '{alias}': {ex.Message}");
            return null;
        }
    }

    internal static bool RemoveOwnedPortableLink(string linkPath, string linksRoot, string? installLocation)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(linksRoot));
        EnsureNotPortableReparsePoint(root);
        var link = Path.GetFullPath(linkPath);
        var parent = Path.GetDirectoryName(link);
        if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Portable command link is outside the links directory: {linkPath}");

        var target = new FileInfo(link).LinkTarget;
        if (target is null)
        {
            if (File.Exists(link) || Directory.Exists(link))
                throw new IOException($"Portable command link is not a symbolic link: {link}");
            return false;
        }
        if (string.IsNullOrWhiteSpace(installLocation))
            throw new InvalidOperationException($"Portable command link ownership cannot be verified: {link}");
        var resolvedTarget = Path.GetFullPath(target, root);
        if (!PathStartsWithCaseInsensitive(resolvedTarget, Path.GetFullPath(installLocation)))
            throw new IOException($"Portable command link points outside the installation: {link}");
        File.Delete(link);
        return true;
    }

    internal static string? PortableLinkTargetBoundary(ExistingPortableEntry? entry, string? installLocation)
    {
        if (entry is null || string.IsNullOrWhiteSpace(installLocation) ||
            !IsOwnedPortableDirectory(true, entry.InstallLocation, installLocation))
            return null;
        if (entry.InstallDirectoryCreated == 1)
            return installLocation;
        if (string.IsNullOrWhiteSpace(entry.PortableTargetFullPath) ||
            !Path.IsPathFullyQualified(entry.PortableTargetFullPath))
            return null;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.PortableTargetFullPath));
        var location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installLocation));
        return !target.Equals(location, StringComparison.OrdinalIgnoreCase) && PathStartsWithCaseInsensitive(target, location)
            ? target : null;
    }

    internal static string? AppendPortableLinksPath(string existing, string linksDir, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var normalizedLinks = linksDir.TrimEnd('\\', '/');
        var alreadyPresent = existing.Split(';').Any(component =>
        {
            var value = expand(component.Trim().Trim('"'));
            return value.TrimEnd('\\', '/').Equals(normalizedLinks, StringComparison.OrdinalIgnoreCase);
        });
        if (alreadyPresent)
            return null;
        if (existing.Length == 0)
            return linksDir;
        return existing.EndsWith(';') ? existing + linksDir : existing + ";" + linksDir;
    }

    [SupportedOSPlatform("windows")]
    internal static (string Value, Microsoft.Win32.RegistryValueKind Kind)? PreparePortablePathUpdate(
        object? raw, Microsoft.Win32.RegistryValueKind kind, string linksDir, Func<string, string>? expand = null)
    {
        if (raw is not null and not string)
            throw new InvalidOperationException("The existing PATH registry value is not a string.");
        if (raw is null)
            kind = Microsoft.Win32.RegistryValueKind.ExpandString;
        if (kind is not (Microsoft.Win32.RegistryValueKind.String or Microsoft.Win32.RegistryValueKind.ExpandString))
            throw new InvalidOperationException("The existing PATH registry value has an unsupported type.");
        if (raw is string existing && existing.Contains('\0'))
            throw new InvalidOperationException("The existing PATH registry value contains an embedded null.");
        var expandForComparison = kind == Microsoft.Win32.RegistryValueKind.ExpandString
            ? expand ?? Environment.ExpandEnvironmentVariables
            : static (string value) => value;
        var newPath = AppendPortableLinksPath((string?)raw ?? "", linksDir, expandForComparison);
        return newPath is null ? null : (newPath, kind);
    }

    /// <summary>
    /// Adds <c>Links\</c> to the selected scope's PATH if not already present,
    /// then broadcasts WM_SETTINGCHANGE so explorer-spawned
    /// shells pick up the new PATH without a logoff. Returns true if PATH was
    /// actually appended this call.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryAddLinksDirToPath(PortableScope scope, List<string> warnings)
    {
        try
        {
            var linksDir = WingetLinksDir(scope);
            using var envKey = scope == PortableScope.Machine
                ? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", writable: true)
                : Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (envKey is null)
                throw new InvalidOperationException("The environment registry key is unavailable.");

            var raw = envKey.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = raw is null ? Microsoft.Win32.RegistryValueKind.ExpandString : envKey.GetValueKind("Path");
            var update = PreparePortablePathUpdate(raw, kind, linksDir);
            if (update is null)
                return false;

            envKey.SetValue("Path", update.Value.Value, update.Value.Kind);
            try { BroadcastEnvironmentChange(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                warnings.Add($"PATH was updated, but the environment-change notification failed: {ex.Message}");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            warnings.Add($"Could not add the portable links directory to PATH: {ex.Message}");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void BroadcastEnvironmentChange()
    {
        const uint HWND_BROADCAST = 0xFFFF;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        if (NativeMethods.SendMessageTimeoutW(
            new IntPtr(unchecked((int)HWND_BROADCAST)),
            WM_SETTINGCHANGE,
            IntPtr.Zero,
            "Environment",
            SMTO_ABORTIFHUNG,
            5000,
            out _) == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Case-insensitive, component-aware path-prefix check for Windows. A raw
    /// <c>string.StartsWith</c> on the path strings would let <c>C:\Foo</c>
    /// falsely match <c>C:\Foobar</c>; iterating path components avoids that.
    /// Used by the Links\ sweep so an uninstall of one portable can't
    /// accidentally delete shims that belong to a different portable whose
    /// install dir happens to share a string prefix.
    /// </summary>
    internal static bool PathStartsWithCaseInsensitive(string child, string parent)
    {
        static string[] Components(string path) =>
            path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.ToLowerInvariant())
                .ToArray();
        var parentParts = Components(parent);
        var childParts = Components(child);
        if (parentParts.Length == 0 || parentParts.Length > childParts.Length)
            return false;
        for (int i = 0; i < parentParts.Length; i++)
        {
            if (!string.Equals(parentParts[i], childParts[i], StringComparison.Ordinal))
                return false;
        }
        return true;
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
    internal static string PortableSourceIdentifier(PackageQuery query)
    {
        var source = query.Source;
        if (string.IsNullOrEmpty(source) || string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase))
            return "Microsoft.Winget.Source_8wekyb3d8bbwe";
        return source;
    }

    internal static string PortableSubkeyName(string packageId, string sourceIdentifier) =>
        $"{packageId}_{sourceIdentifier}";

    internal sealed record ExistingPortableEntry(
        string SubkeyName,
        string? InstallLocation,
        int? InstallDirectoryCreated,
        string? PortableTargetFullPath,
        string? PortableSymlinkFullPath,
        string[]? Files,
        string[]? Directories);

    [SupportedOSPlatform("windows")]
    private static ExistingPortableEntry? ReadExistingPortableEntry(string packageId, PortableScope scope)
    {
        var baseKey = scope == PortableScope.Machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;

        using var uninstall = baseKey
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

            return ReadPortableEntry(subkey, name);
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static ExistingPortableEntry ReadPortableEntry(Microsoft.Win32.RegistryKey subkey, string name) =>
        new(
            SubkeyName: name,
            InstallLocation: subkey.GetValue("InstallLocation") as string,
            InstallDirectoryCreated: subkey.GetValue("InstallDirectoryCreated") as int?,
            PortableTargetFullPath: subkey.GetValue("PortableTargetFullPath") as string,
            PortableSymlinkFullPath: subkey.GetValue("PortableSymlinkFullPath") as string,
            Files: subkey.GetValue("PingetPortableFiles") as string[],
            Directories: subkey.GetValue("PingetPortableDirectories") as string[]);

    private static string ResolvePortableInstallLocation(InstallRequest request, ExistingPortableEntry? existing, string subkeyName, PortableScope scope) =>
        ResolvePortableInstallLocationPure(
            request.InstallLocation,
            existing?.InstallLocation,
            subkeyName,
            DefaultPortableRoot(scope));

    /// <summary>
    /// Pure resolution rule for the portable install directory. Priority:
    /// <c>requestLocation</c> (CLI override) → <c>existingLocation</c> (so
    /// upgrades preserve a winget-installed location) → the scope's default
    /// portable root joined with the package's ARP subkey name.
    /// </summary>
    internal static string ResolvePortableInstallLocationPure(
        string? requestLocation,
        string? existingLocation,
        string subkeyName,
        string defaultUserPortableRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestLocation))
            return requestLocation!;
        if (!string.IsNullOrWhiteSpace(existingLocation))
            return existingLocation!;
        return Path.Combine(defaultUserPortableRoot, subkeyName);
    }

    private static string DefaultPortableRoot(PortableScope scope) => scope == PortableScope.Machine
        ? Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.ProgramFiles),
            "WinGet",
            "Packages"
        )
        : Path.Combine(
            PortableKnownFolder(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WinGet",
            "Packages"
        );

    internal static bool IsOwnedPortableDirectory(bool previouslyCreated, string? existingLocation, string targetDir)
    {
        if (!previouslyCreated || string.IsNullOrWhiteSpace(existingLocation) || !Path.IsPathFullyQualified(existingLocation))
            return false;
        var previous = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingLocation));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        return previous.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    internal static void CleanDirectoryContents(string dir)
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
    /// Writes the scoped ARP entry winget would normally write for a portable
    /// install. Only the subset that winget's portable list view and pinget's
    /// installed-package discovery actually read.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WritePortableArpEntry(PortableArpEntry entry, PortableScope scope)
    {
        var baseKey = scope == PortableScope.Machine
            ? Microsoft.Win32.Registry.LocalMachine
            : Microsoft.Win32.Registry.CurrentUser;

        using var subkey = baseKey
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
        else
            subkey.DeleteValue("PortableSymlinkFullPath", throwOnMissingValue: false);
        subkey.SetValue("InstallDirectoryAddedToPath", entry.AddedToPath ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        subkey.SetValue("InstallDirectoryCreated", entry.InstallDirectoryCreated ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        subkey.SetValue("PingetPortableFiles", entry.Files.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
        subkey.SetValue("PingetPortableDirectories", entry.Directories.ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
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
        return RunShellExecute(path, BuildArguments(installerType, request, manifest, installer, path));
    }

    internal static List<string> BuildArguments(string installerType, InstallerMode mode, Installer installer)
        => BuildArguments(
            installerType,
            new InstallRequest { Query = new PackageQuery(), Mode = mode },
            new Manifest { Id = "Test.Package", Name = "Test Package", Version = "1.0.0" },
            installer);

    internal static List<string> BuildArguments(string installerType, InstallRequest request, Manifest manifest, Installer installer, string? installerPath = null, bool includeMsiInstallTarget = true)
    {
        if (!string.IsNullOrWhiteSpace(request.Override))
            return SplitArguments(request.Override!);

        var normalizedType = installerType.ToLowerInvariant();
        var args = new List<string>();

        if (includeMsiInstallTarget && normalizedType is ("msi" or "wix"))
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

    internal readonly record struct MsiDirectArguments(
        uint UiLevel,
        string Properties,
        uint LogMode,
        string? LogPath,
        uint LogAttributes);

    internal static MsiDirectArguments ParseMsiDirectArguments(IReadOnlyList<string> args)
    {
        const uint installUiLevelDefault = 1;
        const uint installUiLevelNone = 2;
        const uint installUiLevelBasic = 3;
        const uint installUiLevelProgressOnly = 0x40;
        const uint installUiLevelHideCancel = 0x20;
        const uint installUiLevelUacOnly = 0x100;
        const uint logModeDefault = 1 | 2 | 4 | 16 | 128 | 256 | 512;
        const uint logModeAll = logModeDefault | 8 | 1024 | 2048;
        const uint logModeVerbose = 4096;
        const uint logModeExtraDebug = 8192;
        const uint logAttributeAppend = 1;
        const uint logAttributeFlushEachLine = 2;

        var uiLevel = installUiLevelDefault;
        var properties = new List<string>();
        uint logMode = 0;
        string? logPath = null;
        uint logAttributes = 0;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (IsMsiSwitch(token))
            {
                var option = token[1..];
                if (option.Equals("quiet", StringComparison.OrdinalIgnoreCase))
                {
                    uiLevel = installUiLevelNone | installUiLevelUacOnly;
                }
                else if (option.Equals("passive", StringComparison.OrdinalIgnoreCase))
                {
                    uiLevel = installUiLevelBasic | installUiLevelProgressOnly | installUiLevelHideCancel;
                    properties.Add("REBOOTPROMPT=S");
                }
                else if (option.Equals("norestart", StringComparison.OrdinalIgnoreCase))
                {
                    properties.Add("REBOOT=ReallySuppress");
                }
                else if (option.Equals("forcerestart", StringComparison.OrdinalIgnoreCase))
                {
                    properties.Add("REBOOT=Force");
                }
                else if (option.Equals("promptrestart", StringComparison.OrdinalIgnoreCase))
                {
                    properties.Add("REBOOTPROMPT=\"\"");
                }
                else if (option.Equals("log", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    logPath = index < args.Count ? args[index] : throw new InvalidOperationException("MSI /log requires a log path.");
                    logMode = logModeAll;
                }
                else if (option.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    uiLevel = installUiLevelNone | installUiLevelUacOnly;
                }
                else if (option.StartsWith("q", StringComparison.OrdinalIgnoreCase))
                {
                    uiLevel = ParseMsiUiLevel(option[1..]);
                }
                else if (option.StartsWith("l", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    logPath = index < args.Count ? args[index] : throw new InvalidOperationException("MSI logging switch requires a log path.");
                    (logMode, logAttributes) = ParseMsiLogMode(option[1..], logModeDefault, logModeAll, logModeVerbose, logModeExtraDebug, logAttributeAppend, logAttributeFlushEachLine);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported MSI switch for direct MSI execution: {token}");
                }
            }
            else
            {
                properties.Add(FormatMsiProperty(token));
            }
        }

        return new MsiDirectArguments(uiLevel, string.Join(' ', properties), logMode, logPath, logAttributes);

        static uint ParseMsiUiLevel(string modifier)
        {
            const uint installUiLevelNone = 2;
            const uint installUiLevelBasic = 3;
            const uint installUiLevelReduced = 4;
            const uint installUiLevelFull = 5;
            const uint installUiLevelEndDialog = 0x80;
            const uint installUiLevelProgressOnly = 0x40;
            const uint installUiLevelHideCancel = 0x20;
            const uint installUiLevelUacOnly = 0x100;

            if (string.IsNullOrEmpty(modifier))
                modifier = "n";

            var uiLevel = char.ToLowerInvariant(modifier[0]) switch
            {
                'f' => installUiLevelFull,
                'r' => installUiLevelReduced,
                'b' => installUiLevelBasic,
                'n' => installUiLevelNone,
                '+' => installUiLevelNone | installUiLevelEndDialog,
                _ => throw new InvalidOperationException($"Invalid MSI /q modifier: {modifier}")
            };

            foreach (var ch in modifier[1..])
            {
                switch (ch)
                {
                    case '+':
                        uiLevel |= installUiLevelEndDialog;
                        break;
                    case '-' when (uiLevel & 0xF) == installUiLevelBasic:
                        uiLevel |= installUiLevelProgressOnly;
                        break;
                    case '!' when (uiLevel & 0xF) == installUiLevelBasic:
                        uiLevel |= installUiLevelHideCancel;
                        break;
                    case '-' or '!':
                        throw new InvalidOperationException($"MSI /q modifier {ch} is only valid with basic UI.");
                    default:
                        throw new InvalidOperationException($"Invalid MSI /q modifier: {modifier}");
                }
            }

            if ((uiLevel & 0xF) == installUiLevelNone)
                uiLevel |= installUiLevelUacOnly;
            return uiLevel;
        }

        static (uint LogMode, uint LogAttributes) ParseMsiLogMode(
            string modifier,
            uint defaultMode,
            uint allMode,
            uint verbose,
            uint extraDebug,
            uint append,
            uint flush)
        {
            uint mode = 0;
            uint attributes = 0;
            foreach (var ch in modifier)
            {
                switch (char.ToLowerInvariant(ch))
                {
                    case '*':
                        mode |= allMode;
                        break;
                    case 'm':
                        mode |= 1;
                        break;
                    case 'e':
                        mode |= 2;
                        break;
                    case 'w':
                        mode |= 4;
                        break;
                    case 'u':
                        mode |= 8;
                        break;
                    case 'i':
                        mode |= 16;
                        break;
                    case 'o':
                        mode |= 128;
                        break;
                    case 'a':
                        mode |= 256;
                        break;
                    case 'r':
                        mode |= 512;
                        break;
                    case 'p':
                        mode |= 1024;
                        break;
                    case 'c':
                        mode |= 2048;
                        break;
                    case 'v':
                        mode |= verbose;
                        break;
                    case 'x':
                        mode |= extraDebug;
                        break;
                    case '+':
                        attributes |= append;
                        break;
                    case '!':
                        attributes |= flush;
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid MSI /l modifier: {modifier}");
                }
            }

            return (mode == 0 ? defaultMode : mode, attributes);
        }
    }

    private static bool IsMsiSwitch(string token) =>
        token.StartsWith('/') || token.StartsWith('-');

    private static string FormatMsiProperty(string token)
    {
        var separator = token.IndexOf('=');
        if (separator <= 0)
            throw new InvalidOperationException($"Invalid MSI property argument: {token}");

        var name = token[..separator];
        if (name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new InvalidOperationException($"Invalid MSI property name: {name}");

        var value = token[(separator + 1)..];
        if (value.Any(char.IsWhiteSpace) && !(value.StartsWith('"') && value.EndsWith('"')))
            return $"{name}=\"{value.Replace("\"", "\"\"")}\"";

        return token;
    }

    internal static string JoinArguments(IReadOnlyList<string> args) =>
        string.Join(' ', args.Select(QuoteArgument));

    private static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
            return arg;

        var result = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in arg)
        {
            switch (ch)
            {
                case '\\':
                    backslashes++;
                    break;
                case '"':
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    break;
                default:
                    result.Append('\\', backslashes);
                    backslashes = 0;
                    result.Append(ch);
                    break;
            }
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint MsiInstallProduct(string packagePath, string? commandLine);

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint MsiEnableLog(uint logMode, string? logFile, uint logAttributes);

    [System.Runtime.InteropServices.DllImport("msi.dll")]
    private static extern uint MsiSetInternalUI(uint uiLevel, IntPtr window);

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

    internal static bool IsMsiArpEntry(object? windowsInstaller)
        => windowsInstaller is int value && value == 1;

    private static bool TryRunMsiUninstall(
        ListMatch installed,
        string subkeyName,
        string? productCode,
        UninstallRequest request,
        out int exitCode)
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
    private static int UninstallPortable(ListMatch installed, UninstallRequest request, List<string> warnings)
    {
        var existingEntry = ReadPortableEntryForInstalled(installed);
        var knownSymlink = existingEntry?.PortableSymlinkFullPath;

        if (string.IsNullOrWhiteSpace(installed.InstallLocation))
        {
            if (request.Force)
            {
                TryRemovePortableSymlinks(knownSymlink, null, InstalledPortableScope(installed), warnings);
                TryRemovePortableRegistryEntry(installed, warnings);
                return 0;
            }
            throw new InvalidOperationException($"Portable package '{installed.Name}' does not expose an install location.");
        }

        if (request.Preserve)
            return 0;

        var removed = false;
        if (Directory.Exists(installed.InstallLocation))
        {
            RemovePortableDirectory(installed.InstallLocation, existingEntry, warnings);
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
            TryRemovePortableSymlinks(knownSymlink, PortableLinkTargetBoundary(existingEntry, installed.InstallLocation),
                InstalledPortableScope(installed), warnings);
            TryRemovePortableRegistryEntry(installed, warnings);
            return 0;
        }

        throw new InvalidOperationException($"Portable package location not found: {installed.InstallLocation}");
    }

    private static void RemovePortableDirectory(string location, ExistingPortableEntry? entry, List<string> warnings)
    {
        var matchesLocation = IsOwnedPortableDirectory(true, entry?.InstallLocation, location);
        if (matchesLocation && entry?.InstallDirectoryCreated == 1)
        {
            EnsureNotPortableReparsePoint(location);
            Directory.Delete(location, recursive: true);
        }
        else if (matchesLocation && entry?.Files is not null)
        {
            RemoveTrackedPortableFiles(location, entry.Files, entry.Directories ?? []);
        }
        else
        {
            if (matchesLocation && !string.IsNullOrWhiteSpace(entry?.PortableTargetFullPath) &&
                Path.IsPathFullyQualified(entry.PortableTargetFullPath))
            {
                var target = Path.GetRelativePath(location, entry.PortableTargetFullPath);
                if (target != ".")
                    RemoveTrackedPortableFiles(location, [target], []);
            }
            warnings.Add($"The portable directory '{location}' was preserved because complete file ownership information is unavailable; remove any remaining package files manually.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static ExistingPortableEntry? ReadPortableEntryForInstalled(ListMatch installed)
    {
        const string ArpPrefix = @"ARP\";
        if (!installed.LocalId.StartsWith(ArpPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = installed.LocalId[ArpPrefix.Length..];
        var parts = rest.Split('\\', 3);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]) || parts[2].Contains('\\') ||
            !(parts[0].Equals("User", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Machine", StringComparison.OrdinalIgnoreCase)))
            return null;
        var hive = parts[0].Equals("User", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.Win32.RegistryHive.CurrentUser
            : Microsoft.Win32.RegistryHive.LocalMachine;
        var view = parts[1].Equals("X86", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.Win32.RegistryView.Registry32
            : Microsoft.Win32.RegistryView.Registry64;
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
        using var subkey = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{parts[2]}");
        return subkey is null ? null : ReadPortableEntry(subkey, parts[2]);
    }

    internal static PortableScope InstalledPortableScope(ListMatch installed)
    {
        if (installed.LocalId.StartsWith(@"ARP\Machine\", StringComparison.OrdinalIgnoreCase))
            return PortableScope.Machine;
        if (installed.LocalId.StartsWith(@"ARP\User\", StringComparison.OrdinalIgnoreCase))
            return PortableScope.User;
        return ResolvePortableScope(null, installed.Scope, null);
    }

    [SupportedOSPlatform("windows")]
    private static void TryRemovePortableSymlinks(string? knownSymlink, string? installLocation, PortableScope scope, List<string> warnings)
        => RemovePortableLinksInDirectory(knownSymlink, installLocation, WingetLinksDir(scope), warnings);

    internal static void RemovePortableLinksInDirectory(string? knownSymlink, string? installLocation, string linksRoot, List<string> warnings)
    {
        try
        {
            EnsureNotPortableReparsePoint(linksRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   System.Security.SecurityException)
        {
            warnings.Add($"Could not scan the portable links directory: {ex.Message}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(knownSymlink))
        {
            try { RemoveOwnedPortableLink(knownSymlink, linksRoot, installLocation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                       InvalidOperationException or System.Security.SecurityException)
            {
                warnings.Add($"Could not remove the portable command link: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(installLocation))
            return;

        try
        {
            foreach (var entry in Directory.EnumerateFiles(linksRoot))
            {
                try
                {
                    var info = new FileInfo(entry);
                    var target = info.LinkTarget;
                    if (target is null) continue;
                    var resolvedTarget = Path.GetFullPath(target, linksRoot);
                    if (PathStartsWithCaseInsensitive(resolvedTarget, Path.GetFullPath(installLocation)))
                        RemoveOwnedPortableLink(entry, linksRoot, installLocation);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                           System.Security.SecurityException)
                {
                    warnings.Add($"Could not inspect or remove portable command link '{entry}': {ex.Message}");
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   System.Security.SecurityException)
        {
            warnings.Add($"Could not scan the portable links directory: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryRemovePortableRegistryEntry(ListMatch installed, List<string> warnings)
    {
        try
        {
            const string ArpPrefix = @"ARP\";
            if (installed.LocalId.StartsWith(ArpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = installed.LocalId[ArpPrefix.Length..];
                var parts = rest.Split('\\', 3);
                if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]) && !parts[2].Contains('\\') &&
                    (parts[0].Equals("User", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Machine", StringComparison.OrdinalIgnoreCase)))
                {
                    var hive = parts[0].Equals("User", StringComparison.OrdinalIgnoreCase)
                        ? Microsoft.Win32.RegistryHive.CurrentUser
                        : Microsoft.Win32.RegistryHive.LocalMachine;
                    var view = parts[1].Equals("X86", StringComparison.OrdinalIgnoreCase)
                        ? Microsoft.Win32.RegistryView.Registry32
                        : Microsoft.Win32.RegistryView.Registry64;
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    baseKey.DeleteSubKeyTree(
                        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{parts[2]}",
                        throwOnMissingSubKey: false);
                    return;
                }
            }

            warnings.Add("Could not remove the portable registry entry because its exact registry key is unavailable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   System.Security.SecurityException)
        {
            warnings.Add($"Could not remove the portable registry entry: {ex.Message}");
        }
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
