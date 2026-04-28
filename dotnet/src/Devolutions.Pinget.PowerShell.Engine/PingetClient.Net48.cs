#if NET48
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.ComInterop;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Engine;

public sealed class PingetClient : IDisposable
{
    private readonly NativePingetComLibrary _library;
    private readonly PingetPackageManager _packageManager;

    private PingetClient(NativePingetComLibrary library, PingetPackageManager packageManager)
    {
        _library = library;
        _packageManager = packageManager;
    }

    public static PingetClient Open(object? options = null)
    {
        if (options is not null)
            throw new NotSupportedException("Windows PowerShell uses the native Pinget COM bridge and does not accept managed repository options.");

        var library = NativePingetComLibrary.LoadFromDefaultLocation();
        try
        {
            return new PingetClient(library, library.CreatePackageManager());
        }
        catch
        {
            library.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _packageManager.Dispose();
        _library.Dispose();
    }

    public string GetVersion() => _packageManager.GetVersion();

    public CollectionResult<PSSourceResult> GetSources(string? name = null)
    {
        var sourceArray = JsonNode.Parse(_packageManager.ListSourcesJson()) as JsonArray
            ?? throw new InvalidOperationException("The native Pinget COM bridge returned invalid source data.");

        var sources = sourceArray
            .OfType<JsonObject>()
            .Select(ToSourceResult)
            .Where(source => string.IsNullOrWhiteSpace(name) || string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!string.IsNullOrWhiteSpace(name) && sources.Count == 0)
            throw new InvalidOperationException($"Source '{name}' not found.");

        return new CollectionResult<PSSourceResult>(sources, Array.Empty<string>());
    }

    public void AddSource(string name, string argument, string? type, PSSourceTrustLevel trustLevel, bool explicitSource, int priority)
    {
        _packageManager.AddSource(
            name,
            argument,
            string.IsNullOrWhiteSpace(type) ? "Microsoft.Rest" : type,
            trustLevel == PSSourceTrustLevel.Default ? "None" : trustLevel.ToString(),
            explicitSource,
            priority);
    }

    public void RemoveSource(string name) => _packageManager.RemoveSource(name);

    public void ResetSource(string? name, bool all) => _packageManager.ResetSource(name, all);

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
        var response = RequiredObject(_packageManager.SearchJson(ToJsonString(BuildPackageQuery(id, name, moniker, source, query, tag, command, count, matchOption))));
        var items = RequiredArray(response, "matches")
            .OfType<JsonObject>()
            .Select(match => new PSFoundCatalogPackage(
                RequiredString(match, "id"),
                RequiredString(match, "name"),
                RequiredString(match, "source_name"),
                OptionalString(match, "moniker"),
                OptionalString(match, "version"),
                OptionalString(match, "match_criteria")))
            .ToList();

        return new CollectionResult<PSFoundCatalogPackage>(items, WarningsFrom(response), Bool(response, "truncated"));
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
        var response = RequiredObject(_packageManager.ListJson(ToJsonString(BuildListQuery(id, name, moniker, source, query, tag, command, count, matchOption))));
        var items = RequiredArray(response, "matches")
            .OfType<JsonObject>()
            .Select(match => new PSInstalledCatalogPackage(
                RequiredString(match, "id"),
                RequiredString(match, "name"),
                OptionalString(match, "source_name") ?? string.Empty,
                null,
                RequiredString(match, "installed_version"),
                OptionalString(match, "available_version") is { } availableVersion ? new[] { availableVersion } : Array.Empty<string>(),
                OptionalString(match, "publisher"),
                OptionalString(match, "scope"),
                StringArray(match, "package_family_names"),
                StringArray(match, "product_codes")))
            .ToList();

        return new CollectionResult<PSInstalledCatalogPackage>(items, WarningsFrom(response), Bool(response, "truncated"));
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
        var queryObject = BuildPackageQuery(
            id ?? inputObject?.Id,
            name ?? inputObject?.Name,
            moniker ?? inputObject?.Moniker,
            source ?? inputObject?.Source,
            query,
            null,
            null,
            0,
            PSPackageFieldMatchOption.EqualsCaseInsensitive);
        SetIfNotNull(queryObject, "version", version ?? GetCatalogVersion(inputObject));
        SetIfNotNull(queryObject, "locale", locale);
        SetIfNotNull(queryObject, "installer_architecture", architecture == PSProcessorArchitecture.Default ? null : architecture.ToString());
        SetIfNotNull(queryObject, "installer_type", installerType == PSPackageInstallerType.Default ? null : installerType.ToString());
        SetIfNotNull(queryObject, "install_scope", scope == PSPackageInstallScope.Any ? null : scope.ToString());
        SetIfNotNull(queryObject, "platform", ToInstallerPlatform(platform));
        SetIfNotNull(queryObject, "os_version", Normalize(targetOsVersion));

        var request = new JsonObject
        {
            ["query"] = queryObject,
            ["skip_dependencies"] = skipDependencies,
            ["ignore_security_hash"] = allowHashMismatch,
        };
        var outputDirectory = string.IsNullOrWhiteSpace(downloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : downloadDirectory!;

        var response = RequiredObject(_packageManager.DownloadInstallerJson(ToJsonString(request), outputDirectory));
        var manifest = RequiredObject(response, "manifest");
        var installerPath = RequiredString(response, "installer_path");
        return new CommandResult<PSDownloadResult>(
            new PSDownloadResult
            {
                Id = RequiredString(manifest, "id"),
                Name = RequiredString(manifest, "name"),
                Source = OptionalString(queryObject, "source") ?? inputObject?.Source ?? string.Empty,
                CorrelationData = installerPath,
                Status = "Ok",
                Version = RequiredString(manifest, "version"),
                DownloadDirectory = outputDirectory,
                DownloadedInstallerPath = installerPath,
            },
            warnings);
    }

    public JsonObject GetUserSettings() => RequiredObject(_packageManager.GetUserSettingsJson());

    public JsonObject SetUserSettings(JsonObject userSettings, bool merge) =>
        RequiredObject(_packageManager.SetUserSettingsJson(ToJsonString(userSettings), merge));

    public bool TestUserSettings(JsonObject expected, bool ignoreNotSet) =>
        _packageManager.TestUserSettingsJson(ToJsonString(expected), ignoreNotSet);

    public JsonObject GetAdminSettings() => RequiredObject(_packageManager.GetAdminSettingsJson());

    public void SetAdminSetting(string name, bool enabled) => _packageManager.SetAdminSetting(name, enabled);

    public void AssertPackageManager(string? version, bool latest, bool includePrerelease)
    {
        if (!string.IsNullOrWhiteSpace(version) &&
            !string.Equals(version, GetVersion(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Requested version '{version}' does not match Pinget version '{GetVersion()}'.");
        }

        _ = _packageManager.GetDefaultAppRoot();
        _ = _packageManager.ListSourcesJson();
        _ = _packageManager.GetAdminSettingsJson();
        _ = _packageManager.GetUserSettingsJson();
    }

    public CommandResult<int> RepairPackageManager(string? version, bool latest, bool includePrerelease, bool allUsers, bool force)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(version) &&
            !string.Equals(version, GetVersion(), StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Requested version '{version}' is not available; repaired current Pinget version '{GetVersion()}' instead.");
        if (latest)
            warnings.Add("Latest is accepted for compatibility but Pinget repairs the current local build.");
        if (includePrerelease)
            warnings.Add("IncludePrerelease is accepted for compatibility but Pinget repairs the current local build.");
        if (allUsers)
            warnings.Add("AllUsers is accepted for compatibility but Pinget repairs the current user's app root.");
        if (force)
            warnings.Add("Force is accepted for compatibility but Pinget repair does not require process shutdown today.");

        _packageManager.EnsureSettingsFiles();
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
        var warnings = HeaderWarnings(header);
        var request = BuildInstallRequest(
            inputObject,
            version,
            id,
            name,
            moniker,
            source,
            query,
            allowHashMismatch,
            overrideArguments,
            custom,
            location,
            log,
            force,
            skipDependencies,
            locale,
            scope,
            architecture,
            mode,
            installerType);

        var result = RequiredObject(_packageManager.InstallJson(ToJsonString(request)));
        warnings.AddRange(WarningsFrom(result));
        return new CommandResult<PSInstallResult>(ToPsInstallResult(result, request, inputObject), warnings);
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
        var request = new JsonObject
        {
            ["query"] = BuildPackageQuery(
                id ?? inputObject?.Id,
                name ?? inputObject?.Name,
                moniker ?? inputObject?.Moniker,
                source ?? inputObject?.Source,
                query,
                null,
                null,
                0,
                PSPackageFieldMatchOption.EqualsCaseInsensitive),
            ["force"] = force,
            ["mode"] = ToInstallerMode(mode),
        };
        SetIfNotNull(request, "log_path", Normalize(log));
        SetIfNotNull((JsonObject)request["query"]!, "version", version ?? GetCatalogVersion(inputObject));

        var result = RequiredObject(_packageManager.UninstallJson(ToJsonString(request)));
        return new CommandResult<PSUninstallResult>(ToPsUninstallResult(result, request, inputObject), WarningsFrom(result));
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
        var warnings = HeaderWarnings(header);
        var listQuery = new JsonObject
        {
            ["id"] = Normalize(id ?? inputObject?.Id),
            ["name"] = Normalize(name ?? inputObject?.Name),
            ["moniker"] = Normalize(moniker ?? inputObject?.Moniker),
            ["source"] = Normalize(source ?? inputObject?.Source),
            ["query"] = JoinQuery(query),
            ["version"] = Normalize(version),
            ["upgrade_only"] = true,
            ["include_unknown"] = includeUnknown,
            ["exact"] = true,
        };

        var matches = RequiredObject(_packageManager.ListJson(ToJsonString(listQuery)));
        warnings.AddRange(WarningsFrom(matches));
        var results = new List<PSInstallResult>();
        foreach (var match in RequiredArray(matches, "matches").OfType<JsonObject>())
        {
            var matchSource = OptionalString(match, "source_name");
            var request = BuildInstallRequest(
                null,
                Normalize(version) ?? OptionalString(match, "available_version"),
                RequiredString(match, "id"),
                null,
                null,
                matchSource,
                null,
                allowHashMismatch,
                overrideArguments,
                custom,
                location,
                log,
                force,
                skipDependencies,
                locale,
                scope,
                architecture,
                mode,
                installerType);
            var result = RequiredObject(_packageManager.InstallJson(ToJsonString(request)));
            warnings.AddRange(WarningsFrom(result));
            results.Add(ToPsInstallResult(result, request, new PSInstalledCatalogPackage(
                RequiredString(match, "id"),
                RequiredString(match, "name"),
                matchSource ?? string.Empty,
                null,
                RequiredString(match, "installed_version"),
                OptionalString(match, "available_version") is { } availableVersion ? new[] { availableVersion } : Array.Empty<string>(),
                OptionalString(match, "publisher"),
                OptionalString(match, "scope"))));
        }

        return new CollectionResult<PSInstallResult>(results, warnings, Bool(matches, "truncated"));
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
        var request = new JsonObject
        {
            ["query"] = BuildPackageQuery(
                id ?? inputObject?.Id,
                name ?? inputObject?.Name,
                moniker ?? inputObject?.Moniker,
                source ?? inputObject?.Source,
                query,
                null,
                null,
                0,
                PSPackageFieldMatchOption.EqualsCaseInsensitive),
            ["ignore_security_hash"] = allowHashMismatch,
            ["force"] = force,
            ["mode"] = ToInstallerMode(mode),
        };
        SetIfNotNull(request, "log_path", Normalize(log));
        SetIfNotNull((JsonObject)request["query"]!, "version", version ?? GetCatalogVersion(inputObject));

        var result = RequiredObject(_packageManager.RepairJson(ToJsonString(request)));
        var repairedVersion = Normalize(version) ?? GetCatalogVersion(inputObject) ?? RequiredString(result, "version");
        return new CommandResult<PSRepairResult>(ToPsRepairResult(result, request, new PSInstalledCatalogPackage(
            RequiredString(result, "package_id"),
            inputObject?.Name ?? RequiredString(result, "package_id"),
            Normalize(source ?? inputObject?.Source) ?? string.Empty,
            null,
            repairedVersion,
            Array.Empty<string>(),
            null,
            (inputObject as PSInstalledCatalogPackage)?.Scope)), WarningsFrom(result));
    }

    private static JsonObject BuildPackageQuery(
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
        var result = new JsonObject();
        SetIfNotNull(result, "id", Normalize(id));
        SetIfNotNull(result, "name", Normalize(name));
        SetIfNotNull(result, "moniker", Normalize(moniker));
        SetIfNotNull(result, "source", Normalize(source));
        SetIfNotNull(result, "query", JoinQuery(query));
        SetIfNotNull(result, "tag", Normalize(tag));
        SetIfNotNull(result, "command", Normalize(command));
        if (count != 0)
            result["count"] = count;
        result["exact"] = UsesExactMatch(matchOption);
        return result;
    }

    private static JsonObject BuildListQuery(
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
        var result = new JsonObject();
        SetIfNotNull(result, "id", Normalize(id));
        SetIfNotNull(result, "name", Normalize(name));
        SetIfNotNull(result, "moniker", Normalize(moniker));
        SetIfNotNull(result, "source", Normalize(source));
        SetIfNotNull(result, "query", JoinQuery(query));
        SetIfNotNull(result, "tag", Normalize(tag));
        SetIfNotNull(result, "command", Normalize(command));
        if (count != 0)
            result["count"] = count;
        result["exact"] = UsesExactMatch(matchOption);
        return result;
    }

    private static JsonObject BuildInstallRequest(
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
        bool skipDependencies,
        string? locale,
        PSPackageInstallScope scope,
        PSProcessorArchitecture architecture,
        PSPackageInstallMode mode,
        PSPackageInstallerType installerType)
    {
        var queryObject = BuildPackageQuery(
            id ?? inputObject?.Id,
            name ?? inputObject?.Name,
            moniker ?? inputObject?.Moniker,
            source ?? inputObject?.Source,
            query,
            null,
            null,
            0,
            PSPackageFieldMatchOption.EqualsCaseInsensitive);
        SetIfNotNull(queryObject, "version", version ?? GetCatalogVersion(inputObject));
        SetIfNotNull(queryObject, "locale", locale);
        SetIfNotNull(queryObject, "installer_architecture", architecture == PSProcessorArchitecture.Default ? null : architecture.ToString());
        SetIfNotNull(queryObject, "installer_type", installerType == PSPackageInstallerType.Default ? null : installerType.ToString());
        SetIfNotNull(queryObject, "install_scope", scope == PSPackageInstallScope.Any ? null : scope.ToString());

        var request = new JsonObject
        {
            ["query"] = queryObject,
            ["force"] = force,
            ["skip_dependencies"] = skipDependencies,
            ["ignore_security_hash"] = allowHashMismatch,
            ["mode"] = ToInstallerMode(mode),
        };
        SetIfNotNull(request, "override_args", Normalize(overrideArguments));
        SetIfNotNull(request, "custom", Normalize(custom));
        SetIfNotNull(request, "install_location", Normalize(location));
        SetIfNotNull(request, "log_path", Normalize(log));
        return request;
    }

    private static PSInstallResult ToPsInstallResult(JsonObject result, JsonObject request, PSCatalogPackage? inputObject)
    {
        var query = RequiredObject(request, "query");
        var packageId = RequiredString(result, "package_id");
        var identity = ResolveIdentity(packageId, OptionalString(query, "name") ?? inputObject?.Name, OptionalString(query, "source") ?? inputObject?.Source);
        return new PSInstallResult
        {
            Id = packageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = OptionalString(result, "installer_path") ?? string.Empty,
            InstallerErrorCode = unchecked((uint)Int(result, "exit_code")),
            RebootRequired = false,
            Status = OperationStatus(result),
        };
    }

    private static PSUninstallResult ToPsUninstallResult(JsonObject result, JsonObject request, PSCatalogPackage? inputObject)
    {
        var query = RequiredObject(request, "query");
        var packageId = RequiredString(result, "package_id");
        var identity = ResolveIdentity(packageId, OptionalString(query, "name") ?? inputObject?.Name, OptionalString(query, "source") ?? inputObject?.Source);
        return new PSUninstallResult
        {
            Id = packageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = OptionalString(result, "installer_path") ?? string.Empty,
            UninstallerErrorCode = unchecked((uint)Int(result, "exit_code")),
            RebootRequired = false,
            Status = OperationStatus(result),
        };
    }

    private static PSRepairResult ToPsRepairResult(JsonObject result, JsonObject request, PSCatalogPackage? inputObject)
    {
        var query = RequiredObject(request, "query");
        var packageId = RequiredString(result, "package_id");
        var identity = ResolveIdentity(packageId, OptionalString(query, "name") ?? inputObject?.Name, OptionalString(query, "source") ?? inputObject?.Source);
        return new PSRepairResult
        {
            Id = packageId,
            Name = identity.Name,
            Source = identity.Source,
            CorrelationData = OptionalString(result, "installer_path") ?? string.Empty,
            RepairErrorCode = unchecked((uint)Int(result, "exit_code")),
            RebootRequired = false,
            Status = OperationStatus(result),
        };
    }

    private static string OperationStatus(JsonObject result) =>
        Bool(result, "success") ? "Ok" : Bool(result, "no_op") ? "NoOp" : "Error";

    private static (string Name, string? Source) ResolveIdentity(string id, string? name, string? source) =>
        (string.IsNullOrWhiteSpace(name) ? id : name!, Normalize(source));

    private static List<string> HeaderWarnings(IDictionary? headers)
    {
        if (headers is null || headers.Count == 0)
            return new List<string>();

        return new List<string>
        {
            "Header is accepted for compatibility but the Rust COM core does not support per-request headers.",
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

    private static string? ToInstallerPlatform(PSWindowsPlatform platform)
    {
        switch (platform)
        {
            case PSWindowsPlatform.Universal:
                return "Windows.Universal";
            case PSWindowsPlatform.Desktop:
                return "Windows.Desktop";
            case PSWindowsPlatform.IoT:
                return "Windows.IoT";
            case PSWindowsPlatform.Team:
                return "Windows.Team";
            case PSWindowsPlatform.Holographic:
                return "Windows.Holographic";
            default:
                return null;
        }
    }

    private static string ToInstallerMode(PSPackageInstallMode mode)
    {
        switch (mode)
        {
            case PSPackageInstallMode.Silent:
                return "Silent";
            case PSPackageInstallMode.Interactive:
                return "Interactive";
            default:
                return "SilentWithProgress";
        }
    }

    private static string ToInstallerMode(PSPackageUninstallMode mode)
    {
        switch (mode)
        {
            case PSPackageUninstallMode.Silent:
                return "Silent";
            case PSPackageUninstallMode.Interactive:
                return "Interactive";
            default:
                return "SilentWithProgress";
        }
    }

    private static string ToInstallerMode(PSPackageRepairMode mode)
    {
        switch (mode)
        {
            case PSPackageRepairMode.Silent:
                return "Silent";
            case PSPackageRepairMode.Interactive:
                return "Interactive";
            default:
                return "SilentWithProgress";
        }
    }

    private static string? GetCatalogVersion(PSCatalogPackage? inputObject)
    {
        if (inputObject is PSFoundCatalogPackage found)
            return found.Version;
        if (inputObject is PSInstalledCatalogPackage installed && installed.AvailableVersions.Count > 0)
            return installed.AvailableVersions[0];
        if (inputObject is PSInstalledCatalogPackage installedPackage)
            return installedPackage.InstalledVersion;
        return null;
    }

    private static bool UsesExactMatch(PSPackageFieldMatchOption matchOption) =>
        matchOption == PSPackageFieldMatchOption.Equals || matchOption == PSPackageFieldMatchOption.EqualsCaseInsensitive;

    private static string? JoinQuery(string[]? query)
    {
        if (query is null || query.Length == 0)
            return null;

        var values = query.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return values.Length == 0 ? null : string.Join(" ", values);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ToJsonString(JsonObject value) => value.ToJsonString();

    private static void SetIfNotNull(JsonObject target, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[name] = value;
    }

    private static JsonObject RequiredObject(string json)
    {
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("The native Pinget COM bridge returned invalid JSON object data.");
    }

    private static JsonObject RequiredObject(JsonObject source, string name)
    {
        return source[name] as JsonObject
            ?? throw new InvalidOperationException($"The native Pinget COM bridge returned an object without '{name}'.");
    }

    private static JsonArray RequiredArray(JsonObject source, string name)
    {
        return source[name] as JsonArray
            ?? throw new InvalidOperationException($"The native Pinget COM bridge returned an object without '{name}'.");
    }

    private static List<string> WarningsFrom(JsonObject source) => StringArray(source, "warnings").ToList();

    private static string[] StringArray(JsonObject source, string name)
    {
        return (source[name] as JsonArray)?
            .Select(item => item?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray() ?? Array.Empty<string>();
    }

    private static bool Bool(JsonObject source, string name) => source[name]?.GetValue<bool>() ?? false;

    private static int Int(JsonObject source, string name) => source[name]?.GetValue<int>() ?? 0;

    private static PSSourceResult ToSourceResult(JsonObject source)
    {
        return new PSSourceResult
        {
            Name = RequiredString(source, "name"),
            Argument = RequiredString(source, "argument"),
            Type = RequiredString(source, "type"),
            TrustLevel = OptionalString(source, "trustLevel") ?? "None",
            Explicit = source["explicit"]?.GetValue<bool>() ?? false,
            Priority = source["priority"]?.GetValue<int>() ?? 0,
            Identifier = RequiredString(source, "identifier"),
            LastUpdate = ParseDateTime(OptionalString(source, "lastUpdate")),
            SourceVersion = OptionalString(source, "sourceVersion"),
        };
    }

    private static string RequiredString(JsonObject source, string name) =>
        OptionalString(source, name) ?? throw new InvalidOperationException($"The native Pinget COM bridge returned a source without '{name}'.");

    private static string? OptionalString(JsonObject source, string name) => source[name]?.GetValue<string>();

    private static DateTime? ParseDateTime(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;
}
#endif
