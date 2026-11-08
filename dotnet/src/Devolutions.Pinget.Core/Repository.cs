using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Data.Sqlite;
using YamlDotNet.Core;

namespace Devolutions.Pinget.Core;

public class Repository : IDisposable
{
    private const string AppRootEnvironmentVariable = "PINGET_APPROOT";
    private const string SourceModeEnvironmentVariable = "PINGET_SOURCE_MODE";
    private const int SystemWingetMirrorAutoRefreshMinutes = 15;

    internal const string InstalledStateUnsupportedWarning = "Installed package discovery is not supported on this platform; returning no installed packages.";
    internal const string InstallUnsupportedWarning = "Installing packages is not supported on this platform; no changes were made.";
    internal const string UninstallUnsupportedWarning = "Uninstalling packages is not supported on this platform; no changes were made.";
    internal const string RepairUnsupportedWarning = "Repairing packages is not supported on this platform; no changes were made.";
    internal const string RepairReinstallWarning = "Pinget repair currently re-runs the package install flow for the selected package.";
    private static readonly TimeSpan InstallerDownloadConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallerDownloadIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallerDownloadTotalTimeout = TimeSpan.FromMinutes(30);
    private const int InstallerDownloadAttempts = 3;
    private const long InstallerDownloadMetadataInterval = 4 * 1024 * 1024;

    private static int s_sqliteNativeLibraryInitialized;

    private readonly string _appRoot;
    private readonly HttpClient _client;
    private readonly Action<RepositoryWarning>? _diagnostics;
    private readonly Func<Manifest, bool>? _packageAgreementsPrompt;
    private readonly TimeSpan? _preIndexedSourceAutoUpdateInterval;
    private SourceStore _store;
    private readonly EffectiveSourceMode _sourceMode;
    private bool UsesSystemWingetSources => _sourceMode != EffectiveSourceMode.Private;
    private bool MirrorsSystemWingetSources => _sourceMode == EffectiveSourceMode.SystemWingetMirror;
    private static readonly ConcurrentDictionary<string, object> s_preIndexedRefreshLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan s_preIndexedRefreshRetryInterval = TimeSpan.FromMinutes(5);
    private readonly object _preIndexedRefreshAttemptGate = new();
    private readonly Dictionary<string, (DateTime AttemptedAt, Exception? Exception, PreIndexedRefreshKind Kind)> _preIndexedRefreshAttempts = new(StringComparer.OrdinalIgnoreCase);

    private Repository(
        string appRoot,
        HttpClient client,
        SourceStore store,
        EffectiveSourceMode sourceMode,
        Action<RepositoryWarning>? diagnostics,
        Func<Manifest, bool>? packageAgreementsPrompt,
        TimeSpan? preIndexedSourceAutoUpdateInterval)
    {
        _appRoot = appRoot;
        _client = client;
        _store = store;
        _sourceMode = sourceMode;
        _diagnostics = diagnostics;
        _packageAgreementsPrompt = packageAgreementsPrompt;
        _preIndexedSourceAutoUpdateInterval = preIndexedSourceAutoUpdateInterval;
    }

