using System.Text.Json;
using System.Text.Json.Serialization;

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
    public static string NormalizeAppRoot(string? appRoot)
    {
        if (!string.IsNullOrWhiteSpace(appRoot))
            return Path.GetFullPath(appRoot);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "pinget");
    }

    public static void EnsureAppDirs(string? appRoot = null)
    {
        var root = NormalizeAppRoot(appRoot);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
    }

    public static string SourceStateDir(SourceRecord source, string? appRoot = null)
    {
        var safeName = string.Concat(source.Name.Select(c =>
            @"\/:*?""<>|".Contains(c) ? '_' : c));
        return Path.Combine(NormalizeAppRoot(appRoot), "sources", safeName);
    }

    public static string PinsDbPath(string? appRoot = null) => Path.Combine(NormalizeAppRoot(appRoot), "pins.db");

    public static SourceStore Load(string? appRoot = null)
    {
        var path = Path.Combine(NormalizeAppRoot(appRoot), "sources.json");
        if (!File.Exists(path))
            return SourceStore.Default();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, SourceStoreContext.Default.SourceStore) ?? SourceStore.Default();
    }

    public static void Save(SourceStore store, string? appRoot = null)
    {
        var path = Path.Combine(NormalizeAppRoot(appRoot), "sources.json");
        var json = JsonSerializer.Serialize(store, SourceStoreContext.Default.SourceStore);
        File.WriteAllText(path, json);
    }
}
