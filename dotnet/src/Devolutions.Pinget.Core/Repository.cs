using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Data.Sqlite;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Devolutions.Pinget.Core;

public class Repository : IDisposable
{
    private const string AppRootEnvironmentVariable = "PINGET_APPROOT";

    internal const string InstalledStateUnsupportedWarning = "Installed package discovery is not supported on this platform; returning no installed packages.";
    internal const string InstallUnsupportedWarning = "Installing packages is not supported on this platform; no changes were made.";
    internal const string UninstallUnsupportedWarning = "Uninstalling packages is not supported on this platform; no changes were made.";
    internal const string RepairUnsupportedWarning = "Repairing packages is not supported on this platform; no changes were made.";
    internal const string RepairReinstallWarning = "Pinget repair currently re-runs the package install flow for the selected package.";

    private static int s_sqliteNativeLibraryInitialized;

    private readonly string _appRoot;
    private readonly HttpClient _client;
    private readonly bool _useSystemWingetSources;
    private SourceStore _store;

    private Repository(string appRoot, HttpClient client, SourceStore store, bool useSystemWingetSources)
    {
        _appRoot = appRoot;
        _client = client;
        _store = store;
        _useSystemWingetSources = useSystemWingetSources;
    }

    /// <summary>
    /// Opens the repository using either the default CLI storage root or the caller-supplied library options.
    /// </summary>
    public static Repository Open(RepositoryOptions? options = null)
    {
        options ??= new RepositoryOptions();
        EnsureSqliteNativeLibraryLoaded();
        var appRoot = SourceStoreManager.NormalizeAppRoot(options.AppRoot ?? Environment.GetEnvironmentVariable(AppRootEnvironmentVariable));
        SourceStoreManager.EnsureAppDirs(appRoot);
        var useSystemWingetSources = SourceStoreManager.UsesSystemWingetSourceCommands(appRoot);
        var store = SourceStoreManager.Load(appRoot);
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return new Repository(appRoot, client, store, useSystemWingetSources);
    }

    internal static IEnumerable<string> GetSqliteNativeLibraryCandidates(string assemblyDirectory)
    {
        yield return Path.Combine(assemblyDirectory, "e_sqlite3.dll");

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm => "win-arm",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

        yield return Path.Combine(assemblyDirectory, "runtimes", rid, "native", "e_sqlite3.dll");
    }

    private static void EnsureSqliteNativeLibraryLoaded()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (Interlocked.Exchange(ref s_sqliteNativeLibraryInitialized, 1) != 0)
            return;

        var assemblyDirectory = Path.GetDirectoryName(typeof(Repository).Assembly.Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            return;

        var nativeLibraryPath = GetSqliteNativeLibraryCandidates(assemblyDirectory)
            .FirstOrDefault(File.Exists);

        if (string.IsNullOrWhiteSpace(nativeLibraryPath))
            return;

        RegisterSqliteDllImportResolvers(nativeLibraryPath);

        try
        {
            NativeLibrary.Load(nativeLibraryPath);
        }
        catch
        {
            // The resolver path is the important fix for library-hosted environments.
        }
    }

    private static void RegisterSqliteDllImportResolvers(string nativeLibraryPath)
    {
        RegisterSqliteDllImportResolver("SQLitePCLRaw.provider.e_sqlite3", nativeLibraryPath);
        RegisterSqliteDllImportResolver("SQLitePCLRaw.core", nativeLibraryPath);
    }

    private static void RegisterSqliteDllImportResolver(string assemblyName, string nativeLibraryPath)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                ?? Assembly.Load(new AssemblyName(assemblyName));

            NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
            {
                if (!string.Equals(libraryName, "e_sqlite3", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                return NativeLibrary.Load(nativeLibraryPath);
            });
        }
        catch
        {
            // Ignore duplicate resolver registration or unavailable optional assemblies.
        }
    }

    public void Dispose() => _client.Dispose();
    public string AppRoot => _appRoot;
    public static IReadOnlyList<string> SupportedAdminSettings => SettingsStoreManager.SupportedAdminSettings;

    public void SetRequestHeader(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Header name is required.");

        _client.DefaultRequestHeaders.Remove(name);
        if (!_client.DefaultRequestHeaders.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"Invalid request header '{name}'.");
    }

    // ── Source management ──

    public List<SourceRecord> ListSources()
    {
        RefreshSystemWingetSources();
        return _store.Sources.ToList();
    }

    public void AddSource(string name, string arg, SourceKind kind, string trustLevel = "None", bool explicitSource = false, int priority = 0)
    {
        if (_useSystemWingetSources)
        {
            SystemWingetSourceStore.AddSource(name, arg, kind, NormalizeSourceTrustLevel(trustLevel), explicitSource);
            RefreshSystemWingetSources();
            return;
        }

        if (_store.Sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A source with name '{name}' already exists.");
        if (_store.Sources.Any(s => s.Arg == arg))
            throw new InvalidOperationException($"A source with argument '{arg}' already exists.");

        _store.Sources.Add(new SourceRecord
        {
            Name = name,
            Kind = kind,
            Arg = arg,
            Identifier = name,
            TrustLevel = NormalizeSourceTrustLevel(trustLevel),
            Explicit = explicitSource,
            Priority = priority,
        });
        SourceStoreManager.Save(_store, _appRoot);
    }

    public void EditSource(string name, bool? explicitSource = null, string? trustLevel = null)
    {
        if (_useSystemWingetSources)
            throw new InvalidOperationException("Editing system WinGet sources is not supported by winget source commands.");

        var source = _store.Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Source '{name}' not found.");

        if (explicitSource.HasValue)
            source.Explicit = explicitSource.Value;

        if (!string.IsNullOrWhiteSpace(trustLevel))
            source.TrustLevel = NormalizeSourceTrustLevel(trustLevel);

        SourceStoreManager.Save(_store, _appRoot);
    }

    public void RemoveSource(string name)
    {
        if (_useSystemWingetSources)
        {
            SystemWingetSourceStore.RemoveSource(name);
            RefreshSystemWingetSources();
            return;
        }

        var source = _store.Sources.FirstOrDefault(s => s.Name == name)
            ?? throw new InvalidOperationException($"Source '{name}' not found.");

        _store.Sources.Remove(source);
        var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot);
        if (Directory.Exists(stateDir))
            Directory.Delete(stateDir, recursive: true);
        SourceStoreManager.Save(_store, _appRoot);
    }

    public void ResetSource(string name)
    {
        if (_useSystemWingetSources)
        {
            SystemWingetSourceStore.ResetSource(name);
            RefreshSystemWingetSources();
            return;
        }

        var index = _store.Sources.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException($"Source '{name}' not found.");

        var source = _store.Sources[index];
        var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot);
        if (Directory.Exists(stateDir))
            Directory.Delete(stateDir, recursive: true);

        var defaultSource = SourceStore.Default().Sources
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (defaultSource is not null)
        {
            _store.Sources[index] = defaultSource with { };
        }
        else
        {
            source.LastUpdate = null;
            source.SourceVersion = null;
        }

        SourceStoreManager.Save(_store, _appRoot);
    }

    public void ResetSources()
    {
        if (_useSystemWingetSources)
        {
            SystemWingetSourceStore.ResetSources();
            RefreshSystemWingetSources();
            return;
        }

        foreach (var source in _store.Sources)
        {
            var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot);
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
        }
        _store = SourceStore.Default();
        SourceStoreManager.Save(_store, _appRoot);
    }

    public JsonObject GetUserSettings() =>
        SettingsStoreManager.LoadJsonObject(SettingsStoreManager.UserSettingsPath(_appRoot));

    public JsonObject SetUserSettings(JsonObject userSettings, bool merge)
    {
        var effective = merge
            ? SettingsStoreManager.MergeJsonObjects(GetUserSettings(), userSettings)
            : userSettings.DeepClone().AsObject();
        SettingsStoreManager.SaveJsonObject(SettingsStoreManager.UserSettingsPath(_appRoot), effective);
        return effective;
    }

    public bool TestUserSettings(JsonObject expected, bool ignoreNotSet)
    {
        var current = GetUserSettings();
        return ignoreNotSet
            ? SettingsStoreManager.JsonContains(current, expected)
            : JsonNode.DeepEquals(current, expected);
    }

    public JsonObject GetAdminSettings()
    {
        var settings = SettingsStoreManager.LoadJsonObject(SettingsStoreManager.AdminSettingsPath(_appRoot));
        foreach (var name in SupportedAdminSettings)
        {
            settings.TryAdd(name, false);
        }

        return settings;
    }

    public void SetAdminSetting(string name, bool enabled)
    {
        var normalized = SettingsStoreManager.NormalizeAdminSettingName(name);
        var settings = GetAdminSettings();
        settings[normalized] = enabled;
        SettingsStoreManager.SaveJsonObject(SettingsStoreManager.AdminSettingsPath(_appRoot), settings);
    }

    public void ResetAdminSetting(string? name = null, bool resetAll = false)
    {
        if (!resetAll && string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Resetting admin settings requires a setting name or reset-all.");

        var settings = GetAdminSettings();
        if (resetAll)
        {
            foreach (var settingName in SupportedAdminSettings)
            {
                settings[settingName] = false;
            }
        }
        else
        {
            settings[SettingsStoreManager.NormalizeAdminSettingName(name!)] = false;
        }

        SettingsStoreManager.SaveJsonObject(SettingsStoreManager.AdminSettingsPath(_appRoot), settings);
    }

    public void EnsureSettingsFiles()
    {
        var userSettingsPath = SettingsStoreManager.UserSettingsPath(_appRoot);
        if (!File.Exists(userSettingsPath))
            SettingsStoreManager.SaveJsonObject(userSettingsPath, new JsonObject());

        var adminSettingsPath = SettingsStoreManager.AdminSettingsPath(_appRoot);
        if (!File.Exists(adminSettingsPath))
            SettingsStoreManager.SaveJsonObject(adminSettingsPath, GetAdminSettings());
    }

    public List<SourceUpdateResult> UpdateSources(string? sourceName = null)
    {
        if (_useSystemWingetSources)
            return UpdateSystemWingetSources(sourceName);

        var indexes = ResolveSourceIndexes(sourceName);
        var results = new List<SourceUpdateResult>();

        foreach (var index in indexes)
        {
            var source = _store.Sources[index];
            var detail = source.Kind switch
            {
                SourceKind.PreIndexed => PreIndexedSource.Update(_client, source, _appRoot),
                SourceKind.Rest => RestSource.UpdateRest(_client, source, _appRoot),
                _ => "Unknown source kind"
            };
            source.LastUpdate = DateTime.UtcNow;
            results.Add(new SourceUpdateResult { Name = source.Name, Kind = source.Kind, Detail = detail });
        }

        SourceStoreManager.Save(_store, _appRoot);
        return results;
    }

    // ── Search ──

    public SearchResponse Search(PackageQuery query)
    {
        var (matches, warnings, truncated) = SearchLocated(query, SearchSemantics.Many);
        return new SearchResponse
        {
            Matches = matches.Select(m => m.Display).ToList(),
            Warnings = warnings,
            Truncated = truncated
        };
    }

    public List<Dictionary<string, object?>> SearchManifests(PackageQuery query)
    {
        var (matches, _, _) = SearchLocated(query, SearchSemantics.Many);
        return StructuredOutput.CollapseManifestResults(matches.Select(located =>
        {
            var (_, structuredDocument, _) = ManifestForMatch(located, query);
            return structuredDocument;
        }));
    }

    // ── Show ──

    public ShowResult Show(PackageQuery query)
    {
        var (located, warnings) = FindSingleMatch(query);
        var (manifest, structuredDocument, cachedFiles) = ManifestForMatch(located, query);
        var selectedInstaller = SelectInstaller(manifest.Installers, query);

        return new ShowResult
        {
            Package = located.Display,
            Manifest = manifest,
            SelectedInstaller = selectedInstaller,
            CachedFiles = cachedFiles,
            Warnings = warnings,
            StructuredDocument = structuredDocument,
        };
    }

    public VersionsResult ShowVersions(PackageQuery query)
    {
        var (located, warnings) = FindSingleMatch(query);
        var versions = VersionsForMatch(located, query);
        return new VersionsResult { Package = located.Display, Versions = versions, Warnings = warnings };
    }

    public VersionsResult SearchVersions(PackageQuery query)
    {
        var (located, warnings) = FindSingleMatchWithSemantics(query, SearchSemantics.Many);
        var versions = VersionsForMatch(located, query);
        return new VersionsResult { Package = located.Display, Versions = versions, Warnings = warnings };
    }

    // ── Cache warm ──

    public CacheWarmResult WarmCache(PackageQuery query)
    {
        var (located, warnings) = FindSingleMatch(query);
        var (_, _, cachedFiles) = ManifestForMatch(located, query);
        return new CacheWarmResult { Package = located.Display, CachedFiles = cachedFiles, Warnings = warnings };
    }

    // ── List ──

    public ListResponse List(ListQuery query)
    {
        if ((query.IncludeUnknown || query.IncludePinned) && !query.UpgradeOnly)
            throw new InvalidOperationException("--include-unknown and --include-pinned require --upgrade-available");

        if (query.Source is not null && query.Query is null && query.Id is null &&
            query.Name is null && query.Moniker is null && query.Tag is null && query.Command is null)
            throw new InvalidOperationException("list --source currently requires a query or explicit filter");

        bool hasFilter = ListQueryNeedsAvailableLookup(query);
        bool needsAvailable = hasFilter || query.UpgradeOnly;
        var warnings = new List<string>();
        var installed = InstalledPackages.Collect(query.InstallScope);
        if (!OperatingSystem.IsWindows())
            warnings.Add(InstalledStateUnsupportedWarning);

        if (needsAvailable && hasFilter)
        {
            var availableQuery = PackageQueryFromListQuery(query);
            var (matches, srcWarnings, _) = SearchLocated(availableQuery, SearchSemantics.Many);
            warnings.AddRange(srcWarnings);
            var candidates = matches.Select(m => m.Display).ToList();
            foreach (var pkg in installed)
                pkg.Correlated = CorrelateInstalledPackage(pkg, candidates, AllowLooseListCorrelation(query));
        }
        else if (needsAvailable)
        {
            warnings.AddRange(CorrelateAllInstalled(installed));
        }

        var filtered = installed
            .Where(p => ListPackageMatches(p, query) &&
                (!query.UpgradeOnly || InstalledPackageMatchesUpgradeFilter(p, query)))
            .OrderBy(ListSortWeight)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.LocalId)
            .ToList();

        var pins = query.UpgradeOnly ? ListPins() : [];
        var listMatches = filtered
            .Select(ListMatchFromInstalled)
            .Where(match => !query.UpgradeOnly || query.IncludePinned || !IsUpgradeBlockedByPin(match, pins))
            .ToList();

        bool truncated = false;
        if (query.Count is int limit)
        {
            truncated = listMatches.Count > limit;
            listMatches = listMatches.Take(limit).ToList();
        }

        return new ListResponse
        {
            Matches = listMatches,
            Warnings = warnings,
            Truncated = truncated,
        };
    }

    // ── Pin management ──

    public List<PinRecord> ListPins(string? sourceId = null) => PinStore.List(_appRoot, sourceId);
    public void AddPin(string packageId, string version, string sourceId, PinType pinType)
        => PinStore.Add(packageId, version, sourceId, pinType, _appRoot);
    public bool RemovePin(string packageId, string? sourceId = null) => PinStore.Remove(packageId, _appRoot, sourceId);
    public void ResetPins(string? sourceId = null) => PinStore.Reset(_appRoot, sourceId);

    // ── Install / Uninstall ──

    public (Manifest Manifest, string InstallerPath) DownloadInstaller(PackageQuery query, string downloadDir)
        => DownloadInstaller(new InstallRequest { Query = query }, downloadDir);

    public (Manifest Manifest, string InstallerPath) DownloadInstaller(InstallRequest request, string downloadDir)
    {
        var manifest = ResolveManifestForInstall(request);
        var installer = SelectInstaller(manifest.Installers, request.Query)
            ?? throw new InvalidOperationException("No applicable installer found for the current system");
        var url = installer.Url ?? throw new InvalidOperationException("Installer has no URL");

        Directory.CreateDirectory(downloadDir);
        var filename = request.Rename ?? url.Split('/').Last().Split('?').First();
        if (string.IsNullOrEmpty(filename)) filename = "installer";
        var dest = Path.Combine(downloadDir, filename);

        using var response = _client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        File.WriteAllBytes(dest, bytes);

        // Verify hash
        if (installer.Sha256 is not null)
        {
            var actual = Sha256Hex(bytes);
            if (!actual.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase) &&
                !request.IgnoreSecurityHash)
            {
                File.Delete(dest);
                throw new InvalidOperationException($"Installer hash mismatch. Expected: {installer.Sha256}, Got: {actual}");
            }
        }

        return (manifest, dest);
    }

    public InstallResult Install(PackageQuery query, bool silent)
    {
        return Install(new InstallRequest
        {
            Query = query,
            Mode = silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress,
        });
    }

    public InstallResult Install(PackageQuery query, InstallerMode mode)
        => Install(new InstallRequest { Query = query, Mode = mode });

    public InstallResult Install(InstallRequest request)
    {
        var manifest = ResolveManifestForInstall(request);
        if (!OperatingSystem.IsWindows())
        {
            var unsupportedInstallerType = (SelectInstaller(manifest.Installers, request.Query)?.InstallerType ?? "install")
                .ToLowerInvariant();
            return CreateUnsupportedActionResult(manifest.Id, manifest.Version, unsupportedInstallerType, InstallUnsupportedWarning);
        }

        var existingMatch = FindInstalledPackageForInstall(request, manifest);
        var noOpResult = CreateInstallNoOpResult(request, manifest, existingMatch);
        if (noOpResult is not null)
            return noOpResult;

        EnsurePackageAgreementsAccepted(manifest, request);
        InstallDependencies(manifest, request);

        if (request.DependenciesOnly)
        {
            return new InstallResult
            {
                PackageId = manifest.Id,
                Version = manifest.Version,
                InstallerPath = "",
                InstallerType = "dependencies",
                ExitCode = 0,
                Success = true,
                NoOp = false,
            };
        }

        var selectedInstaller = SelectInstaller(manifest.Installers, request.Query)
            ?? throw new InvalidOperationException("No applicable installer found");

        if (request.UninstallPrevious)
        {
            var uninstallQuery = request.Query with
            {
                Id = request.Query.Id ?? manifest.Id,
                Query = request.Query.Query ?? manifest.Id,
            };
            try
            {
                Uninstall(new UninstallRequest
                {
                    Query = uninstallQuery,
                    ProductCode = selectedInstaller.ProductCode,
                    Mode = InstallerMode.Silent,
                    AllVersions = true,
                    Force = true,
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "pinget-install");
        var (_, installerPath) = DownloadInstaller(request, tempDir);

        var installerType = (selectedInstaller.InstallerType ?? "exe").ToLowerInvariant();
        var exitCode = InstallerDispatch.Execute(installerPath, installerType, request, manifest, selectedInstaller);

        return new InstallResult
        {
            PackageId = manifest.Id,
            Version = manifest.Version,
            InstallerPath = installerPath,
            InstallerType = installerType,
            ExitCode = exitCode,
            Success = exitCode == 0,
            NoOp = false,
        };
    }

    public InstallResult Repair(RepairRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestPath) && !OperatingSystem.IsWindows())
        {
            var packageId = request.Query.Id ??
                request.Query.Name ??
                request.Query.Moniker ??
                request.Query.Query ??
                request.ProductCode ??
                "repair";
            return CreateUnsupportedActionResult(packageId, request.Query.Version ?? "", "repair", RepairUnsupportedWarning);
        }

        var warnings = new List<string> { RepairReinstallWarning };
        ListMatch? installedMatch = null;

        if (string.IsNullOrWhiteSpace(request.ManifestPath))
        {
            var installed = List(CreateRepairListQuery(request));
            warnings.AddRange(installed.Warnings);
            if (installed.Matches.Count == 0)
                throw new InvalidOperationException("No installed package matched the supplied repair query.");
            if (installed.Matches.Count > 1)
                throw new InvalidOperationException("Multiple installed packages matched the supplied repair query.");

            installedMatch = installed.Matches[0];
        }

        var installResult = Install(CreateRepairInstallRequest(request, installedMatch));
        return installResult with { Warnings = [.. warnings, .. installResult.Warnings] };
    }

    public InstallResult Uninstall(PackageQuery query, bool silent)
    {
        return Uninstall(new UninstallRequest
        {
            Query = query,
            Mode = silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress,
        });
    }

    public InstallResult Uninstall(UninstallRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            var (packageId, version) = DescribeUninstallTarget(request);
            return CreateUnsupportedActionResult(packageId, version, "uninstall", UninstallUnsupportedWarning);
        }

        var matches = ResolveUninstallMatches(request);
        var exitCode = 0;

        foreach (var installed in matches)
        {
            exitCode = InstallerDispatch.Uninstall(installed, request);
            if (exitCode != 0 && !request.AllVersions)
                break;
        }

        var primary = matches[0];
        return new InstallResult
        {
            PackageId = primary.Id,
            Version = primary.InstalledVersion,
            InstallerPath = "",
            InstallerType = primary.InstallerCategory ?? "uninstall",
            ExitCode = exitCode,
            Success = exitCode == 0,
            NoOp = false,
        };
    }

    private Manifest ResolveManifestForInstall(InstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManifestPath))
            return LoadManifestFromPath(request.ManifestPath!);

        var (located, _) = FindSingleMatch(request.Query);
        var (manifest, _, _) = ManifestForMatch(located, request.Query);
        return manifest;
    }

    private ListMatch? FindInstalledPackageForInstall(InstallRequest request, Manifest manifest)
    {
        var installedMatches = List(new ListQuery
        {
            Query = request.Query.Query,
            Id = request.Query.Id ?? manifest.Id,
            Name = request.Query.Name,
            Moniker = request.Query.Moniker,
            Source = request.Query.Source,
            Exact = request.Query.Exact || !string.IsNullOrWhiteSpace(manifest.Id),
            InstallScope = request.Query.InstallScope,
            Count = 100,
        }).Matches;

        return installedMatches.FirstOrDefault(match =>
            match.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            match.LocalId.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsurePackageAgreementsAccepted(Manifest manifest, InstallRequest request)
    {
        if (!request.AcceptPackageAgreements && manifest.Agreements.Count > 0)
            throw new InvalidOperationException("Package agreements are present; rerun with --accept-package-agreements to continue.");
    }

    private void InstallDependencies(Manifest manifest, InstallRequest request)
    {
        if (request.SkipDependencies)
            return;

        var dependencies = manifest.PackageDependencies
            .Concat(manifest.Installers.SelectMany(installer => installer.PackageDependencies))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dependencyId in dependencies)
        {
            Install(CreateDependencyInstallRequest(dependencyId, request));
        }
    }

    internal static InstallRequest CreateDependencyInstallRequest(string dependencyId, InstallRequest request) => new()
    {
        Query = new PackageQuery
        {
            Id = dependencyId,
            Source = request.DependencySource ?? request.Query.Source,
            Exact = true,
        },
        Mode = request.Mode == InstallerMode.Interactive ? InstallerMode.Interactive : InstallerMode.Silent,
        SkipDependencies = false,
        DependenciesOnly = false,
        AcceptPackageAgreements = request.AcceptPackageAgreements,
        NoUpgrade = true,
        IgnoreSecurityHash = request.IgnoreSecurityHash,
        DependencySource = request.DependencySource,
    };

    internal static ListQuery CreateRepairListQuery(RepairRequest request) => new()
    {
        Query = request.Query.Query,
        Id = request.Query.Id,
        Name = request.Query.Name,
        Moniker = request.Query.Moniker,
        ProductCode = request.ProductCode,
        Version = request.Query.Version,
        Source = request.Query.Source,
        Exact = request.Query.Exact,
        InstallScope = request.Query.InstallScope,
        Count = 100,
    };

    internal static InstallRequest CreateRepairInstallRequest(RepairRequest request, ListMatch? installedMatch) => new()
    {
        Query = request.Query with
        {
            Query = installedMatch is null ? request.Query.Query : null,
            Id = installedMatch?.Id ?? request.Query.Id,
            Name = installedMatch is null ? request.Query.Name : null,
            Moniker = installedMatch is null ? request.Query.Moniker : null,
            Source = request.Query.Source ?? installedMatch?.SourceName,
            Exact = true,
            Version = request.Query.Version ?? installedMatch?.InstalledVersion,
        },
        ManifestPath = request.ManifestPath,
        Mode = request.Mode,
        LogPath = request.LogPath,
        AcceptPackageAgreements = request.AcceptPackageAgreements,
        Force = true,
        IgnoreSecurityHash = request.IgnoreSecurityHash,
    };

    internal static InstallResult CreateUnsupportedActionResult(string packageId, string version, string installerType, string warning)
    {
        return new InstallResult
        {
            PackageId = string.IsNullOrWhiteSpace(packageId) ? "unknown" : packageId,
            Version = version ?? string.Empty,
            InstallerPath = string.Empty,
            InstallerType = installerType,
            ExitCode = 0,
            Success = true,
            NoOp = true,
            Warnings = [warning],
        };
    }

    internal static InstallResult? CreateInstallNoOpResult(InstallRequest request, Manifest manifest, ListMatch? existingMatch)
    {
        if (existingMatch is null)
            return null;

        if (request.NoUpgrade)
        {
            return new InstallResult
            {
                PackageId = manifest.Id,
                Version = existingMatch.InstalledVersion,
                InstallerPath = string.Empty,
                InstallerType = "install",
                ExitCode = 0,
                Success = true,
                NoOp = true,
                Warnings = ["Package is already installed; skipping because --no-upgrade was specified."],
            };
        }

        if (!request.Force &&
            !string.IsNullOrWhiteSpace(existingMatch.InstalledVersion) &&
            RestSource.CompareVersionStrings(existingMatch.InstalledVersion, manifest.Version) >= 0)
        {
            return new InstallResult
            {
                PackageId = manifest.Id,
                Version = existingMatch.InstalledVersion,
                InstallerPath = string.Empty,
                InstallerType = "install",
                ExitCode = 0,
                Success = true,
                NoOp = true,
                Warnings = ["Package is already installed and up to date; rerun with --force to reinstall."],
            };
        }

        return null;
    }

    private static (string PackageId, string Version) DescribeUninstallTarget(UninstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManifestPath))
        {
            var manifest = LoadManifestFromPath(request.ManifestPath!);
            return (manifest.Id, request.Query.Version ?? manifest.Version);
        }

        return (
            request.Query.Id
                ?? request.Query.Query
                ?? request.Query.Name
                ?? request.Query.Moniker
                ?? request.ProductCode
                ?? "unknown",
            request.Query.Version ?? string.Empty);
    }

    private List<ListMatch> ResolveUninstallMatches(UninstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManifestPath))
        {
            var manifest = LoadManifestFromPath(request.ManifestPath!);
            var manifestQuery = new PackageQuery
            {
                Query = manifest.Id,
                Id = manifest.Id,
                Name = manifest.Name,
                Exact = true,
                Version = request.Query.Version ?? manifest.Version,
            };

            request = request with
            {
                Query = manifestQuery,
                ProductCode = request.ProductCode ?? manifest.Installers
                    .Select(installer => installer.ProductCode)
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)),
            };
        }

        var listQuery = new ListQuery
        {
            Query = request.Query.Query,
            Id = request.Query.Id,
            Name = request.Query.Name,
            Moniker = request.Query.Moniker,
            ProductCode = request.ProductCode,
            Version = request.Query.Version,
            Source = request.Query.Source,
            Exact = request.Query.Exact,
            InstallScope = request.Query.InstallScope,
            Count = request.AllVersions ? null : 100,
        };

        var matches = List(listQuery).Matches;
        if (matches.Count == 0)
            throw new InvalidOperationException("No installed package found matching the query");
        if (!request.AllVersions && matches.Count > 1 && !request.Force)
            throw new InvalidOperationException("Multiple installed packages matched the query; refine the query or use --all-versions.");
        return request.AllVersions ? matches : [matches[0]];
    }

    private static Manifest LoadManifestFromPath(string manifestPath)
    {
        var resolved = ResolveManifestPath(manifestPath);
        return ParseYamlManifest(File.ReadAllBytes(resolved));
    }

    private static string ResolveManifestPath(string manifestPath)
    {
        if (File.Exists(manifestPath))
            return manifestPath;

        if (!Directory.Exists(manifestPath))
            throw new InvalidOperationException($"Manifest path not found: {manifestPath}");

        var manifestFile = Directory.EnumerateFiles(manifestPath, "*.yaml")
            .Concat(Directory.EnumerateFiles(manifestPath, "*.yml"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return manifestFile ?? throw new InvalidOperationException($"No manifest file found under: {manifestPath}");
    }

    // ── Internal search machinery ──

    private (List<LocatedMatch> Matches, List<string> Warnings, bool Truncated) SearchLocated(
        PackageQuery query, SearchSemantics semantics)
    {
        var indexes = ResolveSearchSourceIndexes(query.Source);
        var matches = new List<LocatedMatch>();
        var warnings = new List<string>();
        bool truncated = false;

        foreach (var index in indexes)
        {
            try
            {
                var (sourceMatches, srcTruncated) = SearchSource(index, query, semantics);
                truncated |= srcTruncated;
                matches.AddRange(sourceMatches);
            }
            catch
            {
                warnings.Add($"Failed when searching source; results will not be included: {_store.Sources[index].Name}");
            }
        }

        if (semantics == SearchSemantics.Many)
        {
            matches = matches
                .OrderByDescending(m => SearchMatchSortScore(m.Display, query))
                .ToList();
            int limit = query.Count ?? 50;
            if (matches.Count > limit)
            {
                truncated = true;
                matches = matches.Take(limit).ToList();
            }
        }

        return (matches, warnings, truncated);
    }

    private (List<LocatedMatch> Matches, bool Truncated) SearchSource(
        int sourceIndex, PackageQuery query, SearchSemantics semantics)
    {
        var source = _store.Sources[sourceIndex];
        return source.Kind switch
        {
            SourceKind.PreIndexed => SearchPreindexed(sourceIndex, query, semantics),
            SourceKind.Rest => SearchRest(sourceIndex, query, semantics),
            _ => ([], false)
        };
    }

    private (List<LocatedMatch> Matches, bool Truncated) SearchPreindexed(
        int sourceIndex, PackageQuery query, SearchSemantics semantics)
    {
        var conn = OpenPreindexedConnection(sourceIndex);
        var source = _store.Sources[sourceIndex];

        // Try V2 first, fall back to V1
        try
        {
            var (rows, truncated) = PreIndexedSource.SearchV2(conn, query, semantics);
            return (rows.Select(r => new LocatedMatch
            {
                Display = new SearchMatch
                {
                    SourceName = source.Name,
                    SourceKind = source.Kind,
                    Id = r.Id,
                    Name = r.Name,
                    Moniker = r.Moniker,
                    Version = r.Version,
                    MatchCriteria = r.MatchCriteria,
                },
                SourceIndex = sourceIndex,
                Locator = new PreIndexedV2Locator(r.PackageRowId, r.PackageHash),
            }).ToList(), truncated);
        }
        catch
        {
            // V2 failed, try V1
            var (rows, truncated) = PreIndexedSource.SearchV1(conn, query, semantics);
            return (rows.Select(r => new LocatedMatch
            {
                Display = new SearchMatch
                {
                    SourceName = source.Name,
                    SourceKind = source.Kind,
                    Id = r.Id,
                    Name = r.Name,
                    Moniker = r.Moniker,
                    Version = r.Version,
                    Channel = string.IsNullOrEmpty(r.Channel) ? null : r.Channel,
                    MatchCriteria = r.MatchCriteria,
                },
                SourceIndex = sourceIndex,
                Locator = new PreIndexedV1Locator(r.PackageRowId),
            }).ToList(), truncated);
        }
    }

    private (List<LocatedMatch> Matches, bool Truncated) SearchRest(
        int sourceIndex, PackageQuery query, SearchSemantics semantics)
    {
        var source = _store.Sources[sourceIndex];
        var info = RestSource.LoadInformation(_client, source, _appRoot);

        var (results, truncated) = RestSource.Search(_client, source, query, info, semantics);
        return (results.Select(r => new LocatedMatch
        {
            Display = new SearchMatch
            {
                SourceName = source.Name,
                SourceKind = source.Kind,
                Id = r.PackageId,
                Name = r.PackageName,
                Moniker = r.Moniker,
                Version = r.LatestVersion.Version,
                Channel = string.IsNullOrEmpty(r.LatestVersion.Channel) ? null : r.LatestVersion.Channel,
                MatchCriteria = r.MatchCriteria,
            },
            SourceIndex = sourceIndex,
            Locator = new RestLocator(r.PackageId, r.Versions),
        }).ToList(), truncated);
    }

    private SqliteConnection OpenPreindexedConnection(int sourceIndex)
    {
        var source = _store.Sources[sourceIndex];
        var indexPath = PreIndexedSource.IndexPath(source, _appRoot);
        if (!File.Exists(indexPath))
        {
            PreIndexedSource.Update(_client, source, _appRoot);
            source.LastUpdate = DateTime.UtcNow;
            if (!_useSystemWingetSources)
                SourceStoreManager.Save(_store, _appRoot);
        }

        var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private (LocatedMatch Match, List<string> Warnings) FindSingleMatch(PackageQuery query)
        => FindSingleMatchWithSemantics(query, SearchSemantics.Single);

    private (LocatedMatch Match, List<string> Warnings) FindSingleMatchWithSemantics(
        PackageQuery query, SearchSemantics semantics)
    {
        var (matches, warnings, _) = SearchLocated(query, semantics);

        if (matches.Count == 0)
            throw new InvalidOperationException("no package matched the supplied query");

        if (matches.Count > 1)
        {
            var choices = string.Join(", ", matches.Take(10)
                .Select(m => $"{m.Display.Name} [{m.Display.Id}] ({m.Display.SourceName})"));
            throw new InvalidOperationException($"multiple packages matched: {choices}");
        }

        return (matches[0], warnings);
    }

    private List<VersionKey> VersionsForMatch(LocatedMatch located, PackageQuery query)
    {
        var versions = located.Locator switch
        {
            PreIndexedV1Locator v1 => VersionsFromV1(located.SourceIndex, v1.PackageRowId),
            PreIndexedV2Locator v2 => VersionsFromV2(located.SourceIndex, v2.PackageRowId, v2.PackageHash),
            RestLocator rest => rest.Versions.ToList(),
            _ => throw new InvalidOperationException("Unknown locator type")
        };

        RestSource.SortVersionsDesc(versions);
        return versions;
    }

    private List<VersionKey> VersionsFromV1(int sourceIndex, long packageRowid)
    {
        var conn = OpenPreindexedConnection(sourceIndex);
        var rows = PreIndexedSource.QueryV1Versions(conn, packageRowid);
        return rows.Select(r => new VersionKey { Version = r.Version, Channel = r.Channel }).ToList();
    }

    private List<VersionKey> VersionsFromV2(int sourceIndex, long packageRowid, string packageHash)
    {
        var source = _store.Sources[sourceIndex];
        var conn = OpenPreindexedConnection(sourceIndex);
        var (entries, _) = PreIndexedSource.LoadV2VersionData(_client, conn, source, packageRowid, packageHash, _appRoot);
        return entries.Select(e => new VersionKey { Version = e.Version, Channel = "" }).ToList();
    }

    private (Manifest Manifest, object StructuredDocument, List<string> CachedFiles) ManifestForMatch(LocatedMatch located, PackageQuery query)
    {
        return located.Locator switch
        {
            PreIndexedV1Locator v1 => ManifestFromV1(located.SourceIndex, v1.PackageRowId, query),
            PreIndexedV2Locator v2 => ManifestFromV2(located.SourceIndex, v2.PackageRowId, v2.PackageHash, query),
            RestLocator rest => ManifestFromRest(located.SourceIndex, rest.PackageId, rest.Versions, query),
            _ => throw new InvalidOperationException("Unknown locator type")
        };
    }

    private (Manifest, object, List<string>) ManifestFromV1(int sourceIndex, long packageRowid, PackageQuery query)
    {
        var conn = OpenPreindexedConnection(sourceIndex);
        var source = _store.Sources[sourceIndex];
        var versions = PreIndexedSource.QueryV1Versions(conn, packageRowid);
        var selected = SelectV1Version(versions, query.Version, query.Channel);
        var relativePath = PreIndexedSource.ResolveV1RelativePath(conn, selected.PathPart);
        var bytes = PreIndexedSource.GetCachedSourceFile(_client, "V1_M", source, relativePath, selected.ManifestHash);
        var manifest = ParseYamlManifest(bytes);
        var structuredDocument = ParseYamlManifestDocuments(bytes);
        manifest = manifest with { Version = selected.Version, Channel = selected.Channel };
        return (manifest, structuredDocument, []);
    }

    private (Manifest, object, List<string>) ManifestFromV2(int sourceIndex, long packageRowid, string packageHash, PackageQuery query)
    {
        var source = _store.Sources[sourceIndex];
        var conn = OpenPreindexedConnection(sourceIndex);
        var (entries, vdFile) = PreIndexedSource.LoadV2VersionData(_client, conn, source, packageRowid, packageHash, _appRoot);
        var selected = SelectV2Version(entries, query.Version);
        var bytes = PreIndexedSource.GetCachedSourceFile(_client, "V2_M", source, selected.ManifestRelativePath, selected.ManifestHash);
        var manifest = ParseYamlManifest(bytes);
        var structuredDocument = ParseYamlManifestDocuments(bytes);
        manifest = manifest with { Version = selected.Version };
        return (manifest, structuredDocument, [vdFile]);
    }

    private (Manifest, object, List<string>) ManifestFromRest(int sourceIndex, string packageId, List<VersionKey> versions, PackageQuery query)
    {
        var source = _store.Sources[sourceIndex];
        var info = RestSource.LoadInformation(_client, source, _appRoot);
        var selected = SelectRestVersion(versions, query.Version, query.Channel);
        var (manifest, structuredDocument) = RestSource.FetchManifestWithDocuments(_client, source, info, packageId, selected.Version, selected.Channel);
        return (manifest, structuredDocument, []);
    }

    // ── Version selection ──

    private static PreIndexedSource.V1VersionRow SelectV1Version(
        List<PreIndexedSource.V1VersionRow> versions, string? requestedVersion, string? requestedChannel)
    {
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedVersion is not null)
        {
            var match = versions.FirstOrDefault(v => v.Version == requestedVersion
                && (requestedChannel is null || v.Channel == requestedChannel));
            if (match is not null) return match;
        }

        return versions[0]; // latest
    }

    private static PreIndexedSource.V2VersionDataEntry SelectV2Version(
        List<PreIndexedSource.V2VersionDataEntry> entries, string? requestedVersion)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedVersion is not null)
        {
            var match = entries.FirstOrDefault(e => e.Version == requestedVersion);
            if (match is not null) return match;
        }

        // Sort and return latest
        var sorted = entries.OrderByDescending(e => e.Version, new VersionComparer()).ToList();
        return sorted[0];
    }

    private static VersionKey SelectRestVersion(List<VersionKey> versions, string? requestedVersion, string? requestedChannel)
    {
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedVersion is not null)
        {
            var match = versions.FirstOrDefault(v => v.Version == requestedVersion
                && (requestedChannel is null || v.Channel == requestedChannel));
            if (match is not null) return match;
        }

        var sorted = versions.OrderByDescending(v => v.Version, new VersionComparer()).ToList();
        return sorted[0];
    }

    // ── Installer selection ──

    internal static Installer? SelectInstaller(List<Installer> installers, PackageQuery query)
    {
        if (installers.Count == 0)
            return null;

        var requestedLocale = query.Locale;
        var requestedArchitecture = query.InstallerArchitecture;
        var requestedType = query.InstallerType;
        var requestedScope = query.InstallScope;
        var requestedPlatform = query.Platform;
        var requestedOsVersion = query.OsVersion;
        var systemArchitecture = CurrentArchitecture();
        var systemPlatform = CurrentPlatform();
        var currentOsVersion = CurrentWindowsVersion();

        return installers
            .Where(installer => InstallerMatchesRequested(installer, requestedType, requestedScope, requestedPlatform, requestedOsVersion, systemPlatform, currentOsVersion))
            .Where(installer => InstallerMatchesArchitecture(installer, requestedArchitecture, systemArchitecture))
            .Select((installer, index) => new
            {
                Installer = installer,
                Index = index,
                Rank = InstallerRank(installer, requestedLocale, requestedArchitecture, systemArchitecture),
            })
            .OrderByDescending(item => item.Rank.Architecture)
            .ThenByDescending(item => item.Rank.Locale)
            .ThenByDescending(item => item.Rank.Commands)
            .ThenBy(item => item.Index)
            .Select(item => item.Installer)
            .FirstOrDefault();
    }

    // ── Manifest parsing ──

    internal static object ParseYamlManifestDocuments(byte[] bytes)
    {
        var yaml = System.Text.Encoding.UTF8.GetString(bytes);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var documents = new List<Dictionary<string, object?>>();
        var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
        {
            var doc = deserializer.Deserialize<object?>(parser);
            if (NormalizeYamlValue(doc) is Dictionary<string, object?> normalized)
                documents.Add(normalized);
        }

        if (documents.Count == 1 &&
            documents[0].TryGetValue("ManifestType", out var manifestType) &&
            string.Equals(manifestType?.ToString(), "merged", StringComparison.OrdinalIgnoreCase))
        {
            return SplitMergedManifestDocument(documents[0]);
        }

        if (documents.Count == 1)
            return documents[0];

        return documents;
    }

    internal static Manifest ParseYamlManifest(byte[] bytes)
    {
        var yaml = System.Text.Encoding.UTF8.GetString(bytes);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        // Merge all YAML documents into one dictionary (manifests can be multi-document)
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
        parser.Consume<YamlDotNet.Core.Events.StreamStart>();
        while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
        {
            var doc = deserializer.Deserialize<Dictionary<string, object?>>(parser);
            if (doc is not null)
            {
                foreach (var kvp in doc)
                    dict[kvp.Key] = kvp.Value;
            }
        }

        string GetStr(string key) => dict.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
        string? GetOptStr(string key) => dict.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;
        static List<string> ReadStringList(object? value)
        {
            if (value is null)
                return [];

            if (value is string single)
                return string.IsNullOrWhiteSpace(single) ? [] : [single];

            if (value is IList<object> list)
                return list.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToList();

            return [];
        }

        var tags = new List<string>();
        if (dict.TryGetValue("Tags", out var tagsObj) && tagsObj is IList<object> tagList)
            tags = tagList.Select(t => t?.ToString() ?? "").Where(t => t != "").ToList();

        var docs = new List<Documentation>();
        if (dict.TryGetValue("Documentations", out var docsObj) && docsObj is IList<object> docsList)
        {
            foreach (var d in docsList)
            {
                if (d is IDictionary<object, object> docDict)
                {
                    var url = docDict.TryGetValue("DocumentUrl", out var u) ? u?.ToString() : null;
                    if (url is not null)
                        docs.Add(new Documentation
                        {
                            Label = docDict.TryGetValue("DocumentLabel", out var l) ? l?.ToString() : null,
                            Url = url
                        });
                }
            }
        }

        var agreements = ReadAgreements(dict);

        // Top-level installer defaults (merged manifest format)
        string? topInstallerType = GetOptStr("InstallerType");
        string? topNestedInstallerType = GetOptStr("NestedInstallerType");
        string? topScope = GetOptStr("Scope");
        string? topProductCode = GetOptStr("ProductCode");
        string? topLocale = GetOptStr("InstallerLocale");
        string? topReleaseDate = GetOptStr("ReleaseDate");
        string? topPackageFamilyName = GetOptStr("PackageFamilyName");
        string? topUpgradeCode = GetOptStr("UpgradeCode");
        var topPlatforms = dict.TryGetValue("Platform", out var topPlatformValue)
            ? ReadStringList(topPlatformValue)
            : [];
        string? topMinimumOsVersion = GetOptStr("MinimumOSVersion");
        var topSwitches = ReadInstallerSwitches(dict);

        var installers = new List<Installer>();
        if (dict.TryGetValue("Installers", out var instObj) && instObj is IList<object> instList)
        {
            foreach (var inst in instList)
            {
                if (inst is IDictionary<object, object> instDict)
                {
                    string? InstStr(string key) => instDict.TryGetValue(key, out var v) ? v?.ToString() : null;
                    List<string> InstArr(string key)
                    {
                        if (!instDict.TryGetValue(key, out var v) || v is not IList<object> arr) return [];
                        return arr.Select(x => x?.ToString() ?? "").Where(s => s != "").ToList();
                    }

                    var switches = ReadInstallerSwitches(instDict).MergeWith(topSwitches);

                    var platforms = instDict.TryGetValue("Platform", out var platformValue)
                        ? ReadStringList(platformValue)
                        : [];

                    installers.Add(new Installer
                    {
                        Architecture = InstStr("Architecture"),
                        InstallerType = InstStr("InstallerType") ?? topInstallerType,
                        NestedInstallerType = InstStr("NestedInstallerType") ?? topNestedInstallerType,
                        Url = InstStr("InstallerUrl"),
                        Sha256 = InstStr("InstallerSha256"),
                        ProductCode = InstStr("ProductCode") ?? topProductCode,
                        Locale = InstStr("InstallerLocale") ?? topLocale,
                        Scope = InstStr("Scope") ?? topScope,
                        ReleaseDate = InstStr("ReleaseDate") ?? topReleaseDate,
                        PackageFamilyName = InstStr("PackageFamilyName") ?? topPackageFamilyName,
                        UpgradeCode = InstStr("UpgradeCode") ?? topUpgradeCode,
                        Platforms = platforms.Count > 0 ? platforms : [.. topPlatforms],
                        MinimumOsVersion = InstStr("MinimumOSVersion") ?? topMinimumOsVersion,
                        Switches = switches,
                        Commands = InstArr("Commands"),
                        PackageDependencies = InstArr("PackageDependencies"),
                    });
                }
            }
        }

        var dependencies = new List<string>();
        if (dict.TryGetValue("Dependencies", out var depsObj) && depsObj is IDictionary<object, object> depsDict)
        {
            if (depsDict.TryGetValue("PackageDependencies", out var pkgDeps) && pkgDeps is IList<object> pkgDepList)
            {
                foreach (var dep in pkgDepList)
                {
                    if (dep is IDictionary<object, object> depDict &&
                        depDict.TryGetValue("PackageIdentifier", out var pid))
                        dependencies.Add(pid?.ToString() ?? "");
                }
            }
        }

        return new Manifest
        {
            Id = GetStr("PackageIdentifier"),
            Name = GetOptStr("PackageName") ?? GetStr("PackageIdentifier"),
            Version = GetStr("PackageVersion"),
            Publisher = GetOptStr("Publisher"),
            Description = GetOptStr("Description") ?? GetOptStr("ShortDescription"),
            Moniker = GetOptStr("Moniker"),
            PackageUrl = GetOptStr("PackageUrl"),
            PublisherUrl = GetOptStr("PublisherUrl"),
            PublisherSupportUrl = GetOptStr("PublisherSupportUrl"),
            License = GetOptStr("License"),
            LicenseUrl = GetOptStr("LicenseUrl"),
            PrivacyUrl = GetOptStr("PrivacyUrl"),
            Author = GetOptStr("Author"),
            Copyright = GetOptStr("Copyright"),
            CopyrightUrl = GetOptStr("CopyrightUrl"),
            ReleaseNotes = GetOptStr("ReleaseNotes"),
            ReleaseNotesUrl = GetOptStr("ReleaseNotesUrl"),
            Tags = tags,
            Agreements = agreements,
            Documentation = docs,
            Installers = installers,
            PackageDependencies = dependencies,
        };
    }

    private static object? NormalizeYamlValue(object? value)
    {
        return value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            IList<object> list => list.Select(NormalizeYamlValue).ToList(),
            IDictionary<object, object> dict => dict.ToDictionary(
                kvp => kvp.Key.ToString() ?? "",
                kvp => NormalizeYamlValue(kvp.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => value.ToString(),
        };
    }

    private static List<Dictionary<string, object?>> SplitMergedManifestDocument(Dictionary<string, object?> merged)
    {
        var packageIdentifier = GetDocumentString(merged, "PackageIdentifier") ?? "";
        var packageVersion = GetDocumentString(merged, "PackageVersion") ?? "";
        var packageLocale = GetDocumentString(merged, "PackageLocale") ?? "en-US";
        var manifestVersion = GetDocumentString(merged, "ManifestVersion") ?? "1.10.0";

        var versionDocument = new Dictionary<string, object?>
        {
            ["PackageIdentifier"] = packageIdentifier,
            ["PackageVersion"] = packageVersion,
            ["DefaultLocale"] = packageLocale,
            ["ManifestType"] = "version",
            ["ManifestVersion"] = manifestVersion,
        };

        var defaultLocaleDocument = ProjectDocument(merged,
        [
            "PackageIdentifier",
            "PackageVersion",
            "PackageLocale",
            "Publisher",
            "PublisherUrl",
            "PublisherSupportUrl",
            "PrivacyUrl",
            "Author",
            "PackageName",
            "PackageUrl",
            "License",
            "LicenseUrl",
            "Copyright",
            "CopyrightUrl",
            "ShortDescription",
            "Description",
            "Moniker",
            "Tags",
            "Agreements",
            "ReleaseNotes",
            "ReleaseNotesUrl",
            "PurchaseUrl",
            "InstallationNotes",
            "Documentations",
            "Icons",
        ]);
        defaultLocaleDocument["PackageIdentifier"] = packageIdentifier;
        defaultLocaleDocument["PackageVersion"] = packageVersion;
        defaultLocaleDocument["PackageLocale"] = packageLocale;
        defaultLocaleDocument["ManifestType"] = "defaultLocale";
        defaultLocaleDocument["ManifestVersion"] = manifestVersion;

        var installerDocument = ProjectDocument(merged,
        [
            "PackageIdentifier",
            "PackageVersion",
            "Channel",
            "InstallerLocale",
            "Platform",
            "MinimumOSVersion",
            "InstallerType",
            "NestedInstallerType",
            "NestedInstallerFiles",
            "Scope",
            "InstallModes",
            "InstallerSwitches",
            "InstallerSuccessCodes",
            "ExpectedReturnCodes",
            "UpgradeBehavior",
            "Commands",
            "Protocols",
            "FileExtensions",
            "Dependencies",
            "PackageFamilyName",
            "ProductCode",
            "Capabilities",
            "RestrictedCapabilities",
            "Markets",
            "InstallerAbortsTerminal",
            "ReleaseDate",
            "InstallLocationRequired",
            "RequireExplicitUpgrade",
            "DisplayInstallWarnings",
            "UnsupportedOSArchitectures",
            "UnsupportedArguments",
            "AppsAndFeaturesEntries",
            "ElevationRequirement",
            "InstallationMetadata",
            "DownloadCommandProhibited",
            "RepairBehavior",
            "ArchiveBinariesDependOnPath",
            "Authentication",
            "Installers",
        ]);
        installerDocument["PackageIdentifier"] = packageIdentifier;
        installerDocument["PackageVersion"] = packageVersion;
        installerDocument["ManifestType"] = "installer";
        installerDocument["ManifestVersion"] = manifestVersion;

        return [versionDocument, defaultLocaleDocument, installerDocument];
    }

    private static Dictionary<string, object?> ProjectDocument(Dictionary<string, object?> source, IEnumerable<string> keys)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value))
                result[key] = value;
        }

        return result;
    }

    private static string? GetDocumentString(Dictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static List<PackageAgreement> ReadAgreements(IDictionary<string, object?> values)
    {
        if (!values.TryGetValue("Agreements", out var agreementsObj) || agreementsObj is not IList<object> agreementsList)
            return [];

        var agreements = new List<PackageAgreement>();
        foreach (var agreement in agreementsList)
        {
            if (agreement is not IDictionary<object, object> agreementDict)
                continue;

            agreements.Add(new PackageAgreement
            {
                Label = agreementDict.TryGetValue("AgreementLabel", out var label) ? label?.ToString() : null,
                Text = agreementDict.TryGetValue("Agreement", out var text) ? text?.ToString() : null,
                Url = agreementDict.TryGetValue("AgreementUrl", out var url) ? url?.ToString() : null,
            });
        }

        return agreements;
    }

    private static InstallerSwitches ReadInstallerSwitches(IDictionary<string, object?> values)
    {
        values.TryGetValue("InstallerSwitches", out var switchesObj);
        switchesObj ??= values.TryGetValue("Switches", out var legacySwitches) ? legacySwitches : null;
        return ReadInstallerSwitchesObject(switchesObj);
    }

    private static InstallerSwitches ReadInstallerSwitches(IDictionary<object, object> values)
    {
        values.TryGetValue("InstallerSwitches", out var switchesObj);
        switchesObj ??= values.TryGetValue("Switches", out var legacySwitches) ? legacySwitches : null;
        return ReadInstallerSwitchesObject(switchesObj);
    }

    private static InstallerSwitches ReadInstallerSwitchesObject(object? switchesObj)
    {
        if (switchesObj is not IDictionary<object, object> switches)
            return new InstallerSwitches();

        string? Get(string key) => switches.TryGetValue(key, out var value) ? value?.ToString() : null;
        return new InstallerSwitches
        {
            Silent = Get("Silent"),
            SilentWithProgress = Get("SilentWithProgress"),
            Interactive = Get("Interactive"),
            Custom = Get("Custom"),
            Log = Get("Log"),
            InstallLocation = Get("InstallLocation"),
        };
    }

    // ── List helpers ──

    private List<string> CorrelateAllInstalled(List<InstalledPackage> installed)
    {
        var allQuery = new PackageQuery { Count = 100_000 };
        var (matches, warnings, _) = SearchLocated(allQuery, SearchSemantics.Many);
        var candidates = matches.Select(m => m.Display).ToList();
        foreach (var pkg in installed)
            pkg.Correlated = CorrelateInstalledPackage(pkg, candidates, true);
        return warnings;
    }

    private static SearchMatch? CorrelateInstalledPackage(InstalledPackage pkg, List<SearchMatch> candidates, bool loose)
    {
        if (pkg.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            return null;

        var installedName = NormalizeCorrelationName(pkg.Name);
        var candidateNames = CorrelationNameCandidates(pkg.Name);

        SearchMatch? best = null;
        int bestScore = 0;
        foreach (var candidate in candidates)
        {
            var candidateNorm = NormalizeCorrelationName(candidate.Name);
            int score;
            if (candidate.Id.Equals(pkg.LocalId, StringComparison.OrdinalIgnoreCase))
                score = 1000;
            else if (candidateNames.Any(n => NormalizeCorrelationName(n).Equals(candidateNorm, StringComparison.OrdinalIgnoreCase)))
                score = 900;
            else if (loose && candidateNorm.Length >= 6 && installedName.Contains(candidateNorm, StringComparison.OrdinalIgnoreCase))
                score = 700;
            else
                score = 0;

            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    private static List<string> CorrelationNameCandidates(string name)
    {
        var candidates = new List<string> { name.Trim() };
        var trimmed = name.Trim();
        var parenIdx = trimmed.IndexOf(" (");
        if (parenIdx >= 0) trimmed = trimmed[..parenIdx];

        var words = new List<string>();
        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = token.Trim('(', ')').ToLowerInvariant();
            if (words.Count > 0 &&
                (lower is "x64" or "x86" or "arm64" || token.Any(char.IsAsciiDigit)))
                break;
            words.Add(token);
        }
        if (words.Count > 0) candidates.Add(string.Join(' ', words));
        return candidates.Distinct().ToList();
    }

    private static string NormalizeCorrelationName(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool ListPackageMatches(InstalledPackage pkg, ListQuery query)
    {
        var correlated = pkg.Correlated;

        if (query.Id is not null)
        {
            var localMatch = MatchesText(pkg.LocalId, query.Id, query.Exact);
            var correlatedMatch = correlated is not null && MatchesText(correlated.Id, query.Id, query.Exact);
            if (!localMatch && !correlatedMatch)
                return false;
        }

        if (query.Name is not null && !MatchesText(pkg.Name, query.Name, query.Exact))
            return false;

        if (query.ProductCode is not null &&
            !pkg.ProductCodes.Any(code => code.Equals(query.ProductCode, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (query.Version is not null &&
            !pkg.InstalledVersion.Equals(query.Version, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.Query is not null)
        {
            var localMatch = MatchesText(pkg.Name, query.Query, query.Exact) ||
                MatchesText(pkg.LocalId, query.Query, query.Exact);
            var correlatedMatch = correlated is not null &&
                (MatchesText(correlated.Id, query.Query, query.Exact) ||
                 MatchesText(correlated.Name, query.Query, query.Exact));
            if (!localMatch && !correlatedMatch)
                return false;
        }

        if (query.Source is not null &&
            (correlated?.SourceName is null ||
             !correlated.SourceName.Equals(query.Source, StringComparison.OrdinalIgnoreCase)))
            return false;

        if ((query.Moniker is not null || query.Tag is not null || query.Command is not null) &&
            correlated is null)
            return false;

        return true;
    }

    private static bool InstalledPackageMatchesUpgradeFilter(InstalledPackage pkg, ListQuery query) =>
        InstalledPackageHasUpgrade(pkg) ||
        (query.IncludeUnknown && InstalledPackageHasUnknownVersion(pkg) && pkg.Correlated is not null);

    internal static PinRecord? FindApplicablePin(ListMatch match, IReadOnlyList<PinRecord> pins)
    {
        PinRecord? sourceSpecific = null;
        PinRecord? sourceAgnostic = null;

        foreach (var pin in pins)
        {
            if (!pin.PackageId.Equals(match.Id, StringComparison.OrdinalIgnoreCase) &&
                !pin.PackageId.Equals(match.LocalId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pin.SourceId))
            {
                if (!string.IsNullOrWhiteSpace(match.SourceName) &&
                    pin.SourceId.Equals(match.SourceName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceSpecific = pin;
                    break;
                }
            }
            else if (sourceAgnostic is null)
            {
                sourceAgnostic = pin;
            }
        }

        return sourceSpecific ?? sourceAgnostic;
    }

    internal static bool IsUpgradeBlockedByPin(ListMatch match, IReadOnlyList<PinRecord> pins)
    {
        if (string.IsNullOrWhiteSpace(match.AvailableVersion))
            return false;

        var pin = FindApplicablePin(match, pins);
        if (pin is null)
            return false;

        return pin.PinType switch
        {
            PinType.Blocking => true,
            PinType.Gating or PinType.Pinning => !VersionMatchesPinPattern(match.AvailableVersion!, pin.Version),
            _ => false,
        };
    }

    internal static bool VersionMatchesPinPattern(string version, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;

        if (!pattern.Contains('*'))
            return version.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        if (pattern.Count(c => c == '*') == 1 && pattern.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1];
            return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return version.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static int ListSortWeight(InstalledPackage pkg)
    {
        if (pkg.LocalId.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (pkg.Name.Contains(".SparseApp") || pkg.LocalId.Contains(".SparseApp_"))
            return 1;
        return 2;
    }

    private static ListMatch ListMatchFromInstalled(InstalledPackage pkg)
    {
        string? availableVersion = null;
        if (pkg.Correlated?.Version is string av)
        {
            if (InstalledPackageHasUnknownVersion(pkg) ||
                RestSource.CompareVersionStrings(av, pkg.InstalledVersion) > 0)
                availableVersion = av;
        }
        return new()
        {
            Name = pkg.Name,
            Id = pkg.Correlated?.Id ?? pkg.LocalId,
            LocalId = pkg.LocalId,
            InstalledVersion = pkg.InstalledVersion,
            AvailableVersion = availableVersion,
            SourceName = pkg.Correlated?.SourceName,
            Publisher = pkg.Publisher,
            Scope = pkg.Scope,
            InstallerCategory = pkg.InstallerCategory,
            InstallLocation = pkg.InstallLocation,
            PackageFamilyNames = pkg.PackageFamilyNames,
            ProductCodes = pkg.ProductCodes,
            UpgradeCodes = pkg.UpgradeCodes,
        };
    }

    private static bool ListQueryNeedsAvailableLookup(ListQuery query)
        => query.Query is not null || query.Id is not null || query.Name is not null ||
           query.Moniker is not null || query.Tag is not null || query.Command is not null;

    private static bool AllowLooseListCorrelation(ListQuery query) => !query.Exact;

    private static bool MatchesText(string candidate, string query, bool exact) =>
        exact
            ? candidate.Equals(query, StringComparison.OrdinalIgnoreCase)
            : candidate.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool InstalledPackageHasUnknownVersion(InstalledPackage pkg) =>
        pkg.InstalledVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool InstalledPackageHasUpgrade(InstalledPackage pkg) =>
        pkg.Correlated?.Version is string availableVersion &&
        RestSource.CompareVersionStrings(availableVersion, pkg.InstalledVersion) > 0;

    private static bool InstallerMatchesRequested(
        Installer installer,
        string? requestedType,
        string? requestedScope,
        string? requestedPlatform,
        string? requestedOsVersion,
        string? systemPlatform,
        string? currentOsVersion) =>
        MatchesOptionalCaseInsensitive(installer.InstallerType, requestedType) &&
        InstallerMatchesScope(installer.Scope, requestedScope) &&
        InstallerMatchesPlatform(installer, requestedPlatform, systemPlatform) &&
        InstallerMatchesOsVersion(installer, requestedOsVersion, currentOsVersion);

    private static bool InstallerMatchesScope(string? installerScope, string? requestedScope) =>
        requestedScope is null || installerScope is null || installerScope.Equals(requestedScope, StringComparison.OrdinalIgnoreCase);

    private static bool InstallerMatchesPlatform(Installer installer, string? requestedPlatform, string? systemPlatform)
    {
        if (installer.Platforms.Count == 0)
            return true;

        var effectivePlatform = requestedPlatform ?? systemPlatform;
        if (string.IsNullOrWhiteSpace(effectivePlatform))
            return true;

        return installer.Platforms.Any(platform => platform.Equals(effectivePlatform, StringComparison.OrdinalIgnoreCase));
    }

    private static bool InstallerMatchesOsVersion(Installer installer, string? requestedOsVersion, string? currentOsVersion)
    {
        if (string.IsNullOrWhiteSpace(installer.MinimumOsVersion))
            return true;

        var effectiveOsVersion = requestedOsVersion ?? currentOsVersion;
        if (string.IsNullOrWhiteSpace(effectiveOsVersion))
            return true;

        if (TryParseVersion(installer.MinimumOsVersion!, out var minimumVersion) &&
            TryParseVersion(effectiveOsVersion, out var actualVersion))
        {
            return actualVersion.CompareTo(minimumVersion) >= 0;
        }

        return installer.MinimumOsVersion.Equals(effectiveOsVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool InstallerMatchesArchitecture(Installer installer, string? requestedArchitecture, string systemArchitecture)
    {
        if (installer.Architecture is null)
            return true;

        if (requestedArchitecture is not null)
            return installer.Architecture.Equals(requestedArchitecture, StringComparison.OrdinalIgnoreCase);

        return PreferredArchitectures(systemArchitecture)
            .Any(candidate => installer.Architecture.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static (int Architecture, int Locale, int Commands) InstallerRank(
        Installer installer,
        string? requestedLocale,
        string? requestedArchitecture,
        string systemArchitecture)
    {
        var architecture = ArchitectureRank(
            installer.Architecture,
            requestedArchitecture ?? systemArchitecture,
            requestedArchitecture is not null,
            systemArchitecture);
        var locale = LocaleRank(installer.Locale, requestedLocale);
        var commands = installer.Commands.Count == 0 ? 0 : 1;
        return (architecture, locale, commands);
    }

    private static int ArchitectureRank(
        string? installerArchitecture,
        string preferredArchitecture,
        bool strict,
        string systemArchitecture)
    {
        if (installerArchitecture is null)
            return 0;
        if (installerArchitecture.Equals(preferredArchitecture, StringComparison.OrdinalIgnoreCase))
            return 5;
        if (installerArchitecture.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (strict)
            return -1;

        var preferred = PreferredArchitectures(systemArchitecture);
        for (int i = preferred.Length - 1; i >= 0; i--)
        {
            if (installerArchitecture.Equals(preferred[i], StringComparison.OrdinalIgnoreCase))
                return preferred.Length - i;
        }
        return -1;
    }

    private static int LocaleRank(string? installerLocale, string? requestedLocale)
    {
        if (installerLocale is not null && requestedLocale is not null &&
            installerLocale.Equals(requestedLocale, StringComparison.OrdinalIgnoreCase))
            return 3;

        if (installerLocale is not null && requestedLocale is not null)
        {
            var installerLanguage = installerLocale.Split('-')[0];
            var requestedLanguage = requestedLocale.Split('-')[0];
            if (installerLanguage.Equals(requestedLanguage, StringComparison.OrdinalIgnoreCase))
                return 2;
            return -1;
        }

        if ((installerLocale is null) != (requestedLocale is null))
            return 1;

        return 0;
    }

    private static bool MatchesOptionalCaseInsensitive(string? value, string? requested) =>
        requested is null || (value is not null && value.Equals(requested, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseVersion(string value, out System.Version version)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length > 4)
            parts = parts.Take(parts.Length - 1).ToArray();

        return System.Version.TryParse(string.Join('.', parts), out version!);
    }

    private static string CurrentArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => "neutral",
    };

    private static string? CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "Windows.Desktop" : null;

    private static string? CurrentWindowsVersion() =>
        OperatingSystem.IsWindows() ? Environment.OSVersion.Version.ToString() : null;

    private static string[] PreferredArchitectures(string systemArchitecture) => systemArchitecture switch
    {
        "arm64" => ["arm64", "neutral", "x64", "x86"],
        "x64" => ["x64", "neutral", "x86"],
        "x86" => ["x86", "neutral"],
        _ => ["neutral"],
    };

    private static PackageQuery PackageQueryFromListQuery(ListQuery query) => new()
    {
        Query = query.Query,
        Id = query.Id,
        Name = query.Name,
        Moniker = query.Moniker,
        Tag = query.Tag,
        Command = query.Command,
        Source = query.Source,
        Count = 500,
        Exact = query.Exact,
        Version = null,
    };

    private List<int> ResolveSearchSourceIndexes(string? sourceName)
    {
        if (sourceName is not null)
            return ResolveSourceIndexes(sourceName);

        return _store.Sources
            .Select((source, index) => new { source, index })
            .Where(entry => !entry.source.Explicit)
            .OrderByDescending(entry => entry.source.Priority)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.index)
            .ToList();
    }

    private List<int> ResolveSourceIndexes(string? sourceName)
    {
        if (sourceName is null)
            return Enumerable.Range(0, _store.Sources.Count).ToList();

        for (int i = 0; i < _store.Sources.Count; i++)
        {
            if (_store.Sources[i].Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                return [i];
        }
        throw new InvalidOperationException($"Source '{sourceName}' not found.");
    }

    private void RefreshSystemWingetSources()
    {
        if (_useSystemWingetSources)
            _store = SystemWingetSourceStore.Load();
    }

    private List<SourceUpdateResult> UpdateSystemWingetSources(string? sourceName)
    {
        RefreshSystemWingetSources();
        var indexes = ResolveSourceIndexes(sourceName);
        var selectedNames = indexes.Select(index => _store.Sources[index].Name).ToList();
        var detail = SystemWingetSourceStore.UpdateSources(sourceName);
        RefreshSystemWingetSources();

        return selectedNames
            .Select(name => _store.Sources.FirstOrDefault(source => source.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(source => source is not null)
            .Select(source => new SourceUpdateResult
            {
                Name = source!.Name,
                Kind = source.Kind,
                Detail = detail,
            })
            .ToList();
    }

    // ── Scoring ──

    private static int SearchMatchSortScore(SearchMatch match, PackageQuery query)
    {
        var score = 0;

        if (query.Query is not null)
        {
            score = Math.Max(score, ScoreTextMatch(match.Name, query.Query, query.Exact, 140, 50, 30));
            score = Math.Max(score, ScoreTextMatch(match.Id, query.Query, query.Exact, 135, 45, 25));
            if (match.Moniker is not null)
                score = Math.Max(score, ScoreTextMatch(match.Moniker, query.Query, query.Exact, 130, 55, 35));
        }

        if (query.Id is not null)
            score = Math.Max(score, ScoreTextMatch(match.Id, query.Id, query.Exact, 220, 200, 180));

        if (query.Name is not null)
            score = Math.Max(score, ScoreTextMatch(match.Name, query.Name, query.Exact, 210, 190, 170));

        if (query.Moniker is not null && match.Moniker is not null)
            score = Math.Max(score, ScoreTextMatch(match.Moniker, query.Moniker, query.Exact, 205, 185, 165));

        if (query.Tag is not null &&
            TryParseMatchCriteria(match.MatchCriteria, out var tagField, out var tagValue) &&
            tagField == "Tag")
        {
            score = Math.Max(score, ScoreTextMatch(tagValue, query.Tag, query.Exact, 160, 150, 140));
        }

        if (query.Command is not null &&
            TryParseMatchCriteria(match.MatchCriteria, out var commandField, out var commandValue) &&
            commandField == "Command")
        {
            score = Math.Max(score, ScoreTextMatch(commandValue, query.Command, query.Exact, 155, 145, 135));
        }

        if (match.SourceKind == SourceKind.PreIndexed)
            score += 5;

        if (SearchMatchHasUnknownVersion(match))
            score = Math.Max(0, score - 10);

        return score;
    }

    private static int ScoreTextMatch(
        string candidate,
        string query,
        bool exact,
        int exactScore,
        int prefixScore,
        int substringScore)
    {
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase))
            return exactScore;

        if (exact)
            return 0;

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return prefixScore;

        return candidate.Contains(query, StringComparison.OrdinalIgnoreCase) ? substringScore : 0;
    }

    private static bool TryParseMatchCriteria(string? matchCriteria, out string field, out string value)
    {
        field = "";
        value = "";

        if (string.IsNullOrEmpty(matchCriteria))
            return false;

        var separator = matchCriteria.IndexOf(':');
        if (separator <= 0 || separator >= matchCriteria.Length - 1)
            return false;

        field = matchCriteria[..separator];
        value = matchCriteria[(separator + 1)..].TrimStart();
        return true;
    }

    private static bool SearchMatchHasUnknownVersion(SearchMatch match) =>
        match.Version is not null && match.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    // ── Utilities ──

    private static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private class VersionComparer : IComparer<string>
    {
        public int Compare(string? x, string? y) => RestSource.CompareVersionStrings(x ?? "", y ?? "");
    }

    private static string NormalizeSourceTrustLevel(string? trustLevel) =>
        string.IsNullOrWhiteSpace(trustLevel)
            ? "None"
            : trustLevel.Trim().ToLowerInvariant() switch
            {
                "none" => "None",
                "default" => "None",
                "trusted" => "Trusted",
                _ => throw new InvalidOperationException($"Unsupported source trust level: {trustLevel}")
            };
}
