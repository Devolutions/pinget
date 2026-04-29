using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Devolutions.Pinget.Core;

internal record SourceStore
{
    public List<SourceRecord> Sources { get; set; } = [];

    public static SourceStore Default() => new()
    {
        Sources =
        [
            new SourceRecord
            {
                Name = "winget",
                Kind = SourceKind.PreIndexed,
                Arg = "https://cdn.winget.microsoft.com/cache",
                Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                TrustLevel = "Trusted",
            },
            new SourceRecord
            {
                Name = "msstore",
                Kind = SourceKind.Rest,
                Arg = "https://storeedgefd.dsx.mp.microsoft.com/v9.0",
                Identifier = "StoreEdgeFD",
                TrustLevel = "Trusted",
            }
        ]
    };
}

[JsonSerializable(typeof(SourceStore))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal partial class SourceStoreContext : JsonSerializerContext;

internal static class SourceStoreManager
{
    internal const string PackagedFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    internal const string PackagedName = "Microsoft.DesktopAppInstaller";

    private const string LegacyStoreFileName = "sources.json";
    private const string PackagedSourcesFileName = "user_sources";
    private const string PackagedMetadataFileName = "sources_metadata";
    private const string LegacyPinsFileName = "pins.db";
    private const string PackagedPinsFileName = "pinning.db";

    public static string NormalizeAppRoot(string? appRoot)
    {
        if (!string.IsNullOrWhiteSpace(appRoot))
            return Path.GetFullPath(appRoot);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return OperatingSystem.IsWindows()
            ? Path.Combine(localAppData, "Packages", PackagedFamilyName, "LocalState")
            : Path.Combine(localAppData, "pinget");
    }

    public static void EnsureAppDirs(string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        Directory.CreateDirectory(root);

        if (UsesPackagedLayout(root))
        {
            Directory.CreateDirectory(GetPackagedSourceCacheRoot(root));

            var userSourcesDirectory = Path.GetDirectoryName(GetPackagedUserSourcesPath(root));
            if (!string.IsNullOrWhiteSpace(userSourcesDirectory))
                Directory.CreateDirectory(userSourcesDirectory);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(root, "sources"));
        }
    }

    public static string SourceStateDir(SourceRecord source, string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        if (UsesPackagedLayout(root))
        {
            return Path.Combine(
                root,
                ToPackagedSourceType(source.Kind),
                SanitizePathSegment(source.Identifier));
        }

        return Path.Combine(root, "sources", SanitizePathSegment(source.Name));
    }

    public static string PinsDbPath(string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        return Path.Combine(root, UsesPackagedLayout(root) ? PackagedPinsFileName : LegacyPinsFileName);
    }

    public static SourceStore Load(string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        if (UsesPackagedLayout(root))
        {
            var store = LoadPackagedStore(root);
            if (store is not null)
                return store;
        }

        var legacyPath = Path.Combine(root, LegacyStoreFileName);
        if (!File.Exists(legacyPath))
            return SourceStore.Default();

        var json = File.ReadAllText(legacyPath);
        return JsonSerializer.Deserialize(json, SourceStoreContext.Default.SourceStore) ?? SourceStore.Default();
    }

    internal static bool UsesSystemWingetSourceCommands(string? appRoot)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var root = NormalizeAppRoot(appRoot);
        if (!UsesPackagedLayout(root))
            return false;

        var userSourcesPath = GetPackagedUserSourcesPath(root);
        var userSourcesYaml = File.Exists(userSourcesPath) ? File.ReadAllText(userSourcesPath) : null;
        return SystemWingetSourceStore.IsSecureSettingsStub(userSourcesYaml);
    }

    public static void Save(SourceStore store, string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        if (UsesSystemWingetSourceCommands(root))
            throw new InvalidOperationException("System WinGet source settings must be modified through winget.");

        if (UsesPackagedLayout(root))
        {
            SavePackagedStore(root, store);
            return;
        }

        var path = Path.Combine(root, LegacyStoreFileName);
        var json = JsonSerializer.Serialize(store, SourceStoreContext.Default.SourceStore);
        File.WriteAllText(path, json);
    }

    internal static bool UsesPackagedLayout(string? appRoot)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!TryGetPackagedLocalStateRoot(NormalizeAppRoot(appRoot), out _))
            return false;

        return true;
    }

    internal static string GetPackagedUserSourcesPath(string? appRoot)
    {
        var root = NormalizeAppRoot(appRoot);
        TryGetPackagedLocalStateRoot(root, out _);
        return Path.Combine(GetPackagedSecureSettingsRoot(), PackagedSourcesFileName);
    }

    internal static string GetPackagedUserSettingsPath(string? appRoot) =>
        Path.Combine(NormalizeAppRoot(appRoot), "settings.json");

    internal static string GetPackagedFileCacheRoot(string? appRoot) =>
        Path.Combine(NormalizeAppRoot(appRoot), "Microsoft", "Windows Package Manager");

    internal static SourceStore? ParsePackagedSourceStore(string? userSourcesYaml, string? metadataYaml)
    {
        if (string.IsNullOrWhiteSpace(userSourcesYaml) && string.IsNullOrWhiteSpace(metadataYaml))
            return null;

        var store = SourceStore.Default();
        var sources = store.Sources.ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var sourceNode in EnumerateYamlSources(userSourcesYaml))
        {
            var name = GetYamlScalar(sourceNode, "Name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (GetYamlBool(sourceNode, "IsTombstone"))
            {
                sources.Remove(name);
                continue;
            }

            if (!TryMapSourceRecord(sourceNode, out var mapped))
                continue;

            sources[name] = mapped;
        }

        foreach (var sourceNode in EnumerateYamlSources(metadataYaml))
        {
            var name = GetYamlScalar(sourceNode, "Name");
            if (string.IsNullOrWhiteSpace(name) || !sources.TryGetValue(name, out var source))
                continue;

            if (TryGetYamlDateTime(sourceNode, "LastUpdate", out var lastUpdate))
                source.LastUpdate = lastUpdate;

            var sourceVersion = GetYamlScalar(sourceNode, "SourceVersion");
            if (!string.IsNullOrWhiteSpace(sourceVersion))
                source.SourceVersion = sourceVersion;
        }

        store.Sources = sources.Values
            .OrderBy(source => source.Explicit)
            .ThenByDescending(source => source.Priority)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return store;
    }

    internal static SourceStore? LoadPackagedStoreFromStreams(string? userSourcesYaml, string? metadataYaml)
    {
        if (SystemWingetSourceStore.IsSecureSettingsStub(userSourcesYaml))
            return SystemWingetSourceStore.Load();

        return ParsePackagedSourceStore(userSourcesYaml, metadataYaml);
    }

    private static SourceStore? LoadPackagedStore(string root)
    {
        var userSourcesPath = GetPackagedUserSourcesPath(root);
        var metadataPath = Path.Combine(root, PackagedMetadataFileName);

        var userSourcesYaml = File.Exists(userSourcesPath) ? File.ReadAllText(userSourcesPath) : null;
        var metadataYaml = File.Exists(metadataPath) ? File.ReadAllText(metadataPath) : null;
        return LoadPackagedStoreFromStreams(userSourcesYaml, metadataYaml);
    }

    private static void SavePackagedStore(string root, SourceStore store)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var sourceDocument = new Dictionary<string, object?>
        {
            ["Sources"] = store.Sources.Select(source => new Dictionary<string, object?>
            {
                ["Name"] = source.Name,
                ["Type"] = ToPackagedSourceType(source.Kind),
                ["Arg"] = source.Arg,
                ["Data"] = source.Identifier,
                ["TrustLevel"] = ToPackagedTrustLevel(source.TrustLevel),
                ["Explicit"] = source.Explicit,
                ["Priority"] = source.Priority,
                ["IsTombstone"] = false,
            }).ToList(),
        };

        var metadataDocument = new Dictionary<string, object?>
        {
            ["Sources"] = store.Sources.Select(source => new Dictionary<string, object?>
            {
                ["Name"] = source.Name,
                ["LastUpdate"] = source.LastUpdate.HasValue
                    ? new DateTimeOffset(source.LastUpdate.Value).ToUnixTimeSeconds()
                    : null,
                ["SourceVersion"] = source.SourceVersion,
            }).Where(entry => entry["LastUpdate"] is not null || entry["SourceVersion"] is not null).ToList(),
        };

        var userSourcesPath = GetPackagedUserSourcesPath(root);
        var userSourcesDirectory = Path.GetDirectoryName(userSourcesPath);
        if (!string.IsNullOrWhiteSpace(userSourcesDirectory))
            Directory.CreateDirectory(userSourcesDirectory);
        File.WriteAllText(userSourcesPath, serializer.Serialize(sourceDocument));

        File.WriteAllText(Path.Combine(root, PackagedMetadataFileName), serializer.Serialize(metadataDocument));
    }

    private static bool TryGetPackagedLocalStateRoot(string root, out string localStateRoot)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (!directory.Name.Equals("LocalState", StringComparison.OrdinalIgnoreCase) ||
            directory.Parent?.Name is null ||
            !directory.Parent.Name.Equals(PackagedFamilyName, StringComparison.OrdinalIgnoreCase) ||
            directory.Parent.Parent?.Name is null ||
            !directory.Parent.Parent.Name.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
            directory.Parent.Parent.Parent?.FullName is null)
        {
            localStateRoot = string.Empty;
            return false;
        }

        localStateRoot = directory.FullName;
        return true;
    }

    private static string GetPackagedSecureSettingsRoot()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var sid = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? "UnknownSid"
            : "UnknownSid";
        return Path.Combine(programData, "Microsoft", "WinGet", sid, "settings", "pkg", PackagedName);
    }

    private static string GetPackagedSourceCacheRoot(string root) =>
        Path.Combine(root, "Microsoft", "Windows Package Manager");

    private static IEnumerable<Dictionary<string, string?>> EnumerateYamlSources(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            yield break;

        Dictionary<string, string?>? current = null;
        foreach (var rawLine in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') ||
                string.Equals(trimmed, "Sources:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                if (current is not null)
                    yield return current;

                current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                AddYamlKeyValue(current, trimmed[2..]);
                continue;
            }

            if (current is not null)
                AddYamlKeyValue(current, trimmed);
        }

        if (current is not null)
            yield return current;
    }

    private static bool TryMapSourceRecord(Dictionary<string, string?> yamlSource, out SourceRecord source)
    {
        source = null!;
        var name = GetYamlScalar(yamlSource, "Name");
        var type = GetYamlScalar(yamlSource, "Type");
        var arg = GetYamlScalar(yamlSource, "Arg");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(arg))
            return false;

        if (!TryMapSourceKind(type, out var kind))
            return false;

        source = new SourceRecord
        {
            Name = name,
            Kind = kind,
            Arg = arg,
            Identifier = GetYamlScalar(yamlSource, "Identifier") ?? GetYamlScalar(yamlSource, "Data") ?? name,
            TrustLevel = MapYamlTrustLevel(yamlSource),
            Explicit = GetYamlBool(yamlSource, "Explicit"),
            Priority = GetYamlInt(yamlSource, "Priority") ?? 0,
        };

        return true;
    }

    private static bool TryMapSourceKind(string type, out SourceKind kind)
    {
        if (string.Equals(type, "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
        {
            kind = SourceKind.PreIndexed;
            return true;
        }

        if (string.Equals(type, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase))
        {
            kind = SourceKind.Rest;
            return true;
        }

        kind = default;
        return false;
    }

    private static string ToPackagedSourceType(SourceKind kind) => kind switch
    {
        SourceKind.PreIndexed => "Microsoft.PreIndexed.Package",
        SourceKind.Rest => "Microsoft.Rest",
        _ => throw new InvalidOperationException($"Unsupported source kind: {kind}"),
    };

    private static int ToPackagedTrustLevel(string trustLevel) =>
        string.Equals(trustLevel, "Trusted", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static string MapYamlTrustLevel(Dictionary<string, string?> yamlSource)
    {
        var raw = GetYamlValue(yamlSource, "TrustLevel");
        return raw switch
        {
            string trust when int.TryParse(trust, out var numericTrustLevel) && numericTrustLevel > 0 => "Trusted",
            string trust when trust.Equals("Trusted", StringComparison.OrdinalIgnoreCase) => "Trusted",
            _ => "None",
        };
    }

    private static string? GetYamlValue(Dictionary<string, string?> yamlSource, string key) =>
        yamlSource.TryGetValue(key, out var value) ? value : null;

    private static string? GetYamlScalar(Dictionary<string, string?> yamlSource, string key) =>
        GetYamlValue(yamlSource, key);

    private static bool GetYamlBool(Dictionary<string, string?> yamlSource, string key)
    {
        var value = GetYamlValue(yamlSource, key);
        return value switch
        {
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };
    }

    private static int? GetYamlInt(Dictionary<string, string?> yamlSource, string key)
    {
        var value = GetYamlValue(yamlSource, key);
        return value switch
        {
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryGetYamlDateTime(Dictionary<string, string?> yamlSource, string key, out DateTime value)
    {
        var raw = GetYamlValue(yamlSource, key);
        switch (raw)
        {
            case string text when long.TryParse(text, out var parsedUnixSeconds):
                value = DateTimeOffset.FromUnixTimeSeconds(parsedUnixSeconds).UtcDateTime;
                return true;
            case string text when DateTime.TryParse(text, out var parsedDateTime):
                value = parsedDateTime.ToUniversalTime();
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static void AddYamlKeyValue(Dictionary<string, string?> yamlSource, string line)
    {
        var separatorIndex = line.IndexOf(':');
        if (separatorIndex <= 0)
            return;

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        yamlSource[key] = UnquoteYamlScalar(value);
    }

    private static string? UnquoteYamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string SanitizePathSegment(string value) => string.Concat(value.Select(c =>
        @"\/:*?""<>|".Contains(c) ? '_' : c));
}
