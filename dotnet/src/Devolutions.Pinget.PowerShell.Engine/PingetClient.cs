using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Core;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Engine;

public sealed class PingetClient : IDisposable
{
    private readonly Repository _repository;

    private PingetClient(Repository repository)
    {
        _repository = repository;
    }

    public static PingetClient Open(RepositoryOptions? options = null) => new(Repository.Open(options));

    public void Dispose() => _repository.Dispose();

    public string GetVersion() => PowerShellEngineVersion.Current;

    public CollectionResult<PSSourceResult> GetSources(string? name = null)
    {
        var sources = _repository.ListSources();
        if (!string.IsNullOrWhiteSpace(name))
        {
            sources = sources
                .Where(source => string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sources.Count == 0)
                throw new InvalidOperationException($"Source '{name}' not found.");
        }

        return new CollectionResult<PSSourceResult>(
            sources.Select(source => new PSSourceResult
            {
                Name = source.Name,
                Argument = source.Arg,
                Type = source.Kind switch
                {
                    SourceKind.Rest => "Microsoft.Rest",
                    SourceKind.PreIndexed => "Microsoft.PreIndexed.Package",
                    _ => source.Kind.ToString(),
                },
                TrustLevel = string.IsNullOrWhiteSpace(source.TrustLevel) ? "None" : source.TrustLevel,
                Explicit = source.Explicit,
                Priority = source.Priority,
                Identifier = source.Identifier,
                LastUpdate = source.LastUpdate,
                SourceVersion = source.SourceVersion,
            }).ToList(),
            []);
    }

    public void AddSource(string name, string argument, string? type, PSSourceTrustLevel trustLevel, bool explicitSource, int priority)
    {
        _repository.AddSource(
            name,
            argument,
            ParseSourceKind(type),
            trustLevel == PSSourceTrustLevel.Default ? "None" : trustLevel.ToString(),
            explicitSource,
            priority);
    }

    public void RemoveSource(string name) => _repository.RemoveSource(name);