    /// <summary>
    /// Opens the repository using either the default CLI storage root or the caller-supplied library options.
    /// </summary>
    public static Repository Open(RepositoryOptions? options = null)
    {
        options ??= new RepositoryOptions();
        EnsureSqliteNativeLibraryLoaded();
        var requestedAppRoot = options.AppRoot ?? Environment.GetEnvironmentVariable(AppRootEnvironmentVariable);
        var appRoot = SourceStoreManager.NormalizeAppRoot(requestedAppRoot);
        SourceStoreManager.EnsureAppDirs(appRoot);
        var requestedSourceMode = ResolveRequestedSourceMode(options.SourceMode, requestedAppRoot);
        var (sourceMode, store) = SourceStoreManager.LoadEffective(appRoot, requestedSourceMode);
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = InstallerDownloadConnectTimeout,
        });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return new Repository(appRoot, client, store, sourceMode, options.Diagnostics, options.PackageAgreementsPrompt, options.PreIndexedSourceAutoUpdateInterval);
    }

    internal static SourceMode ResolveRequestedSourceMode(SourceMode? configuredSourceMode, string? requestedAppRoot) =>
        ResolveRequestedSourceMode(
            configuredSourceMode,
            requestedAppRoot,
            Environment.GetEnvironmentVariable(SourceModeEnvironmentVariable));

    internal static SourceMode ResolveRequestedSourceMode(
        SourceMode? configuredSourceMode,
        string? requestedAppRoot,
        string? sourceModeFromEnvironment)
    {
        if (configuredSourceMode is { } explicitSourceMode)
            return explicitSourceMode;

        if (TryParseSourceMode(sourceModeFromEnvironment, out var parsedSourceMode))
            return parsedSourceMode;

        return requestedAppRoot is null ? SourceMode.Auto : SourceMode.Private;
    }

    internal static bool TryParseSourceMode(string? value, out SourceMode sourceMode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto":
                sourceMode = SourceMode.Auto;
                return true;
            case "private":
                sourceMode = SourceMode.Private;
                return true;
            case "system-winget-mirror":
            case "systemwingetmirror":
                sourceMode = SourceMode.SystemWingetMirror;
                return true;
            default:
                sourceMode = SourceMode.Auto;
                return false;
        }
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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Assembly.Location returns an empty string for assemblies embedded in a single-file app",
        Justification = "We fall back to AppContext.BaseDirectory when Assembly.Location is empty. The Assembly.Location probe is needed for the PowerShell-module deployment, where Core.dll sits next to runtimes/<rid>/native/e_sqlite3.dll inside the module while the host process (pwsh.exe) has a different base directory.")]
    private static void EnsureSqliteNativeLibraryLoaded()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (Interlocked.Exchange(ref s_sqliteNativeLibraryInitialized, 1) != 0)
            return;

        var assemblyDirectory = Path.GetDirectoryName(typeof(Repository).Assembly.Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            assemblyDirectory = AppContext.BaseDirectory;
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

    /// <summary>
    /// Deliberately not gated by the query refresh interval: a caller asking for the source list
    /// is looking straight at it, quite possibly right after adding a source through WinGet, so
    /// this keeps re-exporting on every call as it always has. Only the package queries trade
    /// that for one spawn per interval.
    /// </summary>
    public List<SourceRecord> ListSources()
    {
        RefreshSystemWingetSourcesForQuery(TimeSpan.Zero);
        return _store.Sources.ToList();
    }

    public void AddSource(string name, string arg, SourceKind kind, string trustLevel = "None", bool explicitSource = false, int priority = 0)
    {
        ValidateSourceArgument(arg);
        AddSourceUnvalidated(name, arg, kind, trustLevel, explicitSource, priority);
    }

    /// <summary>
    /// <c>source add</c> accepts only secure remote URLs, like WinGet's RestSourceFactory::Add
    /// (SOURCE_NOT_REMOTE / SOURCE_NOT_SECURE) and the pre-indexed forms (SOURCE_NOT_SECURE).
    /// Manifests carry installer URLs and hashes, so a plain-HTTP source can be tampered with
    /// in transit. Pinget fetches pre-indexed sources over HTTP(S) only, so a local path is
    /// "not remote" for both kinds.
    /// </summary>
    internal static void ValidateSourceArgument(string arg)
    {
        var trimmed = arg.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The source argument '{arg}' is not remote; only https:// sources are supported.");
        }

        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The source argument '{arg}' is not secure; only https:// sources are supported.");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new InvalidOperationException($"The source argument '{arg}' is not a valid https:// URL.");
    }

    /// <summary>
    /// Test seam: Core.Tests register loopback <c>http://</c> stub servers, which
    /// <see cref="AddSource"/> refuses.
    /// </summary>
    internal void AddSourceUnvalidated(string name, string arg, SourceKind kind, string trustLevel = "None", bool explicitSource = false, int priority = 0)
    {
        if (UsesSystemWingetSources)
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
        if (UsesSystemWingetSources)
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
        if (UsesSystemWingetSources)
        {
            SystemWingetSourceStore.RemoveSource(name);
            RefreshSystemWingetSources();
            return;
        }

        var source = _store.Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Source '{name}' not found.");

        _store.Sources.Remove(source);
        // Clean up cached state for this source. Best effort: a locked or partially
        // deleted cache must not keep the source configured.
        var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot, MirrorsSystemWingetSources);
        try
        {
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        PreIndexedSource.RemoveSourceFileCaches(_appRoot, source.Identifier);
        SourceStoreManager.Save(_store, _appRoot);
    }

    public void ResetSource(string name)
    {
        if (UsesSystemWingetSources)
        {
            SystemWingetSourceStore.ResetSource(name);
            RefreshSystemWingetSources();
            return;
        }

        var index = _store.Sources.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException($"Source '{name}' not found.");

        var source = _store.Sources[index];
        var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot, MirrorsSystemWingetSources);
        if (Directory.Exists(stateDir))
            Directory.Delete(stateDir, recursive: true);
        PreIndexedSource.RemoveSourceFileCaches(_appRoot, source.Identifier);

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
            source.ETag = null;
            source.LastModified = null;
        }

        SourceStoreManager.Save(_store, _appRoot);
    }

    public void ResetSources()
    {
        if (UsesSystemWingetSources)
        {
            SystemWingetSourceStore.ResetSources();
            RefreshSystemWingetSources();
            return;
        }

        foreach (var source in _store.Sources)
        {
            var stateDir = SourceStoreManager.SourceStateDir(source, _appRoot, MirrorsSystemWingetSources);
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
            PreIndexedSource.RemoveSourceFileCaches(_appRoot, source.Identifier);
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

    /// <summary>
    /// Admin settings gate security-sensitive arguments, as in WinGet: without them,
    /// <c>--ignore-security-hash</c> and <c>--manifest</c> are refused rather than silently honored.
    /// </summary>
    internal void EnsureAdminSettingEnabled(string name, string argument)
    {
        if (GetAdminSettings()[name]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(
                $"{argument} is disabled by policy; an administrator must enable it with 'pinget settings --enable {name}'.");
        }
    }

    private void EnsureHashOverrideAllowed(bool ignoreSecurityHash)
    {
        if (ignoreSecurityHash)
            EnsureAdminSettingEnabled("InstallerHashOverride", "--ignore-security-hash");
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
        if (_sourceMode == EffectiveSourceMode.SystemWingetDirect)
            return UpdateSystemWingetSources(sourceName);

        if (MirrorsSystemWingetSources)
            RefreshSystemWingetSources();

        var indexes = ResolveSourceIndexes(sourceName);
        var results = new List<SourceUpdateResult>();

        // One failing source must not stop the others or discard their refreshed metadata:
        // record the failure in its result, keep going, and save what succeeded.
        foreach (var index in indexes)
        {
            var source = _store.Sources[index];
            try
            {
                var detail = source.Kind switch
                {
                    SourceKind.PreIndexed => PreIndexedSource.Update(_client, source, _appRoot, MirrorsSystemWingetSources),
                    SourceKind.Rest => RestSource.UpdateRest(_client, source, _appRoot, MirrorsSystemWingetSources),
                    _ => "Unknown source kind"
                };
                source.LastUpdate = DateTime.UtcNow;
                results.Add(new SourceUpdateResult { Name = source.Name, Kind = source.Kind, Detail = detail });
            }
            catch (Exception ex)
            {
                results.Add(new SourceUpdateResult { Name = source.Name, Kind = source.Kind, Detail = ex.Message, Success = false });
            }
        }

        SaveStore();
        return results;
    }

    // ── Search ──

    public SearchResponse Search(PackageQuery query)
    {
        var (matches, warnings, failures, truncated) = SearchLocated(query, SearchSemantics.Many);
        return new SearchResponse
        {
            Matches = matches.Select(m => m.Display).ToList(),
            Warnings = warnings,
            SourceWarnings = failures.Select(f => f.Warning).ToList(),
            Truncated = truncated
        };
    }

    public List<Dictionary<string, object?>> SearchManifests(PackageQuery query)
    {
        var (matches, _, _, _) = SearchLocated(query, SearchSemantics.Many);
        return StructuredOutput.CollapseManifestResults(matches.Select(located =>
        {
            var (_, structuredDocument, _) = ManifestForMatch(located, query);
            return structuredDocument;
        }));
    }

    // ── Show ──

    public ShowResult Show(PackageQuery query)
    {
        var (located, warnings, sourceWarnings) = FindSingleMatchForShow(query, SearchSemantics.Single);
        return CreateShowResult(located, query, warnings, sourceWarnings);
    }

    public SerializableShowManifest ShowManifest(PackageQuery query) => Show(query).ToSerializableManifest();

    public ShowResult ShowFirstMatchAcrossSources(PackageQuery query)
    {
        if (query.Source is not null)
            return Show(query);

        var allWarnings = new List<string>();
        var allFailures = new List<SourceSearchFailure>();

        foreach (var sourceIndex in ResolveSearchSourceIndexes(null))
        {
            var sourceQuery = query with { Source = _store.Sources[sourceIndex].Name };
            var (matches, warnings, failures, _) = SearchLocated(sourceQuery, SearchSemantics.Single);
            allWarnings.AddRange(warnings);
            allFailures.AddRange(failures);

            if (matches.Count == 0)
                continue;

            if (matches.Count > 1)
                throw new MultiplePackageMatchesException(matches.Select(m => m.Display));

            return CreateShowResult(matches[0], query, allWarnings, allFailures.Select(f => f.Warning).ToList());
        }

        if (allFailures.Count > 0)
            throw new SourceSearchException(allFailures[0].Warning, allFailures[0].Exception);

        throw new InvalidOperationException("no package matched the supplied query");
    }

    private ShowResult CreateShowResult(
        LocatedMatch located,
        PackageQuery query,
        List<string> warnings,
        List<RepositoryWarning> sourceWarnings)
    {
        try
        {
            var (manifest, structuredDocument, cachedFiles) = ManifestForMatch(located, query);
            return new ShowResult
            {
                Package = located.Display,
                Manifest = manifest,
                SelectedInstaller = SelectInstaller(manifest.Installers, query),
                CachedFiles = cachedFiles,
                Warnings = warnings,
                SourceWarnings = sourceWarnings,
                StructuredDocument = structuredDocument,
            };
        }
        catch (Exception ex)
        {
            var warning = CreateSourceWarning(located.SourceIndex, "manifest", ex);
            EmitDiagnostic(warning);
            throw new SourceSearchException(warning, UnwrapSourceOperationException(ex));
        }
    }

    public VersionsResult ShowVersions(PackageQuery query)
    {
        var (located, warnings, sourceWarnings) = FindSingleMatchForShow(query, SearchSemantics.Single);
        var versions = VersionsForMatch(located, query);
        return new VersionsResult { Package = located.Display, Versions = versions, Warnings = warnings, SourceWarnings = sourceWarnings };
    }

    public VersionsResult SearchVersions(PackageQuery query)
    {
        var (located, warnings, sourceWarnings) = FindSingleMatchWithSemantics(query, SearchSemantics.Many);
        var versions = VersionsForMatch(located, query);
        return new VersionsResult { Package = located.Display, Versions = versions, Warnings = warnings, SourceWarnings = sourceWarnings };
    }

    // ── Cache warm ──

    public CacheWarmResult WarmCache(PackageQuery query)
    {
        var (located, warnings, sourceWarnings) = FindSingleMatch(query);
        var (_, _, cachedFiles) = ManifestForMatch(located, query);
        return new CacheWarmResult { Package = located.Display, CachedFiles = cachedFiles, Warnings = warnings, SourceWarnings = sourceWarnings };
    }

    // ── List ──

    public ListResponse List(ListQuery query) => List(query, applyPins: true);

    /// <summary>
    /// Like <see cref="List(ListQuery)"/>, but never hides upgrade rows because of a pin. Explicit
    /// (named) upgrades use this: the caller evaluates the pin against the requested target version and
    /// reports a pinned package instead of silently finding nothing to upgrade.
    /// </summary>
    public ListResponse ListIgnoringPins(ListQuery query) => List(query, applyPins: false);

    private ListResponse List(ListQuery query, bool applyPins)
    {
        var sourceRefreshWarning = RefreshSystemWingetSourcesForQuery();

        if ((query.IncludeUnknown || query.IncludePinned) && !query.UpgradeOnly)
            throw new InvalidOperationException("--include-unknown and --include-pinned require --upgrade-available");

        if (query.Source is not null && !query.UpgradeOnly && query.Query is null && query.Id is null &&
            query.Name is null && query.Moniker is null && query.Tag is null && query.Command is null)
            throw new InvalidOperationException("list --source currently requires a query or explicit filter");

        bool hasFilter = ListQueryNeedsAvailableLookup(query);
        bool needsAvailable = hasFilter || query.UpgradeOnly;
        var warnings = new List<string>();
        if (sourceRefreshWarning is not null)
            warnings.Add(sourceRefreshWarning);

        var installed = InstalledPackages.Collect(query.InstallScope);
        if (!OperatingSystem.IsWindows())
            warnings.Add(InstalledStateUnsupportedWarning);

        // Even plain `list` should canonicalize installed package ids and
        // sources when the local package can be correlated back to a source
        // catalog entry. Available-version lookups stay gated below.
        warnings.AddRange(CorrelateInstalledViaInstalledDb(installed, query.Source));
        warnings.AddRange(CorrelateInstalledViaIndex(installed, query.Source));
        warnings.AddRange(CorrelateInstalledByNormalizedIdentity(installed, query.Source));

        if (needsAvailable)
        {
            var availableQuery = PackageQueryFromListQuery(query);
            if (hasFilter)
            {
                var (matches, srcWarnings, _, _) = SearchLocated(availableQuery, SearchSemantics.Many);
                warnings.AddRange(srcWarnings);
                var candidates = matches.Select(m => m.Display).ToList();
                foreach (var pkg in installed)
                {
                    if (pkg.Correlated is not null) continue;
                    if (CorrelateInstalledPackageFromLocated(pkg, matches) is { } locatedMatch)
                    {
                        if (pkg.Name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(locatedMatch.Name))
                        {
                            pkg.Name = locatedMatch.Name;
                        }
                        pkg.Correlated = locatedMatch;
                        continue;
                    }
                    pkg.Correlated = CorrelateInstalledPackage(pkg, candidates, AllowLooseListCorrelation(query));
                }
            }
            else if (!query.UpgradeOnly)
            {
                warnings.AddRange(CorrelateAllInstalled(installed));
            }
        }

        if (needsAvailable)
        {
            // Enrich rows that correlated via the name-based fallback. Those
            // rows skipped the v2-index aMiV/aMaV pass that runs inside
            // CorrelateInstalledViaIndex, so they still carry the raw ARP
            // DisplayVersion (e.g. `JetBrains Rider 2025.3.0.1` showing
            // build `253.28294.112` instead of the marketing `2025.3.0.1`).
            // Also resolves MSIX placeholder names like
            // `ms-resource:appDisplayName` to the catalog's display name —
            // winget pulls these through Windows.Management.Deployment; the
            // catalog name is the parity-faithful substitute pinget has
            // available without taking a WinRT dependency.
            warnings.AddRange(EnrichCorrelatedViaIndex(installed, query.Source));
            warnings.AddRange(ResolveRestCorrelatedVersions(installed));
        }

        // For `upgrade`, collapse multiple installed entries that map to the
        // same catalog package id (side-by-side .NET SDKs, several runtime
        // versions of WindowsAppRuntime, the MSIX shim alongside an ARP
        // install of Edge, etc.). Keep the entry with the highest
        // installed_version so the upgrade comparison runs against the
        // user's newest install — matches winget's one-row-per-id output.
        if (query.UpgradeOnly)
            installed = DedupeCorrelatedForUpgrade(installed);

        var filtered = installed
            .Where(p => ListPackageMatches(p, query) &&
                (!query.UpgradeOnly || InstalledPackageMatchesUpgradeFilter(p, query)))
            .OrderBy(ListSortWeight)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.LocalId)
            .ToList();

        // --include-pinned relaxes only Pinning pins; Blocking and Gating pins still apply.
        var pins = query.UpgradeOnly && applyPins ? ListPins() : [];
        var listMatches = filtered
            .Select(ListMatchFromInstalled)
            .Where(match => !query.UpgradeOnly || !IsUpgradeBlockedByPin(match, pins, query.IncludePinned))
            .ToList();
        if (!query.UpgradeOnly)
            listMatches = SuppressDuplicateAvailableVersions(listMatches);

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

    public DownloadResult DownloadInstaller(PackageQuery query, string downloadDir)
        => DownloadInstaller(new InstallRequest { Query = query }, downloadDir);

    public DownloadResult DownloadInstaller(InstallRequest request, string downloadDir)
    {
        EnsureHashOverrideAllowed(request.IgnoreSecurityHash);
        var (manifest, manifestDocuments) = ResolveManifestAndDocumentsForInstall(request);
        var installer = SelectInstaller(manifest.Installers, request.Query)
            ?? throw new InvalidOperationException("No applicable installer found for the current system");
        // winget download gates: the package agreements must be accepted (DownloadCommand's
        // ShowPromptsForSinglePackage), then an installer marked DownloadCommandProhibited is refused
        // (EnsureSupportForDownload). Install does not apply these download-only checks.
        EnsurePackageAgreementsAccepted(manifest, request);
        EnsureInstallerDownloadSupported(installer);
        var installerPath = FetchInstaller(request, manifest, installer, request.Rename, downloadDir);
        var manifestYaml = BuildDownloadManifestYaml(manifest, manifestDocuments, installer);
        var manifestFileName = DeriveDownloadManifestFilename(manifest, installer);
        var manifestPath = Path.Combine(downloadDir, manifestFileName);
        File.WriteAllText(manifestPath, manifestYaml);

        return new DownloadResult
        {
            Manifest = manifest,
            InstallerPath = installerPath,
            ManifestPath = manifestPath,
        };
    }

    internal const string DownloadCommandProhibitedError =
        "Installer is prohibited from being downloaded for later offline installation.";

    internal const string UpgradeBlockedByManifestError =
        "The package cannot be upgraded using WinGet. Please use the method provided by the publisher for upgrading this package.";

    /// <summary>WinGet <c>APPINSTALLER_CLI_ERROR_DOWNLOAD_COMMAND_PROHIBITED</c>.</summary>
    internal static void EnsureInstallerDownloadSupported(Installer installer)
    {
        if (installer.DownloadCommandProhibited)
            throw new InvalidOperationException(DownloadCommandProhibitedError);
    }

    /// <summary>True when installing <paramref name="targetVersion"/> over <paramref name="installedVersion"/> changes the installed version.</summary>
    internal static bool InstallIsUpdate(string installedVersion, string targetVersion)
        => RestSource.CompareVersionStrings(installedVersion, targetVersion) != 0;

    internal static bool UpgradeBehaviorIs(Installer installer, string behavior)
        => string.Equals(installer.UpgradeBehavior?.Trim(), behavior, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// WinGet <c>APPINSTALLER_CLI_ERROR_INSTALL_UPGRADE_NOT_SUPPORTED</c>: <c>UpgradeBehavior: deny</c> blocks
    /// updating an existing installation with this installer.
    /// </summary>
    internal static void EnsureUpgradeAllowed(Installer installer, bool isUpdate)
    {
        if (isUpdate && UpgradeBehaviorIs(installer, "deny"))
            throw new InvalidOperationException(UpgradeBlockedByManifestError);
    }

    /// <summary>
    /// Fetches the file of an installer the caller already selected, without emitting the YAML manifest.
    /// The install flow passes the manifest and installer it resolved, so the downloaded (and hash-verified)
    /// file always belongs to the installer that is then dispatched; resolving again here could return a
    /// different manifest (a newly published version, a source refresh) whose file would run with the first
    /// one's type and switches.
    /// </summary>
    internal string FetchInstaller(InstallRequest request, Manifest manifest, Installer installer, string? rename, string downloadDir)
    {
        EnsureHashOverrideAllowed(request.IgnoreSecurityHash);
        if (string.Equals(installer.InstallerType, "msstore", StringComparison.OrdinalIgnoreCase) &&
            installer.Url is null)
        {
            throw new InvalidOperationException("Downloading Microsoft Store (msstore) installers is not supported; use 'install' instead.");
        }
        var url = installer.Url ?? throw new InvalidOperationException("Installer has no URL");
        var installerType = (installer.InstallerType ?? "exe").ToLowerInvariant();
        var expectedHash = GetInstallerExpectedHash(installer, request.IgnoreSecurityHash);

        var filename = InstallerDownloadFileName(url, rename, installerType, manifest);
        Directory.CreateDirectory(downloadDir);
        var dest = Path.Combine(downloadDir, filename);

        var actualHash = DownloadInstallerToFile(url, dest);

        if (expectedHash is not null &&
            !actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) &&
            !request.IgnoreSecurityHash)
        {
            File.Delete(dest);
            throw new InvalidOperationException($"Installer hash mismatch. Expected: {expectedHash}, Got: {actualHash}");
        }

        return dest;
    }

    internal const string MissingInstallerHashError =
        "The manifest does not provide an InstallerSha256 for this installer; use --ignore-security-hash to install it anyway.";

    /// <summary>
    /// Returns the hash the downloaded installer must match, or null when no verification applies (the user
    /// overrode it, or an msstore installer has no hash). Like WinGet, every other installer type requires
    /// InstallerSha256.
    /// </summary>
    internal static string? GetInstallerExpectedHash(Installer installer, bool ignoreSecurityHash)
    {
        var expectedHash = string.IsNullOrWhiteSpace(installer.Sha256) ? null : installer.Sha256;
        if (expectedHash is not null || ignoreSecurityHash)
            return expectedHash;
        if (string.Equals(installer.InstallerType, "msstore", StringComparison.OrdinalIgnoreCase))
            return null;
        throw new InvalidOperationException(MissingInstallerHashError);
    }

    private const int MaxInstallerFileNameLength = 200;
    private static readonly char[] InvalidInstallerFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Chooses the on-disk name of a downloaded installer. A --rename value must already be a plain file
    /// name. A name taken from the URL is percent-decoded and used only when it is a safe single path
    /// segment (otherwise the &lt;id&gt;.&lt;version&gt; fallback WinGet uses applies). Either name always receives an
    /// extension that matches the installer type so it cannot be launched as something else (or, without one,
    /// fail to launch).
    /// </summary>
    internal static string InstallerDownloadFileName(string url, string? rename, string installerType, Manifest manifest)
    {
        if (rename is not null)
        {
            if (!IsSafeFileName(rename))
                throw new InvalidOperationException($"Invalid --rename value '{rename}': it must be a plain file name without path separators.");
            return WithInstallerExtension(rename, installerType);
        }

        var path = url.Split('?', '#')[0];
        var segment = path[(path.LastIndexOf('/') + 1)..];
        var decoded = Uri.UnescapeDataString(segment);
        var name = IsSafeFileName(decoded) ? decoded : MakeSafeFileName($"{manifest.Id}.{manifest.Version}");
        return WithInstallerExtension(name, installerType);
    }

    internal static bool IsSafeFileName(string name)
    {
        if (name.Length == 0 ||
            name.Length > MaxInstallerFileNameLength ||
            name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' ') ||
            name.Any(ch => char.IsControl(ch) || InvalidInstallerFileNameChars.Contains(ch)))
            return false;

        var stem = name.Split('.')[0].TrimEnd();
        return !IsReservedWindowsDeviceName(stem);
    }

    private static bool IsReservedWindowsDeviceName(string stem)
    {
        var upper = stem.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" ||
            ((upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) &&
             upper.Length == 4 && upper[3] is >= '1' and <= '9');
    }

    private static string MakeSafeFileName(string value)
    {
        var replaced = new string(value.Select(ch => char.IsControl(ch) || InvalidInstallerFileNameChars.Contains(ch) ? '_' : ch).ToArray());
        var trimmed = replaced.Trim().TrimEnd('.').TrimEnd();
        if (trimmed.Length > MaxInstallerFileNameLength)
            trimmed = trimmed[..MaxInstallerFileNameLength];
        return IsSafeFileName(trimmed) ? trimmed : "installer";
    }

    /// <summary>
    /// Extensions accepted for each installer type; the first one is appended when the name does not already
    /// end with an accepted extension.
    /// </summary>
    private static string[] InstallerTypeExtensions(string installerType) => installerType switch
    {
        "exe" or "burn" or "inno" or "nullsoft" or "nsis" or "portable" => [".exe"],
        "msi" or "wix" => [".msi"],
        "msix" or "appx" => [".msix", ".msixbundle", ".appx", ".appxbundle"],
        "zip" => [".zip"],
        _ => [],
    };

    private static string WithInstallerExtension(string name, string installerType)
    {
        var extensions = InstallerTypeExtensions(installerType);
        if (extensions.Length == 0 || extensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return name;
        return name + extensions[0];
    }

    private string DownloadInstallerToFile(string url, string dest)
    {
        var partPath = SidecarPath(dest, "part");
        var metadataPath = SidecarPath(dest, "part.json");
        var startedAt = DateTimeOffset.UtcNow;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= InstallerDownloadAttempts; attempt++)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            if (elapsed >= InstallerDownloadTotalTimeout)
                break;

            try
            {
                TryDownloadInstallerToPartAsync(
                    url,
                    partPath,
                    metadataPath,
                    InstallerDownloadTotalTimeout - elapsed).GetAwaiter().GetResult();

                var actualHash = Sha256FileHex(partPath);
                File.Move(partPath, dest, overwrite: true);
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
                return actualHash;
            }
            catch (Exception ex) when (IsRetryableInstallerDownloadException(ex))
            {
                lastError = ex;
            }
        }

        throw lastError ?? new TimeoutException("Installer download timed out.");
    }

    private async Task TryDownloadInstallerToPartAsync(string url, string partPath, string metadataPath, TimeSpan remaining)
    {
        var metadata = ReadInstallerDownloadMetadata(metadataPath, partPath, url);
        var offset = metadata?.Offset ?? 0;
        using var totalCts = new CancellationTokenSource(remaining);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
            var ifRange = InstallerDownloadIfRange(metadata!);
            if (ifRange is not null)
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        }

        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, totalCts.Token)
            .ConfigureAwait(false);

        long? totalLength;
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent && offset > 0)
        {
            totalLength = ParseContentRange(response.Content.Headers.ContentRange, offset);
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            offset = 0;
            totalLength = response.Content.Headers.ContentLength;
            if (File.Exists(partPath))
                File.Delete(partPath);
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            DeleteIfExists(partPath);
            DeleteIfExists(metadataPath);
            throw new InstallerDownloadRetryException("Installer partial download could not be resumed; retrying from the beginning.");
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        else
        {
            throw new InvalidOperationException($"Unexpected installer download HTTP status {(int)response.StatusCode}.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(totalCts.Token).ConfigureAwait(false);
        await using var output = offset == 0
            ? File.Create(partPath)
            : new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None);

        var downloadMetadata = new InstallerDownloadMetadata(
            url,
            StrongETag(response),
            response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            totalLength,
            offset);
        WriteInstallerDownloadMetadata(metadataPath, downloadMetadata);

        var buffer = new byte[128 * 1024];
        var nextMetadataOffset = offset + InstallerDownloadMetadataInterval;
        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            idleCts.CancelAfter(InstallerDownloadIdleTimeout);
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token).ConfigureAwait(false);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), totalCts.Token).ConfigureAwait(false);
            offset += read;

            if (offset >= nextMetadataOffset)
            {
                await output.FlushAsync(totalCts.Token).ConfigureAwait(false);
                downloadMetadata = downloadMetadata with { Offset = offset };
                WriteInstallerDownloadMetadata(metadataPath, downloadMetadata);
                nextMetadataOffset = offset + InstallerDownloadMetadataInterval;
            }
        }

        await output.FlushAsync(totalCts.Token).ConfigureAwait(false);
        if (totalLength is not null && offset != totalLength.Value)
            throw new InstallerDownloadRetryException($"Installer download ended early: received {offset} of {totalLength.Value} bytes.");

        WriteInstallerDownloadMetadata(metadataPath, downloadMetadata with { Offset = offset, TotalLength = totalLength });
    }

    internal sealed record InstallerDownloadMetadata(string Url, string? ETag, string? LastModified, long? TotalLength, long Offset);

    private static InstallerDownloadMetadata? ReadInstallerDownloadMetadata(string metadataPath, string partPath, string url)
    {
        InstallerDownloadMetadata? metadata = null;
        if (File.Exists(metadataPath))
        {
            try
            {
                metadata = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(metadataPath), InstallerDownloadMetadataContext.Default.InstallerDownloadMetadata);
            }
            catch (System.Text.Json.JsonException)
            {
                metadata = null;
            }
            catch (IOException)
            {
                metadata = null;
            }
        }

        if (metadata is null || metadata.Url != url || metadata.Offset <= 0 || !File.Exists(partPath))
        {
            DeleteIfExists(partPath);
            DeleteIfExists(metadataPath);
            return null;
        }

        var partLength = new FileInfo(partPath).Length;
        var offset = Math.Min(metadata.Offset, partLength);
        if (offset <= 0)
        {
            DeleteIfExists(partPath);
            DeleteIfExists(metadataPath);
            return null;
        }

        using (var stream = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(offset);
        }

        return metadata with { Offset = offset };
    }

    private static void WriteInstallerDownloadMetadata(string metadataPath, InstallerDownloadMetadata metadata) =>
        File.WriteAllBytes(metadataPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(metadata, InstallerDownloadMetadataContext.Default.InstallerDownloadMetadata));

    internal static string? InstallerDownloadIfRange(InstallerDownloadMetadata metadata) =>
        metadata.ETag is { Length: > 0 } etag && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            ? etag
            : metadata.LastModified;

    internal static long? ParseContentRange(System.Net.Http.Headers.ContentRangeHeaderValue? value, long expectedStart)
    {
        if (value?.From is null)
            throw new InvalidOperationException("Partial installer response missing Content-Range.");
        if (value.From.Value != expectedStart)
            throw new InvalidOperationException($"Partial installer response started at {value.From.Value}, expected {expectedStart}.");
        return value.Length;
    }

    private static string? StrongETag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;

    private static string Sha256FileHex(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal static string SidecarPath(string path, string suffix) => path + "." + suffix;

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsRetryableInstallerDownloadException(Exception exception) =>
        exception is InstallerDownloadRetryException or HttpRequestException or IOException or TaskCanceledException or OperationCanceledException;

    private sealed class InstallerDownloadRetryException(string message) : Exception(message);

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
        => InstallCore(request, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="visited"/> holds the ids of every package already on the current dependency
    /// chain or already handled, shared across the whole recursion so a dependency cycle (or a
    /// self-dependency) is skipped instead of recursing forever, as WinGet's dependency graph does.
    /// </summary>
    private InstallResult InstallCore(InstallRequest request, HashSet<string> visited)
    {
        // Refuse a disabled override before any side effect (agreement prompt, dependency
        // installs) and on paths that never fetch an installer (no-op, msstore, dependencies-only).
        EnsureHashOverrideAllowed(request.IgnoreSecurityHash);
        request = ApplyInstallScope(request);
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

        request = ApplyInstallScope(request, existingMatch?.Scope);

        // Select the installer first: it decides which dependencies apply, and a manifest with no
        // applicable installer must fail before any dependency is installed.
        var selectedInstaller = SelectInstaller(manifest.Installers, request.Query)
            ?? throw new InvalidOperationException("No applicable installer found");

        // Installing a different version over an existing installation is an update (WinGet converts such an
        // install to the upgrade flow). A same-version reinstall (repair, --force) is not.
        var isUpdate = existingMatch is not null && InstallIsUpdate(existingMatch.InstalledVersion, manifest.Version);
        EnsureUpgradeAllowed(selectedInstaller, isUpdate);
        if (!isUpdate && selectedInstaller.Switches.Upgrade is not null)
        {
            // The `Upgrade` switch only applies when updating an existing installation.
            selectedInstaller = selectedInstaller with { Switches = selectedInstaller.Switches with { Upgrade = null } };
        }

        EnsurePackageAgreementsAccepted(manifest, request);
        visited.Add(manifest.Id);
        InstallDependencies(manifest, selectedInstaller, request, visited);

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

        var warnings = new List<string>();
        if ((request.UninstallPrevious || (isUpdate && UpgradeBehaviorIs(selectedInstaller, "uninstallPrevious"))) && existingMatch is not null)
        {
            // Remove the installed version that was found above, not whatever the
            // install query (which may pin the *new* version) happens to match.
            int previousExitCode;
            try
            {
                previousExitCode = InstallerDispatch.Uninstall(existingMatch, CreateUninstallPreviousRequest(existingMatch), warnings);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Failed to uninstall the previous version of {manifest.Id}: {ex.Message}", ex);
            }
            EnsurePreviousVersionUninstalled(manifest.Id, previousExitCode);
        }

        var installerType = (selectedInstaller.InstallerType ?? "exe").ToLowerInvariant();
        string installerPath;
        if (installerType == "msstore")
        {
            installerPath = "";
        }
        else
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pinget-install");
            // Like WinGet, --rename never names the installer an install downloads: it only names a
            // portable's command alias (see InstallerDispatch.DeterminePortableLinks).
            installerPath = FetchInstaller(request, manifest, selectedInstaller, rename: null, tempDir);
            installerPath = PrepareInstallerForExecution(installerPath, installerType);
        }
        var exitCode = InstallerDispatch.Execute(installerPath, installerType, request, manifest, selectedInstaller, warnings);
        return CompletedInstallResult(manifest, selectedInstaller, installerPath, installerType, exitCode, warnings);
    }

    /// <summary>
    /// Builds the result of a finished installer run from its classified exit code. A reboot-required
    /// success is surfaced both as <see cref="InstallResult.RebootRequired"/> and as a warning.
    /// </summary>
    internal static InstallResult CompletedInstallResult(
        Manifest manifest,
        Installer installer,
        string installerPath,
        string installerType,
        int exitCode,
        List<string> warnings)
    {
        // A zip's exit code comes from its nested installer, so classify it with the nested type.
        var (success, rebootRequired) = InstallerDispatch.ClassifyExitCode(installer,
            InstallerDispatch.EffectiveInstallerType(installer, installerType), exitCode);
        if (rebootRequired)
            warnings.Add(InstallerDispatch.InstallRebootRequiredWarning);

        return new InstallResult
        {
            PackageId = manifest.Id,
            Version = manifest.Version,
            InstallerPath = installerPath,
            InstallerType = installerType,
            ExitCode = exitCode,
            Success = success,
            NoOp = false,
            RebootRequired = rebootRequired,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Builds the request used by --uninstall-previous to remove the installed version that the install flow
    /// already located.
    /// </summary>
    internal static UninstallRequest CreateUninstallPreviousRequest(ListMatch previous) => new()
    {
        Query = new PackageQuery { Id = previous.Id, Exact = true },
        Mode = InstallerMode.Silent,
        Force = true,
    };

    internal static void EnsurePreviousVersionUninstalled(string packageId, int exitCode)
    {
        if (exitCode != 0)
            throw new InvalidOperationException($"Failed to uninstall the previous version of {packageId} (exit code: {exitCode})");
    }

    internal static InstallRequest ApplyInstallScope(InstallRequest request, string? installedScope = null)
    {
        var queryScope = request.Query.InstallScope;
        var requestScope = request.Scope;
        if (!string.IsNullOrWhiteSpace(queryScope) &&
            !string.IsNullOrWhiteSpace(requestScope) &&
            !queryScope.Trim().Equals(requestScope.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The install request contains conflicting scope values.");
        }

        var scope = !string.IsNullOrWhiteSpace(queryScope) ? queryScope
            : !string.IsNullOrWhiteSpace(requestScope) ? requestScope
            : installedScope;
        if (string.IsNullOrWhiteSpace(scope))
            return request with { Query = request.Query with { InstallScope = null } };

        var resolved = InstallerDispatch.ResolvePortableScope(scope, null, null);
        return request with
        {
            Query = request.Query with
            {
                InstallScope = resolved == InstallerDispatch.PortableScope.Machine ? "machine" : "user",
            },
        };
    }

    internal static string PrepareInstallerForExecution(string installerPath, string installerType)
    {
        if (!(installerType.Equals("nullsoft", StringComparison.OrdinalIgnoreCase) ||
              installerType.Equals("nsis", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetExtension(installerPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return installerPath;

        // Like WinGet, give the verified NSIS payload an executable extension before ShellExecute.
        var executablePath = Path.ChangeExtension(installerPath, ".exe");
        File.Move(installerPath, executablePath, overwrite: true);
        return executablePath;
    }

    public InstallResult Repair(RepairRequest request)
    {
        EnsureHashOverrideAllowed(request.IgnoreSecurityHash);
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

        var warnings = new List<string>();
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

        // An installed MSI is repaired in place with `msiexec /f <ProductCode>`, as WinGet does. Re-running
        // `msiexec /i` on the same package is a maintenance run without REINSTALL that changes nothing yet
        // reports success.
        var msiProductCode = installedMatch is null ? null : MsiRepairProductCode(installedMatch);
        if (installedMatch is not null && msiProductCode is not null && OperatingSystem.IsWindows())
        {
            var repairExitCode = InstallerDispatch.RunMsiRepair(msiProductCode, request.Mode, request.LogPath);
            var (repairSuccess, repairRebootRequired) = InstallerDispatch.ClassifyExitCode(new Installer(), "msi", repairExitCode);
            if (repairRebootRequired)
                warnings.Add(InstallerDispatch.InstallRebootRequiredWarning);
            return new InstallResult
            {
                PackageId = installedMatch.Id,
                Version = installedMatch.InstalledVersion,
                InstallerPath = "",
                InstallerType = "msi",
                ExitCode = repairExitCode,
                Success = repairSuccess,
                NoOp = false,
                RebootRequired = repairRebootRequired,
                Warnings = warnings,
            };
        }

        warnings.Insert(0, RepairReinstallWarning);
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
        var warnings = new List<string>();
        var exitCode = InstallerDispatch.UninstallEachMatch(matches, request.AllVersions,
            installed => OperatingSystem.IsWindows()
                ? InstallerDispatch.Uninstall(installed, request, warnings)
                : throw new PlatformNotSupportedException("Uninstalling packages is only supported on Windows"));

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
            Warnings = warnings,
        };
    }

    private Manifest ResolveManifestForInstall(InstallRequest request)
        => ResolveManifestAndDocumentsForInstall(request).Manifest;

    private (Manifest Manifest, object Documents) ResolveManifestAndDocumentsForInstall(InstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManifestPath))
            return LoadManifestFromPathWithDocuments(request.ManifestPath!);

        var (located, _, _) = FindSingleMatch(request.Query);
        var (manifest, documents, _) = ManifestForMatch(located, request.Query);
        return (manifest, documents);
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

        var correlationCodes = ManifestCorrelationCodes.FromManifest(manifest);
        var found = installedMatches.FirstOrDefault(match =>
            match.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            match.LocalId.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            ManifestMatchesInstalledPackageFamilyName(manifest, match.PackageFamilyNames) ||
            correlationCodes.Matches(match.ProductCodes, match.UpgradeCodes));
        if (found is not null)
            return found;

        if (!correlationCodes.IsEmpty || manifest.Installers.Any(i => !string.IsNullOrWhiteSpace(i.PackageFamilyName)))
        {
            var allInstalled = InstalledPackages.Collect(request.Query.InstallScope);
            var pfnMatch = allInstalled.FirstOrDefault(candidate =>
                ManifestMatchesInstalledPackageFamilyName(manifest, candidate.PackageFamilyNames) ||
                correlationCodes.Matches(candidate.ProductCodes, candidate.UpgradeCodes));
            if (pfnMatch is not null)
                return ListMatchFromInstalled(pfnMatch, _store.Sources);
        }

        return null;
    }

    /// <summary>
    /// The product and upgrade codes a manifest declares (installer <c>ProductCode</c>/<c>UpgradeCode</c> and every
    /// <c>AppsAndFeaturesEntries</c> entry), used to find its installed package.
    /// </summary>
    internal sealed record ManifestCorrelationCodes(List<string> ProductCodes, List<string> UpgradeCodes)
    {
        public static ManifestCorrelationCodes FromManifest(Manifest manifest)
        {
            var codes = new ManifestCorrelationCodes([], []);
            static void Push(List<string> list, string? value)
            {
                var trimmed = value?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    list.Add(trimmed);
            }

            foreach (var installer in manifest.Installers)
            {
                Push(codes.ProductCodes, installer.ProductCode);
                Push(codes.UpgradeCodes, installer.UpgradeCode);
                foreach (var entry in installer.AppsAndFeaturesEntries)
                {
                    Push(codes.ProductCodes, entry.ProductCode);
                    Push(codes.UpgradeCodes, entry.UpgradeCode);
                }
            }

            return codes;
        }

        public bool IsEmpty => ProductCodes.Count == 0 && UpgradeCodes.Count == 0;

        public bool Matches(IReadOnlyList<string> installedProductCodes, IReadOnlyList<string> installedUpgradeCodes) =>
            installedProductCodes.Any(code => ProductCodes.Contains(code, StringComparer.OrdinalIgnoreCase)) ||
            installedUpgradeCodes.Any(code => UpgradeCodes.Contains(code, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ManifestMatchesInstalledPackageFamilyName(Manifest manifest, IReadOnlyList<string> installedPfns)
    {
        if (installedPfns.Count == 0)
            return false;

        return manifest.Installers.Any(installer =>
            !string.IsNullOrWhiteSpace(installer.PackageFamilyName) &&
            installedPfns.Any(installed => installed.Equals(installer.PackageFamilyName, StringComparison.OrdinalIgnoreCase)));
    }

    private void EnsurePackageAgreementsAccepted(Manifest manifest, InstallRequest request)
    {
        if (request.AcceptPackageAgreements || manifest.Agreements.Count == 0)
            return;

        if (_packageAgreementsPrompt is not null)
        {
            if (_packageAgreementsPrompt(manifest))
                return;

            throw new InvalidOperationException("Package agreements were not agreed to.");
        }

        throw new InvalidOperationException("Package agreements are present; rerun with --accept-package-agreements to continue.");
    }

    private void InstallDependencies(Manifest manifest, Installer installer, InstallRequest request, HashSet<string> visited)
    {
        if (request.SkipDependencies)
            return;

        foreach (var dependency in SelectedInstallerDependencies(manifest, installer))
        {
            var dependencyId = dependency.PackageIdentifier;
            // Already installed in this run, or a dependency loop back to a package on the chain.
            if (!visited.Add(dependencyId))
                continue;

            var dependencyRequest = CreateDependencyInstallRequest(dependencyId, request);
            if (dependency.MinimumVersion is { } minimumVersion)
            {
                // WinGet's DependencyNodeProcessor: an installed version that satisfies `MinimumVersion` is skipped;
                // otherwise the latest available version is installed (upgrading an older one), and it must itself
                // satisfy the minimum.
                var installedVersion = InstalledDependencyVersion(dependencyRequest);
                if (installedVersion is not null && DependencyVersionSatisfies(installedVersion, minimumVersion))
                    continue;
                var available = ResolveManifestForInstall(dependencyRequest);
                EnsureDependencyMinimumVersion(dependencyId, available.Version, minimumVersion);
                dependencyRequest = dependencyRequest with
                {
                    NoUpgrade = false,
                    Query = dependencyRequest.Query with { Version = available.Version },
                };
            }

            var result = InstallCore(dependencyRequest, visited);
            EnsureDependencyInstalled(dependencyId, result);
        }
    }

    /// <summary>Installed version of a dependency (by exact id), if it is installed.</summary>
    private string? InstalledDependencyVersion(InstallRequest dependencyRequest)
    {
        var id = dependencyRequest.Query.Id;
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return List(new ListQuery
        {
            Id = id,
            Source = dependencyRequest.Query.Source,
            Exact = true,
            Count = 100,
        }).Matches
            .FirstOrDefault(match =>
                match.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                match.LocalId.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?.InstalledVersion;
    }

    /// <summary>True when <paramref name="version"/> satisfies a dependency's <c>MinimumVersion</c>.</summary>
    internal static bool DependencyVersionSatisfies(string version, string minimumVersion)
        => !RestSource.IsUnknownVersion(version) && RestSource.CompareVersionStrings(version, minimumVersion) >= 0;

    /// <summary>
    /// WinGet <c>DependenciesFlowNoMinVersion</c>: the version that would be installed must satisfy the dependency's
    /// <c>MinimumVersion</c>.
    /// </summary>
    internal static void EnsureDependencyMinimumVersion(string dependencyId, string availableVersion, string minimumVersion)
    {
        if (!DependencyVersionSatisfies(availableVersion, minimumVersion))
            throw new InvalidOperationException(
                $"No suitable version of dependency {dependencyId} was found: {availableVersion} is older than the required minimum version {minimumVersion}");
    }

    /// <summary>
    /// Package dependencies of the selected installer, falling back to the manifest-level
    /// <c>Dependencies</c> (WinGet inherits root <c>Dependencies</c> into installers that declare none).
    /// Blank ids are dropped and duplicates removed case-insensitively, keeping manifest order.
    /// </summary>
    internal static List<string> SelectedInstallerPackageDependencies(Manifest manifest, Installer installer)
        => SelectedInstallerDependencies(manifest, installer).Select(dependency => dependency.PackageIdentifier).ToList();

    /// <summary>
    /// Package dependencies (with <c>MinimumVersion</c>) of the selected installer. An installer that declares
    /// dependencies of any kind uses only its own; otherwise the root ones apply. A duplicated id keeps its first
    /// position and the highest minimum version, like WinGet.
    /// </summary>
    internal static List<PackageDependency> SelectedInstallerDependencies(Manifest manifest, Installer installer)
    {
        IEnumerable<PackageDependency> declared = !installer.Dependencies.IsEmpty
            ? installer.Dependencies.PackageDependencies
            : (installer.PackageDependencies.Count > 0 ? installer.PackageDependencies : manifest.PackageDependencies)
                .Select(id => new PackageDependency { PackageIdentifier = id });

        var result = new List<PackageDependency>();
        foreach (var dependency in declared)
        {
            var id = dependency.PackageIdentifier.Trim();
            if (id.Length == 0)
                continue;
            var index = result.FindIndex(existing => existing.PackageIdentifier.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var existing = result[index];
                if (dependency.MinimumVersion is { } minimum &&
                    (existing.MinimumVersion is null || RestSource.CompareVersionStrings(minimum, existing.MinimumVersion) > 0))
                {
                    result[index] = existing with { MinimumVersion = minimum };
                }
                continue;
            }

            result.Add(new PackageDependency { PackageIdentifier = id, MinimumVersion = dependency.MinimumVersion });
        }

        return result;
    }

    /// <summary>
    /// A failed dependency must stop the dependent install, as in WinGet. Installer failures are
    /// reported through <see cref="InstallResult.Success"/>, not exceptions, so check explicitly.
    /// </summary>
    internal static void EnsureDependencyInstalled(string dependencyId, InstallResult result)
    {
        if (result.Success || result.NoOp)
            return;

        throw new InvalidOperationException(
            $"Dependency {dependencyId} failed to install (exit code: {result.ExitCode}).");
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

    /// <summary>
    /// The product code to repair with msiexec when the installed package is a Windows Installer (MSI/WiX)
    /// ARP entry, or null when repair has to fall back to reinstalling. The installed scan records the ARP
    /// subkey name (the ProductCode for MSI entries) among the product codes.
    /// </summary>
    internal static string? MsiRepairProductCode(ListMatch installed)
    {
        if (!string.Equals(installed.InstallerCategory, "msi", StringComparison.OrdinalIgnoreCase))
            return null;
        return installed.ProductCodes
            .Select(code => code.Trim())
            .FirstOrDefault(code => code.Length > 2 && code.StartsWith('{') && code.EndsWith('}'));
    }

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
            Version = request.Query.Version ?? (installedMatch is not null &&
                !string.Equals(installedMatch.SourceName, "msstore", StringComparison.OrdinalIgnoreCase) &&
                !RestSource.IsUnknownVersion(installedMatch.InstalledVersion)
                ? installedMatch.InstalledVersion
                : null),
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
            !RestSource.IsUnknownVersion(existingMatch.InstalledVersion) &&
            !RestSource.IsUnknownVersion(manifest.Version) &&
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

    private (string PackageId, string Version) DescribeUninstallTarget(UninstallRequest request)
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
            // Like WinGet's manifest correlation, the product and upgrade codes the manifest declares (including
            // `AppsAndFeaturesEntries`) identify the installed package even when it is not correlated to the
            // manifest's package id.
            if (request.ProductCode is null && InstalledMatchesForManifestCodes(manifest, request) is { } codeMatches)
                return codeMatches;
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

    /// <summary>
    /// Installed packages whose product or upgrade codes match the codes declared by <paramref name="manifest"/>;
    /// null when the manifest declares no codes or nothing installed matches them.
    /// </summary>
    private List<ListMatch>? InstalledMatchesForManifestCodes(Manifest manifest, UninstallRequest request)
    {
        var codes = ManifestCorrelationCodes.FromManifest(manifest);
        if (codes.IsEmpty)
            return null;

        var matches = InstalledPackages.Collect(request.Query.InstallScope)
            .Where(candidate => codes.Matches(candidate.ProductCodes, candidate.UpgradeCodes))
            .Where(candidate => request.Query.Version is null ||
                candidate.InstalledVersion.Equals(request.Query.Version, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => ListMatchFromInstalled(candidate, _store.Sources))
            .ToList();
        if (matches.Count == 0)
            return null;
        if (!request.AllVersions && matches.Count > 1 && !request.Force)
            throw new InvalidOperationException("Multiple installed packages matched the query; refine the query or use --all-versions.");
        return request.AllVersions ? matches : [matches[0]];
    }

    private Manifest LoadManifestFromPath(string manifestPath)
        => LoadManifestFromPathWithDocuments(manifestPath).Manifest;

    /// <summary>
    /// Loads a user-supplied <c>--manifest</c>. Every local-manifest load goes through here, so the
    /// <c>LocalManifestFiles</c> admin setting gates install, repair, download, and uninstall alike.
    /// </summary>
    private (Manifest Manifest, object Documents) LoadManifestFromPathWithDocuments(string manifestPath)
    {
        EnsureAdminSettingEnabled("LocalManifestFiles", "--manifest");
        var parts = ResolveManifestPaths(manifestPath).Select(File.ReadAllBytes).ToList();
        return (ParseYamlManifest(parts), ParseYamlManifestDocuments(parts));
    }

    /// <summary>
    /// Builds the YAML manifest emitted next to the installer by
    /// <c>winget download</c>. The structured document is the merged singleton
    /// the source provided; we narrow the <c>Installers</c> array to the
    /// resolved installer so the file describes exactly the binary written.
    /// Identity fields are overridden from the typed manifest because
    /// <see cref="ManifestForMatch"/> corrects them after the initial YAML
    /// parse.
    /// </summary>
    internal static string BuildDownloadManifestYaml(Manifest manifest, object documents, Installer installer)
    {
        var document = StructuredOutput.CollapseManifestDocuments(documents);

        document["PackageIdentifier"] = manifest.Id;
        document["PackageVersion"] = manifest.Version;
        document["PackageName"] = manifest.Name;
        if (!string.IsNullOrEmpty(manifest.Channel))
            document["Channel"] = manifest.Channel;
        else
            document.Remove("Channel");

        // Like WinGet's YamlWriter, keep every field of the selected installer's entry (switches, nested
        // installers, dependencies, return codes, ...); the typed values below only fill in what the entry
        // inherits from the root.
        var matchedEntry = document.TryGetValue("Installers", out var entries) && entries is IEnumerable<object?> entryList
            ? entryList.OfType<Dictionary<string, object?>>().FirstOrDefault(entry => DownloadManifestEntryMatches(entry, document, installer))
            : null;
        var singleInstaller = matchedEntry is not null
            ? new Dictionary<string, object?>(matchedEntry, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(installer.Architecture)) singleInstaller["Architecture"] = installer.Architecture;
        if (!string.IsNullOrEmpty(installer.InstallerType)) singleInstaller["InstallerType"] = installer.InstallerType;
        if (!string.IsNullOrEmpty(installer.Url)) singleInstaller["InstallerUrl"] = installer.Url;
        if (!string.IsNullOrEmpty(installer.Sha256)) singleInstaller["InstallerSha256"] = installer.Sha256;
        if (!string.IsNullOrEmpty(installer.Scope)) singleInstaller["Scope"] = installer.Scope;
        if (!string.IsNullOrEmpty(installer.Locale)) singleInstaller["InstallerLocale"] = installer.Locale;
        if (!string.IsNullOrEmpty(installer.ProductCode)) singleInstaller["ProductCode"] = installer.ProductCode;
        if (!string.IsNullOrEmpty(installer.MSStoreProductIdentifier)) singleInstaller["MSStoreProductIdentifier"] = installer.MSStoreProductIdentifier;
        if (!string.IsNullOrEmpty(installer.ReleaseDate)) singleInstaller["ReleaseDate"] = installer.ReleaseDate;

        document["Installers"] = new List<object?> { singleInstaller };
        document["ManifestType"] = "merged";

        return YamlEmitter.EmitDocument(document);
    }

    /// <summary>True when a manifest <c>Installers</c> entry (with root-level fallbacks) is the selected installer.</summary>
    private static bool DownloadManifestEntryMatches(Dictionary<string, object?> entry, Dictionary<string, object?> root, Installer installer)
    {
        string? Field(string key)
        {
            var value = (entry.TryGetValue(key, out var own) ? own : root.GetValueOrDefault(key))?.ToString()?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        bool Same(string key, string? expected) =>
            string.Equals(Field(key), string.IsNullOrWhiteSpace(expected) ? null : expected.Trim(), StringComparison.OrdinalIgnoreCase);

        return Same("InstallerUrl", installer.Url) &&
            Same("Architecture", installer.Architecture) &&
            Same("InstallerType", installer.InstallerType) &&
            Same("Scope", installer.Scope) &&
            Same("InstallerLocale", installer.Locale);
    }

    /// <summary>
    /// Computes the file name <c>winget download</c> uses for the YAML
    /// manifest emitted next to the installer:
    /// <c>{PackageName}_{Version?}_{Scope?}_{Architecture}_{InstallerType}_{Locale?}.yaml</c>.
    /// Mirrors <c>GetInstallerDownloadOnlyFileName</c> in winget-cli's
    /// DownloadFlow.
    /// </summary>
    internal static string DeriveDownloadManifestFilename(Manifest manifest, Installer installer)
        => DeriveDownloadFilenameStem(manifest, installer) + ".yaml";

    internal static string DeriveDownloadFilenameStem(Manifest manifest, Installer installer)
    {
        var stem = new System.Text.StringBuilder();
        stem.Append(SanitizeFilenameComponent(manifest.Name));
        if (!string.IsNullOrEmpty(manifest.Version) &&
            !string.Equals(manifest.Version, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            stem.Append('_');
            stem.Append(SanitizeFilenameComponent(manifest.Version));
        }
        if (!string.IsNullOrEmpty(installer.Scope))
        {
            stem.Append('_');
            stem.Append(SanitizeFilenameComponent(CapitalizeFirst(installer.Scope)));
        }

        var architecture = string.IsNullOrEmpty(installer.Architecture) ? "neutral" : installer.Architecture;
        stem.Append('_');
        stem.Append(SanitizeFilenameComponent(FormatArchitecture(architecture)));

        if (!string.IsNullOrEmpty(installer.InstallerType))
        {
            stem.Append('_');
            stem.Append(SanitizeFilenameComponent(installer.InstallerType.ToLowerInvariant()));
        }
        if (!string.IsNullOrEmpty(installer.Locale))
        {
            stem.Append('_');
            stem.Append(SanitizeFilenameComponent(installer.Locale));
        }

        return stem.ToString();
    }

    private static string CapitalizeFirst(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private static string FormatArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "x64" => "X64",
        "x86" => "X86",
        "arm" => "Arm",
        "arm64" => "Arm64",
        "neutral" => "Neutral",
        _ => CapitalizeFirst(value),
    };

    private static string SanitizeFilenameComponent(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        return builder.ToString();
    }

    /// <summary>
    /// Resolves <c>--manifest</c> to the files to load: the file itself, or every <c>*.yaml</c>/<c>*.yml</c>
    /// file in a directory, since a WinGet multi-file manifest (version, defaultLocale, installer and
    /// locale files) is passed as its directory.
    /// </summary>
    internal static List<string> ResolveManifestPaths(string manifestPath)
    {
        if (File.Exists(manifestPath))
            return [manifestPath];

        if (!Directory.Exists(manifestPath))
            throw new InvalidOperationException($"Manifest path not found: {manifestPath}");

        var manifestFiles = Directory.EnumerateFiles(manifestPath)
            .Where(path => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        return manifestFiles.Count > 0
            ? manifestFiles
            : throw new InvalidOperationException($"No manifest file found under: {manifestPath}");
    }

    // ── Internal search machinery ──

    private record SourceSearchFailure(RepositoryWarning Warning, Exception Exception);

    private (List<LocatedMatch> Matches, List<string> Warnings, List<SourceSearchFailure> Failures, bool Truncated) SearchLocated(
        PackageQuery query, SearchSemantics semantics)
    {
        var indexes = ResolveSearchSourceIndexes(query.Source);
        var matches = new List<LocatedMatch>();
        var warnings = new List<string>();
        var failures = new List<SourceSearchFailure>();
        bool truncated = false;

        foreach (var index in indexes)
        {
            try
            {
                var (sourceMatches, srcTruncated) = SearchSource(index, query, semantics);
                truncated |= srcTruncated;
                matches.AddRange(sourceMatches);
            }
            catch (Exception ex)
            {
                var operation = ex is SourceOperationException sourceOperation ? sourceOperation.Operation : "search";
                var exception = UnwrapSourceOperationException(ex);
                var warning = CreateSourceWarning(index, operation, exception);
                failures.Add(new SourceSearchFailure(warning, exception));
                warnings.Add($"Failed when searching source '{warning.SourceName}'; results will not be included: {warning.Message}");
                EmitDiagnostic(warning);
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

        return (matches, warnings, failures, truncated);
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
        using var conn = OpenPreindexedConnection(sourceIndex);
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
        RestSource.RestInformation info;
        try
        {
            info = RestSource.LoadInformation(_client, source, _appRoot, MirrorsSystemWingetSources);
        }
        catch (Exception ex)
        {
            throw new SourceOperationException("information", ex);
        }

        (List<RestSource.RestMatchResult> Results, bool Truncated) search;
        try
        {
            search = RestSource.Search(_client, source, query, info, semantics);
        }
        catch (Exception ex)
        {
            throw new SourceOperationException("search", ex);
        }

        var (results, truncated) = search;
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
            Locator = new RestLocator(r.PackageId, r.Versions, r.PackageFamilyNames, r.ProductCodes, r.UpgradeCodes),
        }).ToList(), truncated);
    }

    private RepositoryWarning CreateSourceWarning(int sourceIndex, string operation, Exception exception)
    {
        var source = _store.Sources[sourceIndex];
        return new RepositoryWarning
        {
            Operation = operation,
            SourceName = source.Name,
            SourceKind = source.Kind,
            SourceArg = source.Arg,
            SourceIdentifier = source.Identifier,
            RequestUri = SourceRequestUri(source, operation),
            CachePath = SourceDiagnosticCachePath(source),
            Message = exception.Message,
            ExceptionType = exception.GetType().FullName,
            HttpStatusCode = exception is HttpRequestException httpException && httpException.StatusCode.HasValue
                ? (int)httpException.StatusCode.Value
                : null,
        };
    }

    private static string SourceRequestUri(SourceRecord source, string operation) =>
        source.Kind == SourceKind.Rest
            ? operation switch
            {
                "search" => $"{source.Arg.TrimEnd('/')}/manifestSearch",
                "manifest" => $"{source.Arg.TrimEnd('/')}/packageManifests",
                "information" => $"{source.Arg.TrimEnd('/')}/information",
                _ => source.Arg,
            }
            : source.Arg;

    private string? SourceDiagnosticCachePath(SourceRecord source) =>
        source.Kind switch
        {
            SourceKind.PreIndexed => PreIndexedSource.IndexPath(source, _appRoot, MirrorsSystemWingetSources),
            SourceKind.Rest => Path.Combine(SourceStoreManager.SourceStateDir(source, _appRoot, MirrorsSystemWingetSources), "rest_info.json"),
            _ => null,
        };

    private void EmitDiagnostic(RepositoryWarning warning) => _diagnostics?.Invoke(warning);

    private sealed class SourceOperationException(string operation, Exception innerException)
        : Exception(innerException.Message, innerException)
    {
        public string Operation { get; } = operation;
    }

    private static Exception UnwrapSourceOperationException(Exception exception) =>
        exception is SourceOperationException { InnerException: { } innerException } ? innerException : exception;

    private enum PreIndexedRefreshKind
    {
        Auto,
        Explicit
    }

    private sealed record PreIndexedRefreshOutcome(bool Attempted, bool Succeeded, Exception? Exception, bool ReusedRecentAttempt)
    {
        public static readonly PreIndexedRefreshOutcome NotAttempted = new(false, false, null, false);
    }

    private SqliteConnection OpenPreindexedConnection(int sourceIndex) =>
        OpenPreindexedConnection(sourceIndex, out _);

    private SqliteConnection OpenPreindexedConnection(int sourceIndex, out PreIndexedRefreshOutcome refreshOutcome)
    {
        var source = _store.Sources[sourceIndex];
        var indexPath = PreIndexedSource.IndexPath(source, _appRoot, MirrorsSystemWingetSources);
        refreshOutcome = EnsurePreindexedIndex(sourceIndex, indexPath);

        var conn = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }

    private PreIndexedRefreshOutcome EnsurePreindexedIndex(int sourceIndex, string indexPath)
    {
        var indexExists = File.Exists(indexPath);
        if (!indexExists)
        {
            RefreshPreindexedSource(sourceIndex, force: true, PreIndexedRefreshKind.Auto);
            return new PreIndexedRefreshOutcome(true, true, null, false);
        }

        var source = _store.Sources[sourceIndex];
        if (!IsPreindexedIndexStale(source, indexPath))
            return PreIndexedRefreshOutcome.NotAttempted;

        var lockKey = PreindexedRefreshLockKey(source);
        if (TryGetRecentPreindexedRefreshAttempt(lockKey, out var recentOutcome))
            return recentOutcome;

        try
        {
            RefreshPreindexedSource(sourceIndex, force: false, PreIndexedRefreshKind.Auto);
            return new PreIndexedRefreshOutcome(true, true, null, false);
        }
        catch (Exception ex)
        {
            var warning = CreateSourceWarning(sourceIndex, "source-refresh", ex);
            EmitDiagnostic(warning);
            return new PreIndexedRefreshOutcome(true, false, ex, false);
        }
    }

    private bool IsPreindexedIndexStale(SourceRecord source, string indexPath)
    {
        if (_preIndexedSourceAutoUpdateInterval is null)
            return false;

        var lastUpdate = source.LastUpdate?.ToUniversalTime();
        if (File.Exists(indexPath))
        {
            var indexLastWrite = File.GetLastWriteTimeUtc(indexPath);
            if (lastUpdate is null || indexLastWrite > lastUpdate.Value)
                lastUpdate = indexLastWrite;
        }

        if (lastUpdate is null)
            return true;

        return DateTime.UtcNow - lastUpdate.Value >= _preIndexedSourceAutoUpdateInterval.Value;
    }

    private string RefreshPreindexedSource(int sourceIndex, bool force, PreIndexedRefreshKind kind)
    {
        var source = _store.Sources[sourceIndex];
        var indexPath = PreIndexedSource.IndexPath(source, _appRoot, MirrorsSystemWingetSources);
        var lockKey = PreindexedRefreshLockKey(source);
        var refreshLock = s_preIndexedRefreshLocks.GetOrAdd(lockKey, _ => new object());

        lock (refreshLock)
        {
            if (!force && File.Exists(indexPath) && !IsPreindexedIndexStale(source, indexPath))
                return "Source already refreshed";

            try
            {
                var detail = PreIndexedSource.Update(_client, source, _appRoot, MirrorsSystemWingetSources);
                source.LastUpdate = DateTime.UtcNow;
                if (_sourceMode != EffectiveSourceMode.SystemWingetDirect)
                    SaveStore();
                RecordPreindexedRefreshAttempt(lockKey, null, kind);
                return detail;
            }
            catch (Exception ex)
            {
                RecordPreindexedRefreshAttempt(lockKey, ex, kind);
                throw;
            }
        }
    }

    private string PreindexedRefreshLockKey(SourceRecord source) =>
        $"{_appRoot}|{source.Identifier}|{source.Name}";

    private bool TryGetRecentPreindexedRefreshAttempt(string lockKey, out PreIndexedRefreshOutcome outcome)
    {
        lock (_preIndexedRefreshAttemptGate)
        {
            if (_preIndexedRefreshAttempts.TryGetValue(lockKey, out var attempt) &&
                DateTime.UtcNow - attempt.AttemptedAt < s_preIndexedRefreshRetryInterval)
            {
                outcome = new PreIndexedRefreshOutcome(true, attempt.Exception is null, attempt.Exception, attempt.Kind == PreIndexedRefreshKind.Auto);
                return true;
            }
        }

        outcome = PreIndexedRefreshOutcome.NotAttempted;
        return false;
    }

    private bool HasRecentSuccessfulPreindexedRefreshAttempt(SourceRecord source) =>
        TryGetRecentPreindexedRefreshAttempt(PreindexedRefreshLockKey(source), out var outcome) && outcome.Succeeded;

    private void RecordPreindexedRefreshAttempt(string lockKey, Exception? exception, PreIndexedRefreshKind kind)
    {
        lock (_preIndexedRefreshAttemptGate)
            _preIndexedRefreshAttempts[lockKey] = (DateTime.UtcNow, exception, kind);
    }

    private void EmitPreindexedRefreshDiagnostic(int sourceIndex, string packageId, string requestedVersion, string? requestedChannel)
    {
        var source = _store.Sources[sourceIndex];
        var channelPart = string.IsNullOrWhiteSpace(requestedChannel) ? "" : $" channel '{requestedChannel}'";
        EmitDiagnostic(new RepositoryWarning
        {
            Operation = "manifest-refresh",
            SourceName = source.Name,
            SourceKind = source.Kind,
            SourceArg = source.Arg,
            SourceIdentifier = source.Identifier,
            RequestUri = SourceRequestUri(source, "source-refresh"),
            CachePath = SourceDiagnosticCachePath(source),
            Message = $"Package '{packageId}' version '{requestedVersion}'{channelPart} was missing from the local pre-indexed source cache; refreshing source '{source.Name}'.",
        });
    }

    private static MissingPackageVersionException WithRefreshFailure(MissingPackageVersionException exception, Exception refreshException) =>
        new(exception.PackageId, exception.SourceName, exception.RequestedVersion, exception.RequestedChannel, refreshException);

    private (LocatedMatch Match, List<string> Warnings, List<RepositoryWarning> SourceWarnings) FindSingleMatch(PackageQuery query)
        => FindSingleMatchWithSemantics(query, SearchSemantics.Single);

    private (LocatedMatch Match, List<string> Warnings, List<RepositoryWarning> SourceWarnings) FindSingleMatchForShow(
        PackageQuery query,
        SearchSemantics semantics)
    {
        var located = SearchLocated(query, semantics);
        if (ShouldRetryShowAcrossSystemWingetSources(query, located.Matches, located.Failures))
        {
            var fallbackQuery = query with { Source = null };
            return SingleMatchFromLocated(SearchLocated(fallbackQuery, semantics), reportFirstSourceFailure: false);
        }

        return SingleMatchFromLocated(located, reportFirstSourceFailure: query.Source is not null);
    }

    private bool ShouldRetryShowAcrossSystemWingetSources(
        PackageQuery query,
        IReadOnlyCollection<LocatedMatch> matches,
        IReadOnlyCollection<SourceSearchFailure> failures) =>
        ShouldRetryShowAcrossSystemWingetSources(UsesSystemWingetSources, query.Source, matches.Count, failures.Count);

    internal static bool ShouldRetryShowAcrossSystemWingetSources(
        bool usesSystemWingetSources,
        string? source,
        int matchCount,
        int failureCount) =>
        usesSystemWingetSources &&
        string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase) &&
        matchCount == 0 &&
        failureCount == 0;

    private (LocatedMatch Match, List<string> Warnings, List<RepositoryWarning> SourceWarnings) FindSingleMatchWithSemantics(
        PackageQuery query, SearchSemantics semantics)
    {
        return SingleMatchFromLocated(SearchLocated(query, semantics), reportFirstSourceFailure: query.Source is not null);
    }

    private static (LocatedMatch Match, List<string> Warnings, List<RepositoryWarning> SourceWarnings) SingleMatchFromLocated(
        (List<LocatedMatch> Matches, List<string> Warnings, List<SourceSearchFailure> Failures, bool Truncated) located,
        bool reportFirstSourceFailure)
    {
        if (located.Matches.Count == 0)
        {
            if (reportFirstSourceFailure && located.Failures.Count > 0)
                throw new SourceSearchException(located.Failures[0].Warning, located.Failures[0].Exception);

            throw new InvalidOperationException("no package matched the supplied query");
        }

        if (located.Matches.Count > 1)
            throw new MultiplePackageMatchesException(located.Matches.Select(m => m.Display));

        return (located.Matches[0], located.Warnings, located.Failures.Select(f => f.Warning).ToList());
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
        using var conn = OpenPreindexedConnection(sourceIndex);
        var rows = PreIndexedSource.QueryV1Versions(conn, packageRowid);
        return rows.Select(r => new VersionKey { Version = r.Version, Channel = r.Channel }).ToList();
    }

    private List<VersionKey> VersionsFromV2(int sourceIndex, long packageRowid, string packageHash)
    {
        var source = _store.Sources[sourceIndex];
        using var conn = OpenPreindexedConnection(sourceIndex);
        var (entries, _) = PreIndexedSource.LoadV2VersionData(_client, conn, source, packageRowid, packageHash, _appRoot);
        return entries.Select(e => new VersionKey { Version = e.Version, Channel = "" }).ToList();
    }

    private (Manifest Manifest, object StructuredDocument, List<string> CachedFiles) ManifestForMatch(
        LocatedMatch located,
        PackageQuery query,
        bool allowExplicitVersionRefresh = true)
    {
        return located.Locator switch
        {
            PreIndexedV1Locator v1 => ManifestFromV1(located.SourceIndex, v1.PackageRowId, located.Display, query, allowExplicitVersionRefresh),
            PreIndexedV2Locator v2 => ManifestFromV2(located.SourceIndex, v2.PackageRowId, v2.PackageHash, located.Display, query, allowExplicitVersionRefresh),
            RestLocator rest => ManifestFromRest(located.SourceIndex, rest.PackageId, rest.Versions, located.Display, query),
            _ => throw new InvalidOperationException("Unknown locator type")
        };
    }

    private (Manifest, object, List<string>) ManifestFromV1(
        int sourceIndex,
        long packageRowid,
        SearchMatch display,
        PackageQuery query,
        bool allowExplicitVersionRefresh)
    {
        PreIndexedRefreshOutcome openRefresh;
        SqliteConnection? conn = OpenPreindexedConnection(sourceIndex, out openRefresh);
        try
        {
            return ManifestFromV1Connection(sourceIndex, conn, packageRowid, display, query);
        }
        catch (MissingPackageVersionException ex) when (allowExplicitVersionRefresh && query.Version is not null)
        {
            conn.Dispose();
            conn = null;
            if (openRefresh.Attempted && !openRefresh.ReusedRecentAttempt)
                throw openRefresh.Exception is null ? ex : WithRefreshFailure(ex, openRefresh.Exception);
            if (HasRecentSuccessfulPreindexedRefreshAttempt(_store.Sources[sourceIndex]))
                throw;

            return RefreshPreindexedAndRetryManifest(sourceIndex, display, query, ex);
        }
        finally
        {
            conn?.Dispose();
        }
    }

    private (Manifest, object, List<string>) ManifestFromV1Connection(
        int sourceIndex,
        SqliteConnection conn,
        long packageRowid,
        SearchMatch display,
        PackageQuery query)
    {
        var source = _store.Sources[sourceIndex];
        var versions = PreIndexedSource.QueryV1Versions(conn, packageRowid);
        var selected = SelectV1Version(versions, query.Version, query.Channel, display.Id, source.Name);
        var relativePath = PreIndexedSource.ResolveV1RelativePath(conn, selected.PathPart);
        var bytes = PreIndexedSource.GetCachedSourceFile(_client, "V1_M", source, relativePath, selected.ManifestHash, _appRoot);
        var manifest = ParseYamlManifest(bytes);
        var structuredDocument = ParseYamlManifestDocuments(bytes);
        manifest = manifest with { Version = selected.Version, Channel = selected.Channel };
        return (manifest, structuredDocument, []);
    }

    private (Manifest, object, List<string>) ManifestFromV2(
        int sourceIndex,
        long packageRowid,
        string packageHash,
        SearchMatch display,
        PackageQuery query,
        bool allowExplicitVersionRefresh)
    {
        PreIndexedRefreshOutcome openRefresh;
        SqliteConnection? conn = OpenPreindexedConnection(sourceIndex, out openRefresh);
        try
        {
            return ManifestFromV2Connection(sourceIndex, conn, packageRowid, packageHash, display, query);
        }
        catch (MissingPackageVersionException ex) when (allowExplicitVersionRefresh && query.Version is not null)
        {
            conn.Dispose();
            conn = null;
            if (openRefresh.Attempted && !openRefresh.ReusedRecentAttempt)
                throw openRefresh.Exception is null ? ex : WithRefreshFailure(ex, openRefresh.Exception);
            if (HasRecentSuccessfulPreindexedRefreshAttempt(_store.Sources[sourceIndex]))
                throw;

            return RefreshPreindexedAndRetryManifest(sourceIndex, display, query, ex);
        }
        finally
        {
            conn?.Dispose();
        }
    }

    private (Manifest, object, List<string>) ManifestFromV2Connection(
        int sourceIndex,
        SqliteConnection conn,
        long packageRowid,
        string packageHash,
        SearchMatch display,
        PackageQuery query)
    {
        var source = _store.Sources[sourceIndex];
        var (entries, vdFile) = PreIndexedSource.LoadV2VersionData(_client, conn, source, packageRowid, packageHash, _appRoot);
        var selected = SelectV2Version(entries, query.Version, query.Channel, display.Id, source.Name);
        var bytes = PreIndexedSource.GetCachedSourceFile(_client, "V2_M", source, selected.ManifestRelativePath, selected.ManifestHash, _appRoot);
        var manifest = ParseYamlManifest(bytes);
        var structuredDocument = ParseYamlManifestDocuments(bytes);
        manifest = manifest with { Version = selected.Version };
        return (manifest, structuredDocument, [vdFile]);
    }

    private (Manifest, object, List<string>) ManifestFromRest(
        int sourceIndex,
        string packageId,
        List<VersionKey> versions,
        SearchMatch display,
        PackageQuery query)
    {
        var source = _store.Sources[sourceIndex];
        var info = RestSource.LoadInformation(_client, source, _appRoot, MirrorsSystemWingetSources);
        var selected = SelectRestVersion(versions, query.Version, query.Channel, display.Id, source.Name);
        var (manifest, structuredDocument) = RestSource.FetchManifestWithDocuments(_client, source, info, packageId, selected.Version, selected.Channel);
        return (manifest, structuredDocument, []);
    }

    private (Manifest, object, List<string>) RefreshPreindexedAndRetryManifest(
        int sourceIndex,
        SearchMatch display,
        PackageQuery query,
        MissingPackageVersionException missingVersion)
    {
        EmitPreindexedRefreshDiagnostic(sourceIndex, display.Id, missingVersion.RequestedVersion, missingVersion.RequestedChannel);
        try
        {
            RefreshPreindexedSource(sourceIndex, force: true, PreIndexedRefreshKind.Explicit);
        }
        catch (Exception ex)
        {
            throw WithRefreshFailure(missingVersion, ex);
        }

        var refreshed = FindRefreshedPreindexedMatch(sourceIndex, display, query);
        return ManifestForMatch(refreshed, query, allowExplicitVersionRefresh: false);
    }

    private LocatedMatch FindRefreshedPreindexedMatch(int sourceIndex, SearchMatch previousDisplay, PackageQuery query)
    {
        var sourceQuery = query with { Source = _store.Sources[sourceIndex].Name };
        var (matches, _) = SearchPreindexed(sourceIndex, sourceQuery, SearchSemantics.Single);
        var refreshed = matches.FirstOrDefault(match =>
            string.Equals(match.Display.Id, previousDisplay.Id, StringComparison.OrdinalIgnoreCase));

        if (refreshed is not null)
            return refreshed;

        if (matches.Count == 1)
            return matches[0];

        throw new InvalidOperationException($"Package '{previousDisplay.Id}' was not found in source '{_store.Sources[sourceIndex].Name}' after refreshing the pre-indexed source.");
    }

    // ── Version selection ──

    private static PreIndexedSource.V1VersionRow SelectV1Version(
        List<PreIndexedSource.V1VersionRow> versions,
        string? requestedVersion,
        string? requestedChannel,
        string packageId,
        string sourceName)
    {
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedVersion is null)
        {
            if (requestedChannel is null)
                return versions[0]; // latest

            var channelMatch = versions.FirstOrDefault(v => string.Equals(v.Channel, requestedChannel, StringComparison.OrdinalIgnoreCase));
            return channelMatch ?? throw new InvalidOperationException(
                $"Channel '{requestedChannel}' was not found for package '{packageId}' in source '{sourceName}'.");
        }

        var match = versions.FirstOrDefault(v => string.Equals(v.Version, requestedVersion, StringComparison.OrdinalIgnoreCase)
            && (requestedChannel is null || string.Equals(v.Channel, requestedChannel, StringComparison.OrdinalIgnoreCase)));
        if (match is not null)
            return match;

        throw new MissingPackageVersionException(packageId, sourceName, requestedVersion, requestedChannel);
    }

    private static PreIndexedSource.V2VersionDataEntry SelectV2Version(
        List<PreIndexedSource.V2VersionDataEntry> entries,
        string? requestedVersion,
        string? requestedChannel,
        string packageId,
        string sourceName)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedChannel is not null)
            throw new InvalidOperationException(
                $"Channel '{requestedChannel}' cannot be matched for package '{packageId}' in pre-indexed V2 source '{sourceName}' because the local source index does not include channel metadata.");

        if (requestedVersion is not null)
        {
            var match = entries.FirstOrDefault(e => string.Equals(e.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            throw new MissingPackageVersionException(packageId, sourceName, requestedVersion);
        }

        var sorted = entries.OrderByDescending(e => e.Version, new VersionComparer()).ToList();
        return sorted[0];
    }

    private static VersionKey SelectRestVersion(
        List<VersionKey> versions,
        string? requestedVersion,
        string? requestedChannel,
        string packageId,
        string sourceName)
    {
        if (versions.Count == 0)
            throw new InvalidOperationException("No versions found");

        if (requestedVersion is null)
        {
            var sorted = versions.OrderByDescending(v => v.Version, new VersionComparer()).ToList();
            if (requestedChannel is null)
                return sorted[0];

            var channelMatch = sorted.FirstOrDefault(v => string.Equals(v.Channel, requestedChannel, StringComparison.OrdinalIgnoreCase));
            return channelMatch ?? throw new InvalidOperationException(
                $"Channel '{requestedChannel}' was not found for package '{packageId}' in source '{sourceName}'.");
        }

        var match = versions.FirstOrDefault(v => string.Equals(v.Version, requestedVersion, StringComparison.OrdinalIgnoreCase)
            && (requestedChannel is null || string.Equals(v.Channel, requestedChannel, StringComparison.OrdinalIgnoreCase)));
        if (match is not null)
            return match;

        throw new MissingPackageVersionException(packageId, sourceName, requestedVersion, requestedChannel);
    }

    // ── Installer selection ──

    internal static Installer? SelectInstaller(List<Installer> installers, PackageQuery query)
        => SelectInstaller(installers, query, RestSource.DefaultMarket(), PreferredInstallerLocales.Value);

    /// <summary>
    /// Installer selection with the host's market (<c>Markets</c> filter) and preferred UI languages (installer
    /// locale preference when no <c>--locale</c> is requested) passed in explicitly.
    /// </summary>
    internal static Installer? SelectInstaller(
        List<Installer> installers,
        PackageQuery query,
        string market,
        IReadOnlyList<string> preferredLocales)
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
            .Where(installer => !InstallerUnsupportedOnArchitecture(installer, systemArchitecture))
            .Where(installer => InstallerMatchesMarket(installer, market))
            .Where(installer => InstallerMatchesLocale(installer.Locale, requestedLocale))
            .Select((installer, index) => new
            {
                Installer = installer,
                Index = index,
                Rank = InstallerRank(installer, requestedLocale, preferredLocales, requestedArchitecture, systemArchitecture),
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
        => ParseYamlManifestDocuments([bytes]);

    internal static object ParseYamlManifestDocuments(IReadOnlyList<byte[]> parts)
    {
        var documents = new List<Dictionary<string, object?>>();
        foreach (var doc in ReadYamlManifestDocuments(parts))
        {
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

    private static List<long> ReadInstallerReturnCodes(object? value)
    {
        if (value is not IList<object> list)
            return [];

        var codes = new List<long>();
        foreach (var item in list)
        {
            if (TryParseReturnCode(item, out var code))
                codes.Add(code);
        }
        return codes;
    }

    private static List<ExpectedReturnCode> ReadExpectedReturnCodes(object? value)
    {
        if (value is not IList<object> list)
            return [];

        var entries = new List<ExpectedReturnCode>();
        foreach (var item in list)
        {
            if (item is IDictionary<object, object> entry &&
                entry.TryGetValue("InstallerReturnCode", out var codeValue) &&
                TryParseReturnCode(codeValue, out var code) &&
                entry.TryGetValue("ReturnResponse", out var response) &&
                response?.ToString() is { Length: > 0 } responseText)
            {
                entries.Add(new ExpectedReturnCode { InstallerReturnCode = code, ReturnResponse = responseText });
            }
        }
        return entries;
    }

    internal static bool TryParseReturnCode(object? value, out long code)
    {
        var text = value?.ToString()?.Trim();
        // YAML hex integers (`0x8A150001`) arrive as their source text.
        if (text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out code);
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out code);
    }

    /// <summary>
    /// Reads every YAML document of one or more manifest files (a multi-file manifest directory),
    /// in file order.
    /// </summary>
    private static List<object?> ReadYamlManifestDocuments(IReadOnlyList<byte[]> parts)
    {
        var documents = new List<object?>();
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        foreach (var bytes in parts)
        {
            var content = bytes.AsSpan();
            if (content.StartsWith(utf8Bom))
                content = content[utf8Bom.Length..];
            var yaml = System.Text.Encoding.UTF8.GetString(content);
            var parser = new YamlDotNet.Core.Parser(new StringReader(yaml));
            parser.Consume<YamlDotNet.Core.Events.StreamStart>();
            while (parser.Accept<YamlDotNet.Core.Events.DocumentStart>(out _))
                documents.Add(ReadYamlDocument(parser));
        }
        return documents;
    }

    /// <summary>
    /// Merge order of a manifest document into the root manifest: version, then defaultLocale, then
    /// installer, then anything else (singleton/merged). Null for a non-default <c>locale</c> document,
    /// which must not override the default-locale root fields.
    /// </summary>
    private static int? YamlManifestMergeRank(Dictionary<object, object> document)
    {
        var manifestType = document.TryGetValue("ManifestType", out var value) ? value?.ToString() : null;
        if (string.Equals(manifestType, "locale", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(manifestType, "version", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(manifestType, "defaultLocale", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(manifestType, "installer", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    internal static Manifest ParseYamlManifest(byte[] bytes)
        => ParseYamlManifest([bytes]);

    internal static Manifest ParseYamlManifest(IReadOnlyList<byte[]> parts)
    {
        // Merge all YAML documents into one dictionary (manifests can be multi-document or multi-file)
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var ordered = ReadYamlManifestDocuments(parts)
            .OfType<Dictionary<object, object>>()
            .Select(doc => (Rank: YamlManifestMergeRank(doc), Document: doc))
            .Where(item => item.Rank is not null)
            .OrderBy(item => item.Rank);
        foreach (var (_, doc) in ordered)
        {
            foreach (var kvp in doc)
            {
                var keyStr = kvp.Key.ToString();
                if (keyStr is not null) dict[keyStr] = kvp.Value;
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

        var icons = new List<PackageIcon>();
        if (dict.TryGetValue("Icons", out var iconsObj) && iconsObj is IList<object> iconList)
        {
            foreach (var iconObj in iconList)
            {
                if (iconObj is not IDictionary<object, object> iconDict)
                    continue;

                string? IconStr(string key) => iconDict.TryGetValue(key, out var value) ? value?.ToString() : null;
                var icon = new PackageIcon
                {
                    IconUrl = IconStr("IconUrl"),
                    IconFileType = IconStr("IconFileType"),
                    IconResolution = IconStr("IconResolution"),
                    IconTheme = IconStr("IconTheme"),
                    IconSha256 = IconStr("IconSha256"),
                };
                if (!string.IsNullOrWhiteSpace(icon.IconUrl) ||
                    !string.IsNullOrWhiteSpace(icon.IconFileType) ||
                    !string.IsNullOrWhiteSpace(icon.IconResolution) ||
                    !string.IsNullOrWhiteSpace(icon.IconTheme) ||
                    !string.IsNullOrWhiteSpace(icon.IconSha256))
                {
                    icons.Add(icon);
                }
            }
        }

        // Top-level installer defaults (merged manifest format)
        string? topInstallerType = GetOptStr("InstallerType");
        string? topNestedInstallerType = GetOptStr("NestedInstallerType");
        var topNestedInstallerFiles = dict.TryGetValue("NestedInstallerFiles", out var topNestedFilesValue)
            ? ReadNestedInstallerFiles(topNestedFilesValue)
            : [];
        string? topScope = GetOptStr("Scope");
        string? topProductCode = GetOptStr("ProductCode");
        string? topMsStoreProductIdentifier = GetOptStr("MSStoreProductIdentifier");
        string? topLocale = GetOptStr("InstallerLocale");
        string? topReleaseDate = GetOptStr("ReleaseDate");
        string? topPackageFamilyName = GetOptStr("PackageFamilyName");
        string? topUpgradeCode = GetOptStr("UpgradeCode");
        var topPlatforms = dict.TryGetValue("Platform", out var topPlatformValue)
            ? ReadStringList(topPlatformValue)
            : [];
        string? topMinimumOsVersion = GetOptStr("MinimumOSVersion");
        var topSwitches = ReadInstallerSwitches(dict);
        var topSuccessCodes = ReadInstallerReturnCodes(dict.TryGetValue("InstallerSuccessCodes", out var topSuccessValue) ? topSuccessValue : null);
        var topExpectedReturnCodes = ReadExpectedReturnCodes(dict.TryGetValue("ExpectedReturnCodes", out var topExpectedValue) ? topExpectedValue : null);
        var topCommands = ReadStringList(dict.GetValueOrDefault("Commands"));
        var topMarkets = dict.GetValueOrDefault("Markets");
        var topUnsupportedArchitectures = ReadStringList(dict.GetValueOrDefault("UnsupportedOSArchitectures"));
        var topUpgradeBehavior = GetOptStr("UpgradeBehavior");
        var topDownloadCommandProhibited = ReadBool(dict, "DownloadCommandProhibited");
        var topDependencies = ReadDependencies(dict.GetValueOrDefault("Dependencies"));
        var topAppsAndFeaturesEntries = ReadAppsAndFeaturesEntries(dict.GetValueOrDefault("AppsAndFeaturesEntries"));

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

                    var nestedFiles = instDict.TryGetValue("NestedInstallerFiles", out var nestedFilesValue)
                        ? ReadNestedInstallerFiles(nestedFilesValue)
                        : [];
                    // WinGet: installer `Dependencies` of any kind override the root ones entirely; only an
                    // installer that declares none inherits the root dependencies.
                    var installerDependencies = ReadDependencies(instDict.TryGetValue("Dependencies", out var instDeps) ? instDeps : null);
                    if (installerDependencies.IsEmpty)
                        installerDependencies = topDependencies;
                    var markets = instDict.TryGetValue("Markets", out var marketsValue) ? marketsValue : topMarkets;
                    var appsAndFeaturesEntries = ReadAppsAndFeaturesEntries(instDict.TryGetValue("AppsAndFeaturesEntries", out var afeValue) ? afeValue : null);
                    installers.Add(new Installer
                    {
                        Architecture = InstStr("Architecture"),
                        InstallerType = InstStr("InstallerType") ?? topInstallerType,
                        NestedInstallerType = InstStr("NestedInstallerType") ?? topNestedInstallerType,
                        NestedInstallerFiles = instDict.ContainsKey("NestedInstallerFiles") ? nestedFiles : [.. topNestedInstallerFiles],
                        Url = InstStr("InstallerUrl"),
                        Sha256 = InstStr("InstallerSha256"),
                        ProductCode = InstStr("ProductCode") ?? topProductCode,
                        MSStoreProductIdentifier = InstStr("MSStoreProductIdentifier") ?? topMsStoreProductIdentifier,
                        Locale = InstStr("InstallerLocale") ?? topLocale,
                        Scope = InstStr("Scope") ?? topScope,
                        ReleaseDate = InstStr("ReleaseDate") ?? topReleaseDate,
                        PackageFamilyName = InstStr("PackageFamilyName") ?? topPackageFamilyName,
                        UpgradeCode = InstStr("UpgradeCode") ?? topUpgradeCode,
                        Platforms = instDict.ContainsKey("Platform") ? platforms : [.. topPlatforms],
                        MinimumOsVersion = InstStr("MinimumOSVersion") ?? topMinimumOsVersion,
                        Switches = switches,
                        Commands = instDict.ContainsKey("Commands") ? InstArr("Commands") : [.. topCommands],
                        PackageDependencies = installerDependencies.PackageDependencies.Select(dependency => dependency.PackageIdentifier).ToList(),
                        Dependencies = installerDependencies,
                        RequireExplicitUpgrade = ReadBool(instDict, "RequireExplicitUpgrade"),
                        DownloadCommandProhibited = instDict.ContainsKey("DownloadCommandProhibited")
                            ? ReadBool(instDict, "DownloadCommandProhibited")
                            : topDownloadCommandProhibited,
                        AllowedMarkets = ReadMarkets(markets, "AllowedMarkets"),
                        ExcludedMarkets = ReadMarkets(markets, "ExcludedMarkets"),
                        UnsupportedOSArchitectures = instDict.TryGetValue("UnsupportedOSArchitectures", out var unsupportedValue)
                            ? ReadStringList(unsupportedValue)
                            : [.. topUnsupportedArchitectures],
                        UpgradeBehavior = InstStr("UpgradeBehavior") ?? topUpgradeBehavior,
                        AppsAndFeaturesEntries = instDict.ContainsKey("AppsAndFeaturesEntries") ? appsAndFeaturesEntries : [.. topAppsAndFeaturesEntries],
                        InstallerSuccessCodes = instDict.TryGetValue("InstallerSuccessCodes", out var successValue)
                            ? ReadInstallerReturnCodes(successValue)
                            : [.. topSuccessCodes],
                        ExpectedReturnCodes = instDict.TryGetValue("ExpectedReturnCodes", out var expectedValue)
                            ? ReadExpectedReturnCodes(expectedValue)
                            : [.. topExpectedReturnCodes],
                    });
                }
            }
        }

        var dependencies = ReadPackageDependencies(dict.TryGetValue("Dependencies", out var depsObj) ? depsObj : null);

        var topLevelRequireExplicit = ReadBool(dict, "RequireExplicitUpgrade");
        var anyInstallerRequireExplicit = installers.Any(i => i.RequireExplicitUpgrade);

        return new Manifest
        {
            Id = GetStr("PackageIdentifier"),
            Name = GetOptStr("PackageName") ?? GetStr("PackageIdentifier"),
            Version = GetStr("PackageVersion"),
            Channel = GetOptStr("Channel") ?? "",
            Publisher = GetOptStr("Publisher"),
            Description = GetOptStr("Description") ?? GetOptStr("ShortDescription"),
            ShortDescription = GetOptStr("ShortDescription"),
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
            Icons = icons,
            Installers = installers,
            PackageDependencies = dependencies,
            RequireExplicitUpgrade = topLevelRequireExplicit || anyInstallerRequireExplicit,
        };
    }

    /// <summary>
    /// Reads every dependency kind of a manifest root or installer <c>Dependencies</c> mapping, like the
    /// Rust yaml_dependencies. <c>MinimumVersion</c> keeps its source text.
    /// </summary>
    internal static InstallerDependencies ReadDependencies(object? dependencies)
    {
        if (dependencies is not IDictionary<object, object> depsDict)
            return new InstallerDependencies();

        static List<string> List(IDictionary<object, object> source, string key)
        {
            if (!source.TryGetValue(key, out var value) || value is not IList<object> items)
                return [];
            return items.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
        }

        var packages = new List<PackageDependency>();
        if (depsDict.TryGetValue("PackageDependencies", out var pkgDeps) && pkgDeps is IList<object> pkgDepList)
        {
            foreach (var dep in pkgDepList)
            {
                if (dep is IDictionary<object, object> depDict &&
                    depDict.TryGetValue("PackageIdentifier", out var pid) &&
                    pid?.ToString() is { } packageIdentifier)
                {
                    var minimumVersion = depDict.TryGetValue("MinimumVersion", out var min) ? min?.ToString()?.Trim() : null;
                    packages.Add(new PackageDependency
                    {
                        PackageIdentifier = packageIdentifier,
                        MinimumVersion = string.IsNullOrEmpty(minimumVersion) ? null : minimumVersion,
                    });
                }
            }
        }

        return new InstallerDependencies
        {
            WindowsFeatures = List(depsDict, "WindowsFeatures"),
            WindowsLibraries = List(depsDict, "WindowsLibraries"),
            PackageDependencies = packages,
            ExternalDependencies = List(depsDict, "ExternalDependencies"),
        };
    }

    private static List<string> ReadMarkets(object? markets, string key)
    {
        if (markets is not IDictionary<object, object> marketsDict ||
            !marketsDict.TryGetValue(key, out var value) ||
            value is not IList<object> items)
            return [];
        return items.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
    }

    private static List<AppsAndFeaturesEntry> ReadAppsAndFeaturesEntries(object? value)
    {
        if (value is not IList<object> items)
            return [];

        return items
            .OfType<IDictionary<object, object>>()
            .Select(item =>
            {
                string? Get(string key) => item.TryGetValue(key, out var v) ? v?.ToString() : null;
                return new AppsAndFeaturesEntry
                {
                    DisplayName = Get("DisplayName"),
                    Publisher = Get("Publisher"),
                    DisplayVersion = Get("DisplayVersion"),
                    ProductCode = Get("ProductCode"),
                    UpgradeCode = Get("UpgradeCode"),
                    InstallerType = Get("InstallerType"),
                };
            })
            .ToList();
    }

    /// <summary>
    /// Reads <c>Dependencies.PackageDependencies[*].PackageIdentifier</c> from a manifest root or
    /// installer entry, like the Rust yaml_package_dependencies.
    /// </summary>
    private static List<string> ReadPackageDependencies(object? dependencies)
    {
        var result = new List<string>();
        if (dependencies is IDictionary<object, object> depsDict &&
            depsDict.TryGetValue("PackageDependencies", out var pkgDeps) &&
            pkgDeps is IList<object> pkgDepList)
        {
            foreach (var dep in pkgDepList)
            {
                if (dep is IDictionary<object, object> depDict &&
                    depDict.TryGetValue("PackageIdentifier", out var pid))
                    result.Add(pid?.ToString() ?? "");
            }
        }
        return result;
    }

    private static bool ReadBool(IDictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool ReadBool(IDictionary<object, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var raw) || raw is null) return false;
        return raw switch
        {
            bool b => b,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // Hand-rolled YAML node reader. Replaces YamlDotNet's generic Deserialize
    // path, which is reflection-heavy and emits IL3050/IL2026 warnings under
    // NativeAOT. Produces the same untyped object tree shape that
    // `Deserialize<object?>` produces — Dictionary<object,object?> for
    // mappings, List<object?> for sequences, string for scalars (or null for
    // YAML null tags). Nullability is compile-time only, so all downstream
    // pattern matches (`is IList<object>`, `is IDictionary<object,object>`)
    // keep working unchanged at runtime.
    private static object? ReadYamlDocument(YamlDotNet.Core.IParser parser)
    {
        parser.Consume<YamlDotNet.Core.Events.DocumentStart>();
        var value = ReadYamlNode(parser);
        parser.Consume<YamlDotNet.Core.Events.DocumentEnd>();
        return value;
    }

    private static object? ReadYamlNode(YamlDotNet.Core.IParser parser)
    {
        var current = parser.Current;
        switch (current)
        {
            case YamlDotNet.Core.Events.Scalar scalar:
                parser.MoveNext();
                return ScalarToValue(scalar);
            case YamlDotNet.Core.Events.MappingStart:
                {
                    parser.MoveNext();
                    var dict = new Dictionary<object, object?>();
                    while (parser.Current is not YamlDotNet.Core.Events.MappingEnd)
                    {
                        var key = ReadYamlNode(parser);
                        var value = ReadYamlNode(parser);
                        if (key is not null) dict[key] = value;
                    }
                    parser.MoveNext();
                    return dict;
                }
            case YamlDotNet.Core.Events.SequenceStart:
                {
                    parser.MoveNext();
                    var list = new List<object?>();
                    while (parser.Current is not YamlDotNet.Core.Events.SequenceEnd)
                    {
                        list.Add(ReadYamlNode(parser));
                    }
                    parser.MoveNext();
                    return list;
                }
            default:
                parser.MoveNext();
                return null;
        }
    }

    private static object? ScalarToValue(YamlDotNet.Core.Events.Scalar scalar)
    {
        // Match YamlDotNet's plain-scalar null detection so manifest fields
        // like `Moniker: ~` stay null instead of becoming "", which would
        // make GetOptStr return "" where it used to return null.
        if (scalar.Tag.IsEmpty && scalar.Style == YamlDotNet.Core.ScalarStyle.Plain)
        {
            var v = scalar.Value;
            if (v.Length == 0 || v == "~" ||
                v.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Null", StringComparison.Ordinal))
            {
                return null;
            }
        }
        return scalar.Value;
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
            "MSStoreProductIdentifier",
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

    private static List<NestedInstallerFile> ReadNestedInstallerFiles(object? value)
    {
        if (value is not IList<object> list)
            return [];

        var files = new List<NestedInstallerFile>();
        foreach (var entry in list)
        {
            if (entry is not IDictionary<object, object> dict)
                continue;

            var relative = dict.TryGetValue("RelativeFilePath", out var rel) ? rel?.ToString() : null;
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            files.Add(new NestedInstallerFile
            {
                RelativeFilePath = relative!,
                PortableCommandAlias = dict.TryGetValue("PortableCommandAlias", out var alias) ? alias?.ToString() : null,
            });
        }
        return files;
    }

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
            Upgrade = Get("Upgrade"),
        };
    }

    // ── List helpers ──

    private List<string> CorrelateAllInstalled(List<InstalledPackage> installed)
    {
        // Skip work entirely when every entry was resolved by identity.
        if (installed.All(p => p.Correlated is not null))
            return [];
        var allQuery = new PackageQuery { Count = 100_000 };
        var (matches, warnings, _, _) = SearchLocated(allQuery, SearchSemantics.Many);
        var candidates = matches.Select(m => m.Display).ToList();
        foreach (var pkg in installed)
        {
            if (pkg.Correlated is not null) continue;
            pkg.Correlated = CorrelateInstalledPackage(pkg, candidates, true);
        }
        return warnings;
    }

    internal static SearchMatch? CorrelateInstalledPackageFromLocated(InstalledPackage pkg, IReadOnlyList<LocatedMatch> locatedMatches)
    {
        foreach (var located in locatedMatches)
        {
            if (located.Locator is not RestLocator rest)
                continue;

            bool pfnMatch = pkg.PackageFamilyNames.Count > 0 &&
                            rest.PackageFamilyNames is { Count: > 0 } &&
                            pkg.PackageFamilyNames.Any(installedPfn =>
                                rest.PackageFamilyNames.Any(candidatePfn => candidatePfn.Equals(installedPfn, StringComparison.OrdinalIgnoreCase)));
            bool productCodeMatch = pkg.ProductCodes.Count > 0 &&
                                    rest.ProductCodes is { Count: > 0 } &&
                                    pkg.ProductCodes.Any(installedCode =>
                                        rest.ProductCodes.Any(candidateCode => candidateCode.Equals(installedCode, StringComparison.OrdinalIgnoreCase)));
            bool upgradeCodeMatch = pkg.UpgradeCodes.Count > 0 &&
                                    rest.UpgradeCodes is { Count: > 0 } &&
                                    pkg.UpgradeCodes.Any(installedCode =>
                                        rest.UpgradeCodes.Any(candidateCode => candidateCode.Equals(installedCode, StringComparison.OrdinalIgnoreCase)));

            if (pfnMatch || productCodeMatch || upgradeCodeMatch)
                return located.Display;
        }

        return null;
    }

    internal static SearchMatch? CorrelateInstalledPackage(InstalledPackage pkg, List<SearchMatch> candidates, bool loose)
    {
        // MSIX entries can only be correlated through their PackageFamilyName.
        // That lookup happens earlier in CorrelateInstalledViaIndex; if it
        // didn't find a match, name-based fallback is wrong — the catalog
        // doesn't carry this MSIX's identity, so any name collision (e.g. the
        // self-updating `Microsoft Edge Stable` MSIX or the Store stub
        // `Notepad++` MSIX) would manufacture a phantom correlation that
        // winget doesn't make.
        if (pkg.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            return null;

        var installedName = NormalizeCorrelationName(pkg.Name);
        var installedNameLower = pkg.Name.ToLowerInvariant();
        var candidateNames = CorrelationNameCandidates(pkg.Name);

        var scored = new List<(int Score, SearchMatch Match)>();
        foreach (var candidate in candidates)
        {
            var candidateNorm = NormalizeCorrelationName(candidate.Name);
            int baseScore;
            if (candidate.Id.Equals(pkg.LocalId, StringComparison.OrdinalIgnoreCase))
                baseScore = 1000;
            else if (candidateNames.Any(n => NormalizeCorrelationName(n).Equals(candidateNorm, StringComparison.OrdinalIgnoreCase)))
                baseScore = 900;
            else if (loose && candidateNorm.Length >= 6 && installedName.Contains(candidateNorm, StringComparison.OrdinalIgnoreCase))
                baseScore = 700;
            else
                baseScore = 0;

            if (baseScore == 0) continue;

            // Tiebreaker so we pick the *right* candidate when multiple catalog
            // entries collapse to the same normalized name or all loose-match a
            // long installed name. Two failure modes this addresses:
            //   • `Notepad++` and `Notepad--` both normalize to `notepad` (the
            //     alphanumeric filter strips `+`/`-`), so both score 900 and
            //     iteration order picked an arbitrary one.
            //   • Loose substring match picks up `Studio` inside
            //     `...from Visual Studio` and lets `ZeroBrane.Studio` outrank
            //     `Microsoft.DotNet.SDK.10` when both score 700.
            // The bonus rewards catalog names that appear verbatim in the
            // installed display name — strongly when they anchor the start,
            // weaker when they're embedded.
            var candidateLower = candidate.Name.ToLowerInvariant();
            int prefixBonus;
            if (installedNameLower.StartsWith(candidateLower, StringComparison.Ordinal))
                prefixBonus = 100;
            else if (installedNameLower.Contains(candidateLower, StringComparison.Ordinal))
                prefixBonus = 50;
            else
                prefixBonus = 0;

            scored.Add((baseScore + prefixBonus, candidate));
        }

        if (scored.Count == 0) return null;

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var (topScore, topMatch) = scored[0];
        // Reject ambiguous wins. When the user has e.g. `Git` (publisher
        // "The Git Development Community") installed, both `Git.Git` and
        // `Microsoft.Git` normalize their names to `git` and tie at the same
        // score — the only signal that disambiguates them, publisher, isn't
        // part of this scoring function. winget refuses the correlation in
        // this case (the install lists with empty Source); we do the same to
        // avoid manufacturing a fake upgrade against the wrong package.
        if (scored.Count > 1 && scored[1].Score == topScore)
            return null;
        return topMatch;
    }

    // ── Identity correlation against the v2 index ─────────────────────────

    /// <summary>
    /// Looks up a v2 catalog package whose identity (PackageFamilyName,
    /// ProductCode, or UpgradeCode) matches the installed package. Returns
    /// the packages.rowid and the field that produced the match. UpgradeCode
    /// is tried *before* ProductCode because it's the MSI family identity —
    /// stable across versions — while ProductCode changes every release. For
    /// installs whose ProductCode happens to also be carried under a sibling
    /// catalog package (e.g. `OpenJS.NodeJS.LTS` retains old LTS-line
    /// ProductCodes while the live install actually belongs to
    /// `OpenJS.NodeJS.22` via its UpgradeCode), the upgrade-side match
    /// represents the user's package family — the one winget uses.
    /// </summary>
    private static (long Rowid, string MatchedBy)? LookupIdentityMatchV2(
        Microsoft.Data.Sqlite.SqliteConnection conn, InstalledPackage pkg)
    {
        foreach (var pfn in pkg.PackageFamilyNames)
        {
            var rowid = QueryOptionalLong(conn,
                "SELECT package FROM pfns2 WHERE pfn = @v LIMIT 1",
                pfn.ToLowerInvariant());
            if (rowid is long r) return (r, "PackageFamilyName");
        }
        foreach (var code in pkg.UpgradeCodes)
        {
            var rowid = QueryOptionalLong(conn,
                "SELECT package FROM upgradecodes2 WHERE upgradecode = @v LIMIT 1",
                code.ToLowerInvariant());
            if (rowid is long r) return (r, "UpgradeCode");
        }
        foreach (var code in pkg.ProductCodes)
        {
            var rowid = QueryOptionalLong(conn,
                "SELECT package FROM productcodes2 WHERE productcode = @v LIMIT 1",
                code.ToLowerInvariant());
            if (rowid is long r) return (r, "ProductCode");
        }
        return null;
    }

    private static long? QueryOptionalLong(Microsoft.Data.Sqlite.SqliteConnection conn, string sql, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@v", value);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return null;
        return Convert.ToInt64(result);
    }

    private static bool V2IdentityTablesPresent(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'productcodes2' LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    private record V2PackageMetadata(long Rowid, string Id, string Name, string? Moniker, string LatestVersion, string PackageHash);

    private static V2PackageMetadata? FetchV2PackageMetadata(Microsoft.Data.Sqlite.SqliteConnection conn, long packageRowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, moniker, latest_version, hash FROM packages WHERE rowid = @rowid LIMIT 1";
        cmd.Parameters.AddWithValue("@rowid", packageRowid);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var hashValue = reader.GetValue(4);
        var hash = hashValue is byte[] blob
            ? Convert.ToHexString(blob).ToLowerInvariant()
            : (hashValue?.ToString() ?? string.Empty).ToLowerInvariant();
        return new V2PackageMetadata(
            packageRowid,
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            hash);
    }

    /// <summary>
    /// Translates an ARP DisplayVersion to the catalog Version it maps to.
    /// For packages like the .NET SDK or Python the ARP version is an MSI
    /// build number (e.g. `10.1.826.23019`) unrelated to the public version
    /// (`10.0.108`). winget records the ARP range per catalog version in
    /// versionData.mszyml as `aMiV` / `aMaV`; we look for the entry whose
    /// range contains the installed version.
    /// </summary>
    internal static string? MapArpVersionToCatalog(IReadOnlyList<PreIndexedSource.V2VersionDataEntry> entries, string arpVersion)
    {
        if (string.IsNullOrEmpty(arpVersion) || arpVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        foreach (var entry in entries)
        {
            if (entry.ArpMinVersion is null || entry.ArpMaxVersion is null) continue;
            if (RestSource.CompareVersionStrings(arpVersion, entry.ArpMinVersion) >= 0 &&
                RestSource.CompareVersionStrings(arpVersion, entry.ArpMaxVersion) <= 0)
            {
                return entry.Version;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns "&lt; latest&gt;" when ARP range metadata proves the installed
    /// version is below the newest catalog version, but the raw DisplayVersion
    /// is too coarse to map into a concrete aMiV/aMaV bucket.
    /// </summary>
    internal static string? LessThanLatestArpAnchoredVersion(
        IReadOnlyList<PreIndexedSource.V2VersionDataEntry> entries,
        string arpVersion)
    {
        if (string.IsNullOrEmpty(arpVersion) || arpVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        PreIndexedSource.V2VersionDataEntry? latest = null;
        foreach (var entry in entries)
        {
            if (entry.ArpMinVersion is null || entry.ArpMaxVersion is null) continue;
            if (latest is null || RestSource.CompareVersionStrings(entry.Version, latest.Version) > 0)
                latest = entry;
        }
        if (latest?.ArpMinVersion is null)
            return null;
        if (RestSource.CompareVersionStrings(arpVersion, latest.Version) != 0)
            return null;
        if (RestSource.CompareVersionStrings(arpVersion, latest.ArpMinVersion) >= 0)
            return null;

        string? previousMax = null;
        foreach (var entry in entries)
        {
            if (entry.ArpMaxVersion is null) continue;
            if (RestSource.CompareVersionStrings(entry.Version, latest.Version) >= 0) continue;
            if (previousMax is null || RestSource.CompareVersionStrings(entry.ArpMaxVersion, previousMax) > 0)
                previousMax = entry.ArpMaxVersion;
        }
        if (previousMax is not null && RestSource.CompareVersionStrings(arpVersion, previousMax) <= 0)
            return null;

        return $"< {latest.Version}";
    }

    /// <summary>
    /// Returns "&gt; latest&gt;" when ARP range metadata proves the installed
    /// package version is newer than the newest catalog version that has ARP
    /// bounds. This is common for inbox MSIX frameworks such as
    /// WindowsAppRuntime, where Windows can service a package beyond the
    /// latest winget catalog mapping.
    /// </summary>
    internal static string? GreaterThanLatestArpAnchoredVersion(
        IReadOnlyList<PreIndexedSource.V2VersionDataEntry> entries,
        string arpVersion,
        string catalogLatestVersion)
    {
        if (string.IsNullOrEmpty(arpVersion) || arpVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        PreIndexedSource.V2VersionDataEntry? latest = null;
        foreach (var entry in entries)
        {
            if (entry.ArpMinVersion is null || entry.ArpMaxVersion is null) continue;
            if (latest is null || RestSource.CompareVersionStrings(entry.Version, latest.Version) > 0)
                latest = entry;
        }
        if (latest?.ArpMaxVersion is null)
            return null;
        if (!latest.Version.Equals(catalogLatestVersion, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!HasDifferentLeadingVersionNumber(latest.ArpMaxVersion, latest.Version))
            return null;
        if (RestSource.CompareVersionStrings(arpVersion, latest.ArpMaxVersion) <= 0)
            return null;

        return $"> {latest.Version}";
    }

    private static bool HasDifferentLeadingVersionNumber(string left, string right)
    {
        var leftLeading = LeadingVersionNumber(left);
        var rightLeading = LeadingVersionNumber(right);
        return leftLeading is not null && rightLeading is not null &&
               !leftLeading.Equals(rightLeading, StringComparison.OrdinalIgnoreCase);
    }

    private static string? LeadingVersionNumber(string value)
    {
        var trimmed = value.TrimStart();
        int length = 0;
        while (length < trimmed.Length && char.IsAsciiDigit(trimmed[length]))
            length++;
        return length == 0 ? null : trimmed[..length];
    }

    /// <summary>
    /// Returns the latest catalog Version that carries ARP-range metadata.
    /// Packages like `Microsoft.WindowsAppRuntime.1.8` also publish "internal"
    /// version rows whose `v` is an MSI build number (`8000.836.2153.0`)
    /// without ARP bounds — those aren't user-facing upgrade targets. When
    /// the installed side was matched through an ARP range, the available
    /// side must be a peer that also exposes ARP bounds so the two versions
    /// compare on the same scale. Returns null when no entry has bounds.
    /// </summary>
    internal static string? LatestArpAnchoredVersion(IReadOnlyList<PreIndexedSource.V2VersionDataEntry> entries)
    {
        PreIndexedSource.V2VersionDataEntry? best = null;
        foreach (var entry in entries)
        {
            if (entry.ArpMinVersion is null || entry.ArpMaxVersion is null) continue;
            if (best is null || RestSource.CompareVersionStrings(entry.Version, best.Version) > 0)
                best = entry;
        }
        return best?.Version;
    }

    /// <summary>
    /// Uses winget's per-source installed tracking DB when available. This is
    /// an exact identity signal recorded by winget itself, and it preserves
    /// source order for sources that do not expose a preindexed identity table
    /// such as REST sources.
    /// </summary>
    private List<string> CorrelateInstalledViaInstalledDb(List<InstalledPackage> installed, string? requestedSource)
    {
        var warnings = new List<string>();
        for (int sourceIndex = 0; sourceIndex < _store.Sources.Count; sourceIndex++)
        {
            var source = _store.Sources[sourceIndex];
            if (requestedSource is not null && !source.Name.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))
                continue;

            var installedDbPath = SourceStoreManager.SourceInstalledDbPath(source, _appRoot, MirrorsSystemWingetSources);
            if (!File.Exists(installedDbPath))
                continue;

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={installedDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            if (!InstalledDbIdentityTablesPresent(conn))
                continue;

            for (int i = 0; i < installed.Count; i++)
            {
                if (installed[i].Correlated is not null) continue;
                var hit = LookupInstalledDbIdentityMatch(conn, installed[i]);
                if (hit is null) continue;

                string? version = null;
                string? moniker = null;
                if (source.Kind == SourceKind.PreIndexed)
                {
                    try
                    {
                        using var indexConn = OpenPreindexedConnection(sourceIndex);
                        var meta = LookupV2MetadataById(indexConn, hit.Value.Id);
                        if (meta is not null)
                        {
                            hit = (meta.Id, meta.Name, hit.Value.MatchedBy);
                            moniker = meta.Moniker;

                            var (entries, _) = PreIndexedSource.LoadV2VersionData(_client, indexConn, source, meta.Rowid, meta.PackageHash, _appRoot);
                            var canonicalInstalled = MapArpVersionToCatalog(entries, installed[i].InstalledVersion)
                                ?? LessThanLatestArpAnchoredVersion(entries, installed[i].InstalledVersion)
                                ?? GreaterThanLatestArpAnchoredVersion(entries, installed[i].InstalledVersion, meta.LatestVersion);
                            var anchoredLatest = LatestArpAnchoredVersion(entries);
                            version = canonicalInstalled is not null
                                ? (IsGreaterThanVersionMarker(canonicalInstalled) ? null : anchoredLatest)
                                : meta.LatestVersion;

                            if (canonicalInstalled is not null)
                            {
                                installed[i].InstalledVersion = canonicalInstalled;
                                installed[i].InstalledVersionCanonical = true;
                            }
                        }
                    }
                    catch { /* version data fetch is best-effort; fall back to the installed DB identity below */ }
                }

                installed[i].Correlated = new SearchMatch
                {
                    SourceName = source.Name,
                    SourceKind = source.Kind,
                    Id = hit.Value.Id,
                    Name = hit.Value.Name,
                    Moniker = moniker,
                    Version = version,
                    MatchCriteria = hit.Value.MatchedBy,
                };
            }
        }

        return warnings;
    }

    /// <summary>
    /// Rows correlated to a REST source (such as msstore) through that source's installed.db carry no
    /// available version, so they could never show as upgradeable. Resolve the latest version with an
    /// exact-id single lookup against the source, the same request search makes.
    /// </summary>
    internal List<string> ResolveRestCorrelatedVersions(List<InstalledPackage> installed)
    {
        var warnings = new List<string>();
        var failedSources = new HashSet<int>();
        foreach (var pkg in installed)
        {
            var correlated = pkg.Correlated;
            if (correlated is null || correlated.SourceKind != SourceKind.Rest || correlated.Version is not null)
                continue;

            var sourceIndex = _store.Sources.FindIndex(source =>
                source.Name.Equals(correlated.SourceName, StringComparison.OrdinalIgnoreCase));
            if (sourceIndex < 0 || failedSources.Contains(sourceIndex))
                continue;

            var query = new PackageQuery { Id = correlated.Id, Source = correlated.SourceName, Exact = true };
            try
            {
                var (matches, _) = SearchSource(sourceIndex, query, SearchSemantics.Single);
                var hit = matches.FirstOrDefault(match =>
                    match.Display.Id.Equals(correlated.Id, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    pkg.Correlated = correlated with
                    {
                        Version = hit.Display.Version,
                        Channel = hit.Display.Channel,
                    };
                }
            }
            catch (Exception ex)
            {
                failedSources.Add(sourceIndex);
                var operation = ex is SourceOperationException sourceOperation ? sourceOperation.Operation : "search";
                var warning = CreateSourceWarning(sourceIndex, operation, UnwrapSourceOperationException(ex));
                warnings.Add($"Failed when searching source '{warning.SourceName}'; results will not be included: {warning.Message}");
                EmitDiagnostic(warning);
            }
        }
        return warnings;
    }

    private static bool InstalledDbIdentityTablesPresent(Microsoft.Data.Sqlite.SqliteConnection conn) =>
        TableExists(conn, "manifest") &&
        TableExists(conn, "ids") &&
        TableExists(conn, "names") &&
        ((TableExists(conn, "pfns") && TableExists(conn, "pfns_map")) ||
         (TableExists(conn, "productcodes") && TableExists(conn, "productcodes_map")) ||
         (TableExists(conn, "upgradecodes") && TableExists(conn, "upgradecodes_map")));

    private static bool TableExists(Microsoft.Data.Sqlite.SqliteConnection conn, string name) =>
        QueryOptionalLong(conn, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @v LIMIT 1", name) is not null;

    private static (string Id, string Name, string MatchedBy)? LookupInstalledDbIdentityMatch(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        InstalledPackage pkg)
    {
        foreach (var pfn in pkg.PackageFamilyNames)
        {
            var hit = LookupInstalledDbIdentityMatch(conn, "pfns", "pfns_map", "pfn", pfn, "PackageFamilyName");
            if (hit is not null) return hit;
        }

        foreach (var code in pkg.UpgradeCodes)
        {
            var hit = LookupInstalledDbIdentityMatch(conn, "upgradecodes", "upgradecodes_map", "upgradecode", code, "UpgradeCode");
            if (hit is not null) return hit;
        }

        foreach (var code in pkg.ProductCodes)
        {
            var hit = LookupInstalledDbIdentityMatch(conn, "productcodes", "productcodes_map", "productcode", code, "ProductCode");
            if (hit is not null) return hit;
        }

        return null;
    }

    internal static (string Id, string Name, string MatchedBy)? LookupInstalledDbIdentityMatchForTesting(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        InstalledPackage pkg)
        => LookupInstalledDbIdentityMatch(conn, pkg);

    private static (string Id, string Name, string MatchedBy)? LookupInstalledDbIdentityMatch(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        string valueTable,
        string mapTable,
        string valueColumn,
        string value,
        string matchedBy)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT ids.id, names.name
            FROM {mapTable} map
            JOIN {valueTable} value ON map.{valueColumn} = value.rowid
            JOIN manifest ON map.manifest = manifest.rowid
            JOIN ids ON manifest.id = ids.rowid
            JOIN names ON manifest.name = names.rowid
            WHERE value.{valueColumn} = @v
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@v", value.ToLowerInvariant());
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1), matchedBy)
            : null;
    }

    /// <summary>
    /// Correlates installed packages against the v2 pre-indexed catalog
    /// using PackageFamilyName / ProductCode / UpgradeCode lookups — winget's
    /// authoritative correlation path. Also rewrites `InstalledVersion` to
    /// the catalog Version whose ARP range covers the ARP DisplayVersion, so
    /// packages whose ARP version is an MSI build number compare against a
    /// meaningful catalog version. Packages without any identifier or whose
    /// identifiers don't match a catalog entry are left untouched for the
    /// name-based fallback.
    /// </summary>
    private List<string> CorrelateInstalledViaIndex(List<InstalledPackage> installed, string? requestedSource)
    {
        var warnings = new List<string>();
        for (int sourceIndex = 0; sourceIndex < _store.Sources.Count; sourceIndex++)
        {
            var source = _store.Sources[sourceIndex];
            if (source.Kind != SourceKind.PreIndexed) continue;
            if (requestedSource is not null && !source.Name.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))
                continue;

            Microsoft.Data.Sqlite.SqliteConnection conn;
            try { conn = OpenPreindexedConnection(sourceIndex); }
            catch { continue; }

            var resolved = new List<(int InstalledIndex, V2PackageMetadata Meta, string MatchedBy)>();
            using (conn)
            {
                if (!V2IdentityTablesPresent(conn)) continue;
                for (int i = 0; i < installed.Count; i++)
                {
                    if (installed[i].Correlated is not null) continue;
                    var hit = LookupIdentityMatchV2(conn, installed[i]);
                    if (hit is null) continue;
                    var meta = FetchV2PackageMetadata(conn, hit.Value.Rowid);
                    if (meta is null) continue;
                    resolved.Add((i, meta, hit.Value.MatchedBy));
                }
            }

            foreach (var (idx, meta, matchedBy) in resolved)
            {
                string? canonicalInstalled = null;
                string? anchoredLatest = null;
                try
                {
                    using var conn2 = OpenPreindexedConnection(sourceIndex);
                    var (entries, _) = PreIndexedSource.LoadV2VersionData(_client, conn2, source, meta.Rowid, meta.PackageHash, _appRoot);
                    canonicalInstalled = MapArpVersionToCatalog(entries, installed[idx].InstalledVersion)
                        ?? LessThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion)
                        ?? GreaterThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion, meta.LatestVersion);
                    anchoredLatest = LatestArpAnchoredVersion(entries);
                }
                catch { /* version data fetch is best-effort; fall back below */ }

                // When the installed side was rebased to a catalog Version
                // via aMiV/aMaV, the available side must compare on the same
                // scale: prefer the latest ARP-anchored Version over
                // `latest_version`, which can be an internal/MSI build
                // number that would manufacture a phantom upgrade against
                // an installed canonical.
                var availableVersion = canonicalInstalled is not null
                    ? (IsGreaterThanVersionMarker(canonicalInstalled) ? null : anchoredLatest)
                    : meta.LatestVersion;

                installed[idx].Correlated = new SearchMatch
                {
                    SourceName = source.Name,
                    SourceKind = source.Kind,
                    Id = meta.Id,
                    Name = meta.Name,
                    Moniker = meta.Moniker,
                    Version = availableVersion,
                    MatchCriteria = matchedBy,
                };
                if (canonicalInstalled is not null)
                {
                    installed[idx].InstalledVersion = canonicalInstalled;
                    installed[idx].InstalledVersionCanonical = true;
                }
            }
        }
        return warnings;
    }

    /// <summary>
    /// Mid-tier correlation that mirrors winget's ARP normalization path.
    /// For each installed package without a PFN/PC/UC identity match, the
    /// (DisplayName, Publisher) is run through NameNormalization and
    /// intersected against the v2 index's norm_names2 and norm_publishers2
    /// tables. A single resulting package_rowid produces a correlation;
    /// ambiguous matches are skipped, matching winget's empty-Source
    /// behavior when normalization can't pick a winner.
    /// </summary>
    private List<string> CorrelateInstalledByNormalizedIdentity(List<InstalledPackage> installed, string? requestedSource)
    {
        var warnings = new List<string>();
        for (int sourceIndex = 0; sourceIndex < _store.Sources.Count; sourceIndex++)
        {
            var source = _store.Sources[sourceIndex];
            if (source.Kind != SourceKind.PreIndexed) continue;
            if (requestedSource is not null && !source.Name.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))
                continue;

            Microsoft.Data.Sqlite.SqliteConnection conn;
            try { conn = OpenPreindexedConnection(sourceIndex); }
            catch { continue; }

            var resolved = new List<(int Index, V2PackageMetadata Meta)>();
            using (conn)
            {
                if (!V2NormalizedIdentityTablesPresent(conn)) continue;
                for (int i = 0; i < installed.Count; i++)
                {
                    var pkg = installed[i];
                    if (pkg.Correlated is not null) continue;
                    // MSIX entries belong to the PFN-only path; matches
                    // `CorrelateInstalledPackage`'s MSIX hard-block.
                    if (pkg.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(pkg.Publisher)) continue;

                    var normName = NameNormalization.NormalizeName(pkg.Name).Name;
                    if (string.IsNullOrEmpty(normName)) continue;
                    var normPub = NameNormalization.NormalizePublisher(pkg.Publisher);
                    if (string.IsNullOrEmpty(normPub)) continue;

                    var rowid = LookupUniqueNormalizedIdentity(conn, normName, normPub);
                    if (rowid is null) continue;
                    var meta = FetchV2PackageMetadata(conn, rowid.Value);
                    if (meta is null) continue;
                    resolved.Add((i, meta));
                }
            }

            foreach (var (idx, meta) in resolved)
            {
                string? canonicalInstalled = null;
                string? anchoredLatest = null;
                try
                {
                    using var conn2 = OpenPreindexedConnection(sourceIndex);
                    var (entries, _) = PreIndexedSource.LoadV2VersionData(_client, conn2, source, meta.Rowid, meta.PackageHash, _appRoot);
                    canonicalInstalled = MapArpVersionToCatalog(entries, installed[idx].InstalledVersion)
                        ?? LessThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion)
                        ?? GreaterThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion, meta.LatestVersion);
                    anchoredLatest = LatestArpAnchoredVersion(entries);
                }
                catch { /* best-effort */ }

                var availableVersion = canonicalInstalled is not null
                    ? (IsGreaterThanVersionMarker(canonicalInstalled) ? null : anchoredLatest)
                    : meta.LatestVersion;

                installed[idx].Correlated = new SearchMatch
                {
                    SourceName = source.Name,
                    SourceKind = source.Kind,
                    Id = meta.Id,
                    Name = meta.Name,
                    Moniker = meta.Moniker,
                    Version = availableVersion,
                    MatchCriteria = "NormalizedNameAndPublisher",
                };
                if (canonicalInstalled is not null)
                {
                    installed[idx].InstalledVersion = canonicalInstalled;
                    installed[idx].InstalledVersionCanonical = true;
                }
            }
        }
        return warnings;
    }

    private static bool V2NormalizedIdentityTablesPresent(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'norm_names2' LIMIT 1";
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Returns the catalog packages.rowid whose (norm_name, norm_publisher)
    /// pair matches the installed package — if and only if the match is
    /// unique. Multiple matches mean normalization couldn't disambiguate
    /// (an ARP DisplayName that normalizes the same as two distinct catalog
    /// packages); winget refuses to correlate and we do the same.
    /// </summary>
    // Test-only entry points. Keep these internal so the assembly's
    // InternalsVisibleTo grant exposes them just to Core.Tests.
    internal static long? LookupUniqueNormalizedIdentityForTesting(Microsoft.Data.Sqlite.SqliteConnection conn, string normName, string normPublisher)
        => LookupUniqueNormalizedIdentity(conn, normName, normPublisher);

    internal static bool ManifestHasCompatibleInstallerForTesting(Manifest manifest)
        => ManifestHasCompatibleInstaller(manifest);

    internal static bool InstalledPackageMatchesUpgradeFilterForTesting(InstalledPackage pkg, ListQuery query)
        => InstalledPackageMatchesUpgradeFilter(pkg, query);

    internal static PreIndexedSource.V1VersionRow SelectV1VersionForTesting(
        List<PreIndexedSource.V1VersionRow> versions,
        string? requestedVersion,
        string? requestedChannel = null)
        => SelectV1Version(versions, requestedVersion, requestedChannel, "Test.Package", "test");

    internal static PreIndexedSource.V2VersionDataEntry SelectV2VersionForTesting(
        List<PreIndexedSource.V2VersionDataEntry> entries,
        string? requestedVersion,
        string? requestedChannel = null)
        => SelectV2Version(entries, requestedVersion, requestedChannel, "Test.Package", "test");

    internal static VersionKey SelectRestVersionForTesting(
        List<VersionKey> versions,
        string? requestedVersion,
        string? requestedChannel = null)
        => SelectRestVersion(versions, requestedVersion, requestedChannel, "Test.Package", "test");

    private static long? LookupUniqueNormalizedIdentity(Microsoft.Data.Sqlite.SqliteConnection conn, string normName, string normPublisher)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT n.package FROM norm_names2 n
            WHERE n.norm_name = @name
            AND EXISTS (SELECT 1 FROM norm_publishers2 p WHERE p.package = n.package AND p.norm_publisher = @pub)
            LIMIT 2";
        cmd.Parameters.AddWithValue("@name", normName);
        cmd.Parameters.AddWithValue("@pub", normPublisher);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var first = reader.GetInt64(0);
        if (reader.Read()) return null; // ambiguous
        return first;
    }

    /// <summary>
    /// Resolves a v2 package by its catalog id back to its rowid + hash so
    /// the post-correlation enrichment pass can load versionData for rows
    /// that correlated through the name-based fallback (which throws the
    /// rowid away).
    /// </summary>
    private static V2PackageMetadata? LookupV2MetadataById(Microsoft.Data.Sqlite.SqliteConnection conn, string packageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid, id, name, moniker, latest_version, hash FROM packages WHERE id = @id COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("@id", packageId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var hashValue = reader.GetValue(5);
        var hash = hashValue is byte[] blob
            ? Convert.ToHexString(blob).ToLowerInvariant()
            : (hashValue?.ToString() ?? string.Empty).ToLowerInvariant();
        return new V2PackageMetadata(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            hash);
    }

    /// <summary>
    /// MSIX entries whose Windows resource-string display name (e.g.
    /// <c>ms-resource:appDisplayName</c>) hasn't been resolved by the AppX
    /// runtime would otherwise leak that placeholder into pinget's output.
    /// winget resolves it via Windows.Management.Deployment and shows the
    /// catalog's PackageName; we mirror that by substituting the correlated
    /// catalog name once the package is correlated, which is the
    /// parity-faithful substitute we can produce without taking a WinRT
    /// dependency.
    /// </summary>
    internal static void ApplyMsixResourceStringNameFix(InstalledPackage package)
    {
        if (!package.LocalId.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)) return;
        if (!package.Name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return;
        var candidate = package.Correlated;
        if (candidate is null) return;
        if (!string.IsNullOrEmpty(candidate.Name)) package.Name = candidate.Name;
    }

    /// <summary>
    /// Runs after both correlation paths to enrich already-correlated rows
    /// with information that the identity path applies inline but that
    /// name-based correlation skips: the MSIX-placeholder name fix and the
    /// aMiV/aMaV version remap. Without this, packages without a
    /// ProductCode/PFN/UpgradeCode (e.g. JetBrains.Rider) keep showing the
    /// raw ARP DisplayVersion (`253.28294.112`) instead of the catalog
    /// Version (`2025.3.0.1`).
    /// </summary>
    private List<string> EnrichCorrelatedViaIndex(List<InstalledPackage> installed, string? requestedSource)
    {
        // Cheap pass first — no SQL required.
        foreach (var pkg in installed)
            ApplyMsixResourceStringNameFix(pkg);

        // Expensive pass — load versionData for rows whose installed_version
        // wasn't remapped earlier.
        var warnings = new List<string>();
        for (int sourceIndex = 0; sourceIndex < _store.Sources.Count; sourceIndex++)
        {
            var source = _store.Sources[sourceIndex];
            if (source.Kind != SourceKind.PreIndexed) continue;
            if (requestedSource is not null && !source.Name.Equals(requestedSource, StringComparison.OrdinalIgnoreCase))
                continue;

            Microsoft.Data.Sqlite.SqliteConnection conn;
            try { conn = OpenPreindexedConnection(sourceIndex); }
            catch { continue; }

            var needs = new List<(int Index, V2PackageMetadata Meta)>();
            using (conn)
            {
                if (!V2IdentityTablesPresent(conn)) continue;
                for (int i = 0; i < installed.Count; i++)
                {
                    var pkg = installed[i];
                    if (pkg.Correlated is null) continue;
                    // Stay within the source the row was correlated to —
                    // looking up `JetBrains.Rider` in a different catalog
                    // would either miss or pull the wrong package's range.
                    if (!pkg.Correlated.SourceName.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var meta = LookupV2MetadataById(conn, pkg.Correlated.Id);
                    if (meta is null) continue;
                    needs.Add((i, meta));
                }
            }

            foreach (var (idx, meta) in needs)
            {
                // Load versionData once: needed for both the aMiV/aMaV
                // remap (if installed_version isn't canonical yet) and to
                // locate the latest manifest for RequireExplicitUpgrade.
                List<PreIndexedSource.V2VersionDataEntry>? entries = null;
                try
                {
                    using var conn2 = OpenPreindexedConnection(sourceIndex);
                    var (loaded, _) = PreIndexedSource.LoadV2VersionData(_client, conn2, source, meta.Rowid, meta.PackageHash, _appRoot);
                    entries = loaded;
                }
                catch { /* best-effort */ }

                if (entries is null) continue;

                if (!installed[idx].InstalledVersionCanonical)
                {
                    var canonicalInstalled = MapArpVersionToCatalog(entries, installed[idx].InstalledVersion)
                        ?? LessThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion)
                        ?? GreaterThanLatestArpAnchoredVersion(entries, installed[idx].InstalledVersion, meta.LatestVersion);
                    var anchoredLatest = LatestArpAnchoredVersion(entries);
                    if (canonicalInstalled is not null)
                    {
                        installed[idx].InstalledVersion = canonicalInstalled;
                        installed[idx].InstalledVersionCanonical = true;
                        if (installed[idx].Correlated is SearchMatch correlated && anchoredLatest is not null)
                        {
                            installed[idx].Correlated = correlated with
                            {
                                Version = IsGreaterThanVersionMarker(canonicalInstalled) ? null : anchoredLatest
                            };
                        }
                    }
                }

                // RequireExplicitUpgrade is on the installer manifest.
                // winget hides those from bulk `upgrade` (Edge, Steam,
                // Discord). Read the manifest of the version the row would
                // upgrade to: versionData is not sorted, so take the entry
                // matching the correlated available version, else the
                // highest version.
                var latest = UpgradeTargetVersionEntry(entries, installed[idx].Correlated?.Version);
                if (latest is not null)
                {
                    try
                    {
                        var bytes = PreIndexedSource.GetCachedSourceFile(
                            _client, "V2_M", source, latest.ManifestRelativePath, latest.ManifestHash, _appRoot);
                        var manifest = ParseYamlManifest(bytes);
                        installed[idx].CorrelatedRequiresExplicitUpgrade = manifest.RequireExplicitUpgrade;
                        installed[idx].CorrelatedLacksCompatibleInstaller = !ManifestHasCompatibleInstaller(manifest);
                    }
                    catch { /* best-effort */ }
                }
            }
        }
        return warnings;
    }

    /// <summary>
    /// The versionData entry whose manifest describes the upgrade target: the entry equal to
    /// <paramref name="targetVersion"/> when there is one, otherwise the highest version. versionData lists
    /// versions in file order, not sorted.
    /// </summary>
    internal static PreIndexedSource.V2VersionDataEntry? UpgradeTargetVersionEntry(
        IReadOnlyList<PreIndexedSource.V2VersionDataEntry> entries, string? targetVersion)
    {
        if (!string.IsNullOrWhiteSpace(targetVersion))
        {
            foreach (var entry in entries)
            {
                if (RestSource.CompareVersionStrings(entry.Version, targetVersion) == 0)
                    return entry;
            }
        }

        PreIndexedSource.V2VersionDataEntry? best = null;
        foreach (var entry in entries)
        {
            if (best is null || RestSource.CompareVersionStrings(entry.Version, best.Version) > 0)
                best = entry;
        }
        return best;
    }

    /// <summary>
    /// Collapses installed entries that correlate to the same catalog package
    /// id (case-insensitive within a source) down to a single representative
    /// — the one with the highest installed version, preferring rows whose
    /// version was successfully remapped via aMiV/aMaV. Uncorrelated entries
    /// pass through untouched (they can't appear in upgrade output anyway).
    /// </summary>
    internal static List<InstalledPackage> DedupeCorrelatedForUpgrade(List<InstalledPackage> packages)
    {
        var byId = new Dictionary<(string Id, string Source), InstalledPackage>();
        var uncorrelated = new List<InstalledPackage>();
        foreach (var pkg in packages)
        {
            if (pkg.Correlated is null)
            {
                uncorrelated.Add(pkg);
                continue;
            }
            var key = (
                pkg.Correlated.Id.ToLowerInvariant(),
                pkg.Correlated.SourceName.ToLowerInvariant()
            );
            if (!byId.TryGetValue(key, out var existing))
            {
                byId[key] = pkg;
                continue;
            }
            // Prefer rows whose ARP version was mapped to a catalog Version.
            // Comparing a raw ARP DisplayVersion like `40.10.18029` against
            // the catalog's `10.0.300` is meaningless and would also hide
            // the row that legitimately upgrades to it.
            bool keepNew;
            if (pkg.InstalledVersionCanonical && !existing.InstalledVersionCanonical)
                keepNew = true;
            else if (!pkg.InstalledVersionCanonical && existing.InstalledVersionCanonical)
                keepNew = false;
            else
                keepNew = RestSource.CompareVersionStrings(pkg.InstalledVersion, existing.InstalledVersion) > 0;
            if (keepNew) byId[key] = pkg;
        }

        var result = new List<InstalledPackage>(uncorrelated);
        result.AddRange(byId.Values);
        return result;
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

    private static bool InstalledPackageMatchesUpgradeFilter(InstalledPackage pkg, ListQuery query)
    {
        // Hide RequireExplicitUpgrade rows from bulk `upgrade` (Edge, Steam,
        // Discord). Explicit `--id` filters surface them regardless.
        if (pkg.CorrelatedRequiresExplicitUpgrade && !ListQueryNeedsAvailableLookup(query))
            return false;
        // Hide rows whose latest catalog version has no installer the host
        // can run — winget refuses to upgrade past an unusable architecture.
        if (pkg.CorrelatedLacksCompatibleInstaller && !ListQueryNeedsAvailableLookup(query))
            return false;
        return InstalledPackageHasUpgrade(pkg)
            || (query.IncludeUnknown && InstalledPackageHasUnknownVersion(pkg) && pkg.Correlated is not null);
    }

    /// <summary>
    /// True when the host can use at least one installer, applying the same applicability rules as
    /// install (architecture, platform, minimum OS version).
    /// </summary>
    private static bool ManifestHasCompatibleInstaller(Manifest manifest)
    {
        if (manifest.Installers.Count == 0) return true;
        return SelectInstaller(manifest.Installers, new PackageQuery()) is not null;
    }

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

    /// <summary>
    /// Whether <paramref name="pin"/> prevents upgrading to <paramref name="targetVersion"/>, following
    /// WinGet's pin semantics: Blocking always blocks, Pinning blocks unless pinned packages are included
    /// (--include-pinned or an explicit single-package upgrade), and Gating only allows versions inside the
    /// gated range.
    /// </summary>
    public static bool PinBlocksUpgrade(PinRecord pin, string targetVersion, bool includePinned) => pin.PinType switch
    {
        PinType.Blocking => true,
        PinType.Pinning => !includePinned,
        PinType.Gating => !string.IsNullOrWhiteSpace(targetVersion) && !VersionMatchesPinPattern(targetVersion, pin.Version),
        _ => false,
    };

    /// <summary>
    /// Whether a pin hides <paramref name="match"/> from the upgrade list. <paramref name="includePinned"/>
    /// relaxes only Pinning pins, as in WinGet's EvaluatePinnedStateForVersion: Blocking pins still block and
    /// Gating pins still reject versions outside their range. The default is the implicit (upgrade /
    /// upgrade --all) path.
    /// </summary>
    internal static bool IsUpgradeBlockedByPin(ListMatch match, IReadOnlyList<PinRecord> pins, bool includePinned = false)
    {
        if (string.IsNullOrWhiteSpace(match.AvailableVersion))
            return false;

        var pin = FindApplicablePin(match, pins);
        if (pin is null)
            return false;

        return PinBlocksUpgrade(pin, match.AvailableVersion!, includePinned);
    }

    /// <summary>
    /// Mirrors WinGet's GatedVersion::IsValidVersion: a pattern ending in ".*" matches versions whose leading
    /// parts equal the pattern's parts (missing version parts count as 0); any other pattern must equal the
    /// version. Parts are compared as versions, so "1.2.*" matches "1.2", "1.2.0" and "1.2.5" but not "1.20",
    /// and "1.2" equals "1.2.0".
    /// </summary>
    internal static bool VersionMatchesPinPattern(string version, string pattern)
    {
        pattern = pattern?.Trim() ?? "";
        if (pattern.Length == 0 || pattern == "*")
            return true;

        var gateParts = pattern.Split('.').Select(part => part.Trim()).ToArray();
        if (gateParts[^1] != "*")
            return RestSource.CompareVersionStrings(version, pattern) == 0;

        var versionParts = version.Trim().Split('.').Select(part => part.Trim()).ToArray();
        for (var i = 0; i < gateParts.Length - 1; i++)
        {
            var versionPart = i < versionParts.Length ? versionParts[i] : "0";
            if (RestSource.CompareVersionStrings(versionPart, gateParts[i]) != 0)
                return false;
        }

        return true;
    }

    private static int ListSortWeight(InstalledPackage pkg)
    {
        if (pkg.LocalId.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (pkg.Name.Contains(".SparseApp") || pkg.LocalId.Contains(".SparseApp_"))
            return 1;
        return 2;
    }

    private ListMatch ListMatchFromInstalled(InstalledPackage pkg)
        => ListMatchFromInstalled(pkg, _store.Sources);

    internal static ListMatch ListMatchFromInstalledForTesting(InstalledPackage pkg, IReadOnlyList<SourceRecord> sources)
        => ListMatchFromInstalled(pkg, sources);

    private static ListMatch ListMatchFromInstalled(InstalledPackage pkg, IReadOnlyList<SourceRecord> sources)
    {
        string? availableVersion = null;
        if (pkg.Correlated?.Version is string av)
        {
            if (!IsGreaterThanVersionMarker(pkg.InstalledVersion) &&
                (InstalledPackageHasUnknownVersion(pkg) ||
                 RestSource.CompareVersionStrings(av, pkg.InstalledVersion) > 0))
            {
                availableVersion = av;
            }
        }

        string packageId = pkg.Correlated?.Id ?? pkg.LocalId;
        string? sourceName = pkg.Correlated?.SourceName;
        if (sourceName is null && !string.IsNullOrWhiteSpace(pkg.WinGetPackageIdentifier))
        {
            packageId = pkg.WinGetPackageIdentifier;
            if (!string.IsNullOrWhiteSpace(pkg.WinGetSourceIdentifier))
                sourceName = SourceNameFromIdentifier(pkg.WinGetSourceIdentifier, sources);
        }

        if (sourceName is null && TryGetWinGetPackageIdentityFromLocalId(
            pkg.LocalId,
            sources,
            out string? localPackageId,
            out string? localSourceName))
        {
            packageId = localPackageId;
            sourceName = localSourceName;
        }

        return new()
        {
            Name = pkg.Name,
            Id = packageId,
            LocalId = pkg.LocalId,
            InstalledVersion = pkg.InstalledVersion,
            AvailableVersion = availableVersion,
            SourceName = sourceName,
            Publisher = pkg.Publisher,
            Scope = pkg.Scope,
            InstallerCategory = pkg.InstallerCategory,
            InstallLocation = pkg.InstallLocation,
            PackageFamilyNames = pkg.PackageFamilyNames,
            ProductCodes = pkg.ProductCodes,
            UpgradeCodes = pkg.UpgradeCodes,
        };
    }

    internal static List<ListMatch> SuppressDuplicateAvailableVersions(IReadOnlyList<ListMatch> matches)
    {
        var duplicateKeys = matches
            .Where(match => !string.IsNullOrWhiteSpace(match.SourceName))
            .GroupBy(match => $"{match.SourceName}\0{match.Id}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        if (duplicateKeys.Count == 0)
            return matches.ToList();

        var keepLocalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, group) in duplicateKeys)
        {
            var bestVersion = group
                .Select(match => match.InstalledVersion)
                .Aggregate((best, version) => RestSource.CompareVersionStrings(version, best) > 0 ? version : best);
            foreach (var match in group.Where(match => RestSource.CompareVersionStrings(match.InstalledVersion, bestVersion) == 0))
                keepLocalIds.Add($"{key}\0{match.LocalId}");
        }

        return matches
            .Select(match =>
            {
                if (match.AvailableVersion is null || string.IsNullOrWhiteSpace(match.SourceName))
                    return match;

                var key = $"{match.SourceName}\0{match.Id}";
                if (!duplicateKeys.ContainsKey(key))
                    return match;

                return keepLocalIds.Contains($"{key}\0{match.LocalId}")
                    ? match
                    : match with { AvailableVersion = null };
            })
            .ToList();
    }

    private static string SourceNameFromIdentifier(string identifier, IReadOnlyList<SourceRecord> sources) =>
        sources.FirstOrDefault(source => source.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Name
        ?? identifier;

    internal static bool TryGetWinGetPackageIdentityFromLocalId(
        string localId,
        IReadOnlyList<SourceRecord> sources,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? packageId,
        out string? sourceName)
    {
        packageId = null;
        sourceName = null;

        int packageIdStartIndex = localId.LastIndexOf('\\') + 1;
        SourceRecord? source = null;
        int sourceIdentifierStartIndex = -1;
        foreach (SourceRecord candidate in sources)
        {
            string sourceSuffix = "_" + candidate.Identifier;
            if (!localId.EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            source = candidate;
            sourceIdentifierStartIndex = localId.Length - sourceSuffix.Length;
            break;
        }

        if (source is null)
            return false;

        if (sourceIdentifierStartIndex <= packageIdStartIndex)
            return false;

        packageId = localId[packageIdStartIndex..sourceIdentifierStartIndex];
        sourceName = source.Name;
        return !string.IsNullOrWhiteSpace(packageId);
    }

    private static bool ListQueryNeedsAvailableLookup(ListQuery query)
        => query.Query is not null || query.Id is not null || query.Name is not null ||
           query.Moniker is not null || query.Tag is not null || query.Command is not null;

    internal static bool AllowLooseListCorrelation(ListQuery query) =>
        !query.Exact && (query.Query is not null || query.Id is not null || query.Name is not null);

    private static bool MatchesText(string candidate, string query, bool exact) =>
        exact
            ? candidate.Equals(query, StringComparison.OrdinalIgnoreCase)
            : candidate.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool InstalledPackageHasUnknownVersion(InstalledPackage pkg) =>
        pkg.InstalledVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool InstalledPackageHasUpgrade(InstalledPackage pkg) =>
        !IsGreaterThanVersionMarker(pkg.InstalledVersion) &&
        !InstalledPackageHasUnknownVersion(pkg) &&
        pkg.Correlated?.Version is string availableVersion &&
        RestSource.CompareVersionStrings(availableVersion, pkg.InstalledVersion) > 0;

    private static bool IsGreaterThanVersionMarker(string version) =>
        version.TrimStart().StartsWith('>');

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

        // WinGet does not filter on Platform for install, and Windows.Universal (UWP) packages run on
        // desktop Windows: unless a platform was requested explicitly, Universal installers apply.
        return installer.Platforms.Any(platform =>
            platform.Equals(effectivePlatform, StringComparison.OrdinalIgnoreCase) ||
            (requestedPlatform is null && platform.Equals(UniversalPlatform, StringComparison.OrdinalIgnoreCase)));
    }

    private const string UniversalPlatform = "Windows.Universal";

    private static bool InstallerMatchesOsVersion(Installer installer, string? requestedOsVersion, string? currentOsVersion)
    {
        if (string.IsNullOrWhiteSpace(installer.MinimumOsVersion))
            return true;

        var effectiveOsVersion = requestedOsVersion ?? currentOsVersion;
        if (string.IsNullOrWhiteSpace(effectiveOsVersion))
            return true;

        if (TryParseDottedVersion(installer.MinimumOsVersion!, out var minimumVersion) &&
            TryParseDottedVersion(effectiveOsVersion, out var actualVersion))
        {
            return CompareDottedVersions(actualVersion, minimumVersion) >= 0;
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

    /// <summary>
    /// WinGet <c>MarketFilter</c>: a non-empty <c>AllowedMarkets</c> must contain the market; otherwise the
    /// market must not be in <c>ExcludedMarkets</c>. Compared case-insensitively.
    /// </summary>
    internal static bool InstallerMatchesMarket(Installer installer, string market)
    {
        bool Contains(List<string> markets) =>
            markets.Any(value => value.Trim().Equals(market, StringComparison.OrdinalIgnoreCase));
        return installer.AllowedMarkets.Count > 0
            ? Contains(installer.AllowedMarkets)
            : !Contains(installer.ExcludedMarkets);
    }

    /// <summary>
    /// WinGet <c>MachineArchitectureComparator</c>: an installer listing the system architecture in
    /// <c>UnsupportedOSArchitectures</c> is not applicable (it would otherwise run under emulation).
    /// </summary>
    internal static bool InstallerUnsupportedOnArchitecture(Installer installer, string systemArchitecture)
        => installer.UnsupportedOSArchitectures.Any(value =>
            value.Trim().Equals(systemArchitecture, StringComparison.OrdinalIgnoreCase));

    private static (int Architecture, int Locale, int Commands) InstallerRank(
        Installer installer,
        string? requestedLocale,
        IReadOnlyList<string> preferredLocales,
        string? requestedArchitecture,
        string systemArchitecture)
    {
        var architecture = ArchitectureRank(
            installer.Architecture,
            requestedArchitecture ?? systemArchitecture,
            requestedArchitecture is not null,
            systemArchitecture);
        var locale = requestedLocale is not null
            ? LocaleRank(installer.Locale, requestedLocale)
            : PreferredLocaleRank(installer.Locale, preferredLocales);
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

    /// <summary>
    /// Locale preference without <c>--locale</c>, after WinGet's <c>LocaleComparator</c>: the first preferred
    /// language that an installer matches decides (an exact tag beats the same language), an installer declaring
    /// no locale beats one whose locale matches no preference, and with no preferences at all the locale does not
    /// rank installers.
    /// </summary>
    internal static int PreferredLocaleRank(string? installerLocale, IReadOnlyList<string> preferredLocales)
    {
        if (preferredLocales.Count == 0)
            return 0;
        var locale = installerLocale?.Trim();
        if (string.IsNullOrEmpty(locale))
            return 1;

        for (var index = 0; index < preferredLocales.Count; index++)
        {
            var preferred = preferredLocales[index].Trim();
            var weight = 2 + (preferredLocales.Count - index) * 2;
            if (locale.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                return weight + 1;
            var language = locale.Split('-')[0];
            if (language.Length > 0 && language.Equals(preferred.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                return weight;
        }

        return 0;
    }

    /// <summary>
    /// The user's preferred UI languages (BCP-47 tags, most preferred first), which WinGet uses as the installer
    /// locale preference when no locale is requested.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> PreferredInstallerLocales = new(ReadPreferredUiLanguages);

    private const uint MuiLanguageName = 0x8;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint numLanguages, char[]? languagesBuffer, ref uint languagesBufferLength);

    private static IReadOnlyList<string> ReadPreferredUiLanguages()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                uint length = 0;
                if (GetUserPreferredUILanguages(MuiLanguageName, out _, null, ref length) && length > 0)
                {
                    var buffer = new char[length];
                    if (GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref length))
                        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            return [];
        }

        var name = System.Globalization.CultureInfo.CurrentUICulture.Name;
        return string.IsNullOrEmpty(name) ? [] : [name];
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

    /// <summary>Parses a dotted numeric version of any length, like the Rust parse_dotted_version.</summary>
    private static bool TryParseDottedVersion(string value, out ulong[] version)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        version = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version[i]))
                return false;
        }
        return parts.Length > 0;
    }

    /// <summary>
    /// Compares dotted numeric versions component by component, treating missing trailing components
    /// as zero, so <c>10.0.19041</c> equals <c>10.0.19041.0</c>.
    /// </summary>
    internal static int CompareDottedVersions(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right)
    {
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var cmp = (i < left.Count ? left[i] : 0UL).CompareTo(i < right.Count ? right[i] : 0UL);
            if (cmp != 0)
                return cmp;
        }
        return 0;
    }

    /// <summary>
    /// A requested <c>--locale</c> is a requirement, not a preference: an installer that declares a
    /// different language is not applicable. Installers that declare no locale stay applicable.
    /// </summary>
    private static bool InstallerMatchesLocale(string? installerLocale, string? requestedLocale)
        => LocaleRank(installerLocale, requestedLocale) >= 0;

    internal static string CurrentArchitecture() => RuntimeInformation.OSArchitecture switch
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

        for (int i = 0; i < _store.Sources.Count; i++)
        {
            if (_store.Sources[i].Identifier.Equals(sourceName, StringComparison.OrdinalIgnoreCase))
                return [i];
        }

        throw new InvalidOperationException($"Source '{sourceName}' not found.");
    }

    private void RefreshSystemWingetSources()
    {
        _store = _sourceMode switch
        {
            EffectiveSourceMode.Private => _store,
            EffectiveSourceMode.SystemWingetDirect => SystemWingetSourceStore.Load(),
            EffectiveSourceMode.SystemWingetMirror => SourceStoreManager.RefreshSystemWingetMirrorStore(_appRoot),
            _ => _store,
        };
    }

    /// <summary>
    /// A query must not fail because the sources could not be re-mirrored: the cached mirror is
    /// what the previous queries already ran against. Mutating commands keep using the strict
    /// refresh above, since they have to see the real source list.
    /// </summary>
    private string? RefreshSystemWingetSourcesForQuery() =>
        RefreshSystemWingetSourcesForQuery(TimeSpan.FromMinutes(SystemWingetMirrorAutoRefreshMinutes));

    internal string? RefreshSystemWingetSourcesForQuery(TimeSpan maxAge)
    {
        switch (_sourceMode)
        {
            case EffectiveSourceMode.SystemWingetDirect:
                try
                {
                    _store = SystemWingetSourceStore.Load();
                    return null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException
                    or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return SystemWingetSourceRefreshWarning(ex);
                }

            case EffectiveSourceMode.SystemWingetMirror:
                if (SourceStoreManager.SystemWingetMirrorIsFresh(_appRoot, maxAge))
                    return null;

                try
                {
                    _store = SourceStoreManager.RefreshSystemWingetMirrorStore(_appRoot);
                    return null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException
                    or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return SystemWingetSourceRefreshWarning(ex);
                }

            default:
                return null;
        }
    }

    private static string SystemWingetSourceRefreshWarning(Exception error) =>
        $"Could not refresh the system WinGet sources; using the sources Pinget cached earlier. {error.Message}";

    private void SaveStore()
    {
        if (MirrorsSystemWingetSources)
            SourceStoreManager.SaveSystemWingetMirrorStore(_appRoot, _store);
        else
            SourceStoreManager.Save(_store, _appRoot);
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

[System.Text.Json.Serialization.JsonSerializable(typeof(Repository.InstallerDownloadMetadata))]
internal partial class InstallerDownloadMetadataContext : System.Text.Json.Serialization.JsonSerializerContext;
