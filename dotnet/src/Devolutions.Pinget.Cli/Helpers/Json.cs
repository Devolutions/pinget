using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class Json
{
    internal static JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static void WriteNode(JsonNode value, OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Yaml:
                var structured = JsonSerializer.Deserialize<object>(value.ToJsonString()) ?? new object();
                Console.Write(new SerializerBuilder().Build().Serialize(structured));
                break;
            default:
                Console.WriteLine(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                break;
        }
    }

    internal static string? GetString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
}