    public void ResetSource(string? name, bool all)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _repository.ResetSource(name);
            return;
        }

        if (!all)
            throw new InvalidOperationException("Resetting all sources requires -All.");

        _repository.ResetSources();
    }

    public CollectionResult<PSFoundCatalogPackage> FindPackages(
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        string? tag,
        string? command,
        uint count,
        PSPackageFieldMatchOption matchOption)
    {
        var response = _repository.Search(BuildPackageQuery(id, name, moniker, source, query, tag, command, count, matchOption));
        return new CollectionResult<PSFoundCatalogPackage>(
            response.Matches.Select(match => new PSFoundCatalogPackage(
                match.Id,
                match.Name,
                match.SourceName,
                match.Moniker,
                match.Version,
                match.MatchCriteria)).ToList(),
            response.Warnings,
            response.Truncated);
    }

    public CollectionResult<PSInstalledCatalogPackage> GetInstalledPackages(
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        string? tag,
        string? command,
        uint count,
        PSPackageFieldMatchOption matchOption)
    {
        var response = _repository.List(BuildListQuery(id, name, moniker, source, query, tag, command, count, matchOption));
        return new CollectionResult<PSInstalledCatalogPackage>(
            response.Matches.Select(match => new PSInstalledCatalogPackage(
                match.Id,
                match.Name,
                match.SourceName ?? string.Empty,
                null,
                match.InstalledVersion,
                string.IsNullOrWhiteSpace(match.AvailableVersion) ? [] : [match.AvailableVersion],
                match.Publisher,
                match.Scope)).ToList(),
            response.Warnings,
            response.Truncated);
    }

    public CommandResult<PSDownloadResult> DownloadPackage(
        PSCatalogPackage? inputObject,
        string? version,
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        bool allowHashMismatch,
        bool skipDependencies,
        string? locale,
        PSPackageInstallScope scope,
        PSProcessorArchitecture architecture,
        PSPackageInstallerType installerType,
        string? downloadDirectory,
        bool skipMicrosoftStoreLicense,
        PSWindowsPlatform platform,
        string? targetOsVersion)
    {
        var warnings = new List<string>();
        AddDownloadCompatibilityWarnings(warnings, skipDependencies, skipMicrosoftStoreLicense);

        var request = new InstallRequest
        {
            Query = BuildPackageQuery(
                id ?? inputObject?.Id,
                name ?? inputObject?.Name,
                moniker ?? inputObject?.Moniker,
                source ?? inputObject?.Source,
                query,
                null,
                null,
                0,
                PSPackageFieldMatchOption.EqualsCaseInsensitive) with
            {
                Version = version ?? GetCatalogVersion(inputObject),
                Locale = locale,
                InstallerArchitecture = architecture == PSProcessorArchitecture.Default ? null : architecture.ToString(),
                InstallerType = installerType == PSPackageInstallerType.Default ? null : installerType.ToString(),
                InstallScope = scope == PSPackageInstallScope.Any ? null : scope.ToString(),
                Platform = ToInstallerPlatform(platform),
                OsVersion = Normalize(targetOsVersion),
            },
            SkipDependencies = skipDependencies,
            IgnoreSecurityHash = allowHashMismatch,
        };

        var outputDirectory = string.IsNullOrWhiteSpace(downloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : downloadDirectory;

        var (manifest, installerPath) = _repository.DownloadInstaller(request, outputDirectory);
        return new CommandResult<PSDownloadResult>(
            new PSDownloadResult
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Source = request.Query.Source ?? inputObject?.Source ?? string.Empty,
                CorrelationData = installerPath,
                Status = "Ok",
                Version = manifest.Version,
                DownloadDirectory = outputDirectory,
                DownloadedInstallerPath = installerPath,
            },
            warnings);
    }

    public JsonObject GetUserSettings() => _repository.GetUserSettings();

    public JsonObject SetUserSettings(JsonObject userSettings, bool merge) => _repository.SetUserSettings(userSettings, merge);

    public bool TestUserSettings(JsonObject expected, bool ignoreNotSet) => _repository.TestUserSettings(expected, ignoreNotSet);

    public JsonObject GetAdminSettings() => _repository.GetAdminSettings();

    public void SetAdminSetting(string name, bool enabled) => _repository.SetAdminSetting(name, enabled);

    public void AssertPackageManager(string? version, bool latest, bool includePrerelease)
    {
        var issues = new List<string>();

        if (!string.IsNullOrWhiteSpace(version) &&
            !string.Equals(version, GetVersion(), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add($"Requested version '{version}' does not match Pinget version '{GetVersion()}'.");
        }

        if (latest && !includePrerelease)
        {
            // Pinget is self-hosted in this workspace, so the current build is the only version we can assert.
        }

        try
        {
            _ = _repository.ListSources();
            _ = GetAdminSettings();
            _ = GetUserSettings();
        }
        catch (Exception ex)
        {
            issues.Add(ex.Message);
        }

        if (issues.Count != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, issues));
    }

    public CommandResult<int> RepairPackageManager(string? version, bool latest, bool includePrerelease, bool allUsers, bool force)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(version) &&
            !string.Equals(version, GetVersion(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Requested version '{version}' is not available; repaired current Pinget version '{GetVersion()}' instead.");
        }
        if (latest)
            warnings.Add("Latest is accepted for compatibility but Pinget repairs the current local build.");
        if (includePrerelease)
            warnings.Add("IncludePrerelease is accepted for compatibility but Pinget repairs the current local build.");
        if (allUsers)
            warnings.Add("AllUsers is accepted for compatibility but Pinget repairs the current user's app root.");
        if (force)
            warnings.Add("Force is accepted for compatibility but Pinget repair does not require process shutdown today.");

        Directory.CreateDirectory(_repository.AppRoot);
        Directory.CreateDirectory(Path.Combine(_repository.AppRoot, "sources"));
        var sourcesPath = Path.Combine(_repository.AppRoot, "sources.json");
        if (!File.Exists(sourcesPath))
            _repository.ResetSources();
        _repository.EnsureSettingsFiles();

        AssertPackageManager(null, false, false);
        return new CommandResult<int>(0, warnings);
    }

    public CommandResult<PSInstallResult> InstallPackage(
        PSCatalogPackage? inputObject,
        string? version,
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        bool allowHashMismatch,
        string? overrideArguments,
        string? custom,
        string? location,
        string? log,
        bool force,
        IDictionary? header,
        bool skipDependencies,
        string? locale,
        PSPackageInstallScope scope,
        PSProcessorArchitecture architecture,
        PSPackageInstallMode mode,
        PSPackageInstallerType installerType)
    {
        var warnings = new List<string>();
        ApplyRequestHeaders(header);

        var request = new InstallRequest
        {
            Query = BuildPackageQuery(
                id ?? inputObject?.Id,
                name ?? inputObject?.Name,
                moniker ?? inputObject?.Moniker,
                source ?? inputObject?.Source,
                query,
                null,
                null,
                0,
                PSPackageFieldMatchOption.EqualsCaseInsensitive) with
            {
                Version = version ?? GetCatalogVersion(inputObject),
                Locale = locale,
                InstallerArchitecture = architecture == PSProcessorArchitecture.Default ? null : architecture.ToString(),
                InstallerType = installerType == PSPackageInstallerType.Default ? null : installerType.ToString(),
                InstallScope = scope == PSPackageInstallScope.Any ? null : scope.ToString(),
            },
            Override = Normalize(overrideArguments),
            Custom = Normalize(custom),
            InstallLocation = Normalize(location),
            LogPath = Normalize(log),
            Force = force,
            SkipDependencies = skipDependencies,
            IgnoreSecurityHash = allowHashMismatch,
            Mode = ToInstallerMode(mode),
        };

        var result = _repository.Install(request);
        return new CommandResult<PSInstallResult>(ToPsInstallResult(result, request, inputObject), warnings.Concat(result.Warnings).ToList());
    }

    public CommandResult<PSUninstallResult> UninstallPackage(
        PSCatalogPackage? inputObject,
        string? version,
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        bool force,
        string? log,
        PSPackageUninstallMode mode)
    {
        var request = new UninstallRequest
        {
            Query = BuildPackageQuery(
                id ?? inputObject?.Id,
                name ?? inputObject?.Name,
                moniker ?? inputObject?.Moniker,
                source ?? inputObject?.Source,
                query,
                null,
                null,
                0,
                PSPackageFieldMatchOption.EqualsCaseInsensitive) with
            {
                Version = version ?? GetCatalogVersion(inputObject),
            },
            Force = force,
            LogPath = Normalize(log),
            Mode = ToInstallerMode(mode),
        };

        var result = _repository.Uninstall(request);
        return new CommandResult<PSUninstallResult>(ToPsUninstallResult(result, request, inputObject), result.Warnings);
    }

    public CollectionResult<PSInstallResult> UpdatePackages(
        PSCatalogPackage? inputObject,
        string? version,
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        bool allowHashMismatch,
        string? overrideArguments,
        string? custom,
        string? location,
        string? log,
        bool force,
        IDictionary? header,
        bool skipDependencies,
        string? locale,
        PSPackageInstallScope scope,
        PSProcessorArchitecture architecture,
        PSPackageInstallMode mode,
        PSPackageInstallerType installerType,
        bool includeUnknown)
    {
        var warnings = new List<string>();
        ApplyRequestHeaders(header);

        var listQuery = new ListQuery
        {
            Id = Normalize(id ?? inputObject?.Id),
            Name = Normalize(name ?? inputObject?.Name),
            Moniker = Normalize(moniker ?? inputObject?.Moniker),
            Source = Normalize(source ?? inputObject?.Source),
            Query = JoinQuery(query),
            Version = Normalize(version),
            UpgradeOnly = true,
            IncludeUnknown = includeUnknown,
            Exact = true,
        };

        var matches = _repository.List(listQuery);
        warnings.AddRange(matches.Warnings);

        var results = new List<PSInstallResult>();
        foreach (var match in matches.Matches)
        {
            var request = new InstallRequest
            {
                Query = new PackageQuery
                {
                    Id = match.Id,
                    Source = match.SourceName,
                    Exact = true,
                    Version = Normalize(version) ?? match.AvailableVersion,
                    Locale = locale,
                    InstallerArchitecture = architecture == PSProcessorArchitecture.Default ? null : architecture.ToString(),
                    InstallerType = installerType == PSPackageInstallerType.Default ? null : installerType.ToString(),
                    InstallScope = scope == PSPackageInstallScope.Any ? null : scope.ToString(),
                },
                Override = Normalize(overrideArguments),
                Custom = Normalize(custom),
                InstallLocation = Normalize(location),
                LogPath = Normalize(log),
                Force = force,
                SkipDependencies = skipDependencies,
                IgnoreSecurityHash = allowHashMismatch,
                Mode = ToInstallerMode(mode),
            };

            var result = _repository.Install(request);
            warnings.AddRange(result.Warnings);
            results.Add(ToPsInstallResult(result, request, new PSInstalledCatalogPackage(
                match.Id,
                match.Name,
                match.SourceName ?? string.Empty,
                null,
                match.InstalledVersion,
                string.IsNullOrWhiteSpace(match.AvailableVersion) ? [] : [match.AvailableVersion],
                match.Publisher,
                match.Scope)));
        }

        return new CollectionResult<PSInstallResult>(results, warnings, matches.Truncated);
    }

    public CommandResult<PSRepairResult> RepairPackage(
        PSCatalogPackage? inputObject,
        string? version,
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        bool allowHashMismatch,
        bool force,
        string? log,
        PSPackageRepairMode mode)
    {
        var warnings = new List<string>();
        var request = new RepairRequest
        {
            Query = new PackageQuery
            {
                Id = Normalize(id ?? inputObject?.Id),
                Name = Normalize(name ?? inputObject?.Name),
                Moniker = Normalize(moniker ?? inputObject?.Moniker),
                Source = Normalize(source ?? inputObject?.Source),
                Query = JoinQuery(query),
                Version = Normalize(version) ?? GetCatalogVersion(inputObject),
                Exact = true,
            },
            IgnoreSecurityHash = allowHashMismatch,
            Force = force,
            LogPath = Normalize(log),
            Mode = ToInstallerMode(mode),
        };

        var result = _repository.Repair(request);
        var repairedVersion = Normalize(version) ?? GetCatalogVersion(inputObject) ?? result.Version;
        warnings.AddRange(result.Warnings);
        return new CommandResult<PSRepairResult>(ToPsRepairResult(result, request, new PSInstalledCatalogPackage(
            result.PackageId,
            inputObject?.Name ?? result.PackageId,
            Normalize(source ?? inputObject?.Source) ?? string.Empty,
            null,
            repairedVersion,
            [],
            null,
            (inputObject as PSInstalledCatalogPackage)?.Scope)), warnings);
    }

    private static PackageQuery BuildPackageQuery(
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        string? tag,
        string? command,
        uint count,
        PSPackageFieldMatchOption matchOption)
    {
        return new PackageQuery
        {
            Id = Normalize(id),
            Name = Normalize(name),
            Moniker = Normalize(moniker),
            Source = Normalize(source),
            Query = JoinQuery(query),
            Tag = Normalize(tag),
            Command = Normalize(command),
            Count = count == 0 ? null : (int)count,
            Exact = UsesExactMatch(matchOption),
        };
    }

    private static ListQuery BuildListQuery(
        string? id,
        string? name,
        string? moniker,
        string? source,
        string[]? query,
        string? tag,
        string? command,
        uint count,
        PSPackageFieldMatchOption matchOption)
    {
        return new ListQuery
        {
            Id = Normalize(id),
            Name = Normalize(name),
            Moniker = Normalize(moniker),
            Source = Normalize(source),
            Query = JoinQuery(query),
            Tag = Normalize(tag),
            Command = Normalize(command),
            Count = count == 0 ? null : (int)count,
            Exact = UsesExactMatch(matchOption),
        };
    }

    private static void AddDownloadCompatibilityWarnings(
        List<string> warnings,
        bool skipDependencies,
        bool skipMicrosoftStoreLicense)
    {
        if (skipDependencies)
            warnings.Add("SkipDependencies does not affect Pinget export and was ignored.");
    }

    private void ApplyRequestHeaders(IDictionary? headers)
    {
        if (headers is null)
            return;

        foreach (DictionaryEntry entry in headers)
        {
            if (entry.Key is null)
                continue;

            var name = Normalize(entry.Key.ToString())
                ?? throw new InvalidOperationException("Header name is required.");
            var value = Normalize(entry.Value?.ToString())
                ?? throw new InvalidOperationException($"Header '{name}' requires a value.");
            _repository.SetRequestHeader(name, value);
        }
    }

    private static string? ToInstallerPlatform(PSWindowsPlatform platform) => platform switch
    {
        PSWindowsPlatform.Default => null,
        PSWindowsPlatform.Universal => "Windows.Universal",
        PSWindowsPlatform.Desktop => "Windows.Desktop",
        PSWindowsPlatform.IoT => "Windows.IoT",
        PSWindowsPlatform.Team => "Windows.Team",
        PSWindowsPlatform.Holographic => "Windows.Holographic",
        _ => null,
    };

    private static string? GetCatalogVersion(PSCatalogPackage? inputObject) => inputObject switch
    {
        PSFoundCatalogPackage found => found.Version,
        PSInstalledCatalogPackage installed when installed.AvailableVersions.Count > 0 => installed.AvailableVersions[0],
        PSInstalledCatalogPackage installed => installed.InstalledVersion,
        _ => null,
    };

    private static InstallerMode ToInstallerMode(PSPackageInstallMode mode) => mode switch
    {
        PSPackageInstallMode.Silent => InstallerMode.Silent,
        PSPackageInstallMode.Interactive => InstallerMode.Interactive,
        _ => InstallerMode.SilentWithProgress,
    };

    private static InstallerMode ToInstallerMode(PSPackageUninstallMode mode) => mode switch
    {
        PSPackageUninstallMode.Silent => InstallerMode.Silent,
        PSPackageUninstallMode.Interactive => InstallerMode.Interactive,
        _ => InstallerMode.SilentWithProgress,
    };

    private static InstallerMode ToInstallerMode(PSPackageRepairMode mode) => mode switch
    {
        PSPackageRepairMode.Silent => InstallerMode.Silent,
        PSPackageRepairMode.Interactive => InstallerMode.Interactive,
        _ => InstallerMode.SilentWithProgress,
    };

    private static PSInstallResult ToPsInstallResult(InstallResult result, InstallRequest request, PSCatalogPackage? inputObject)
    {
        var identity = ResolveIdentity(result.PackageId, request.Query.Name ?? inputObject?.Name, request.Query.Source ?? inputObject?.Source);
        return new PSInstallResult
        {
            Id = result.PackageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = result.InstallerPath,
            InstallerErrorCode = unchecked((uint)result.ExitCode),
            RebootRequired = false,
            Status = result.Success ? "Ok" : result.NoOp ? "NoOp" : "Error",
        };
    }

    private static PSUninstallResult ToPsUninstallResult(InstallResult result, UninstallRequest request, PSCatalogPackage? inputObject)
    {
        var identity = ResolveIdentity(result.PackageId, request.Query.Name ?? inputObject?.Name, request.Query.Source ?? inputObject?.Source);
        return new PSUninstallResult
        {
            Id = result.PackageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = result.InstallerPath,
            UninstallerErrorCode = unchecked((uint)result.ExitCode),
            RebootRequired = false,
            Status = result.Success ? "Ok" : result.NoOp ? "NoOp" : "Error",
        };
    }

    private static PSRepairResult ToPsRepairResult(InstallResult result, RepairRequest request, PSCatalogPackage? inputObject)
    {
        var identity = ResolveIdentity(result.PackageId, request.Query.Name ?? inputObject?.Name, request.Query.Source ?? inputObject?.Source);
        return new PSRepairResult
        {
            Id = result.PackageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = result.InstallerPath,
            RepairErrorCode = unchecked((uint)result.ExitCode),
            RebootRequired = false,
            Status = result.Success ? "Ok" : result.NoOp ? "NoOp" : "Error",
        };
    }

    private static (string Name, string? Source) ResolveIdentity(string id, string? name, string? source) =>
        (string.IsNullOrWhiteSpace(name) ? id : name, Normalize(source));

    private static bool UsesExactMatch(PSPackageFieldMatchOption matchOption) =>
        matchOption is PSPackageFieldMatchOption.Equals or PSPackageFieldMatchOption.EqualsCaseInsensitive;

    private static string? JoinQuery(string[]? query)
    {
        if (query is null || query.Length == 0)
            return null;

        var values = query.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static SourceKind ParseSourceKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "rest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.Rest;
        }

        if (string.Equals(value, "preindexed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
        {
            return SourceKind.PreIndexed;
        }

        throw new InvalidOperationException($"Unsupported source type: {value}");
    }
}
