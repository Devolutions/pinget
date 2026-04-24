using System.Text.Json;
using System.Text.Json.Nodes;

namespace Devolutions.Pinget.Core;

internal static class SettingsStoreManager
{
    public static readonly string[] SupportedAdminSettings =
    [
        "LocalManifestFiles",
        "BypassCertificatePinningForMicrosoftStore",
        "InstallerHashOverride",
        "LocalArchiveMalwareScanOverride",
        "ProxyCommandLineOptions",
    ];

    public static string UserSettingsPath(string? appRoot = null) =>
        Path.Combine(SourceStoreManager.NormalizeAppRoot(appRoot), "user-settings.json");

    public static string AdminSettingsPath(string? appRoot = null) =>
        Path.Combine(SourceStoreManager.NormalizeAppRoot(appRoot), "admin-settings.json");

    public static JsonObject LoadJsonObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var node = JsonNode.Parse(File.ReadAllText(path));
        return node as JsonObject ?? new JsonObject();
    }

    public static void SaveJsonObject(string path, JsonObject value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static JsonObject MergeJsonObjects(JsonObject current, JsonObject update)
    {
        var merged = current.DeepClone().AsObject();
        foreach (var entry in update)
        {
            if (entry.Value is JsonObject updateObject &&
                merged[entry.Key] is JsonObject currentObject)
            {
                merged[entry.Key] = MergeJsonObjects(currentObject, updateObject);
            }
            else
            {
                merged[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return merged;
    }

    public static bool JsonContains(JsonNode? current, JsonNode? expected)
    {
        if (current is null || expected is null)
            return current is null && expected is null;

        if (expected is JsonObject expectedObject)
        {
            var currentObject = current as JsonObject;
            if (currentObject is null)
                return false;

            foreach (var entry in expectedObject)
            {
                if (!currentObject.TryGetPropertyValue(entry.Key, out var currentChild) ||
                    !JsonContains(currentChild, entry.Value))
                {
                    return false;
                }
            }

            return true;
        }

        if (expected is JsonArray expectedArray)
        {
            var currentArray = current as JsonArray;
            if (currentArray is null || currentArray.Count != expectedArray.Count)
                return false;

            for (var i = 0; i < expectedArray.Count; i++)
            {
                if (!JsonContains(currentArray[i], expectedArray[i]))
                    return false;
            }

            return true;
        }

        return JsonNode.DeepEquals(current, expected);
    }

    public static string NormalizeAdminSettingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("An administrator setting name is required.");

        var normalized = SupportedAdminSettings
            .FirstOrDefault(setting => string.Equals(setting, name, StringComparison.OrdinalIgnoreCase));

        return normalized ?? throw new InvalidOperationException($"Unsupported admin setting: {name}");
    }
}
