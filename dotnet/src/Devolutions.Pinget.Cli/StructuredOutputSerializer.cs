using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

public static class StructuredOutputSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public static string SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    public static string SerializeJson(JsonNode? node) =>
        node?.ToJsonString(JsonOptions) ?? "null";

    public static string SerializeYaml(object? value) =>
        YamlEmitter.EmitDocument(value);

    public static string SerializeYaml<T>(T value, JsonTypeInfo<T> typeInfo) =>
        YamlEmitter.EmitDocument(JsonSerializer.SerializeToNode(value, typeInfo));

    internal static JsonNode? DynamicToJsonNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.DeepClone();
            case string s:
                return JsonValue.Create(s);
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case double d:
                return JsonValue.Create(d);
            case decimal m:
                return JsonValue.Create(m);
            case DateTime dt:
                return JsonValue.Create(dt);
            case DateTimeOffset dto:
                return JsonValue.Create(dto);
            case Guid g:
                return JsonValue.Create(g);
            case IDictionary<string, object?> dict:
                {
                    var obj = new JsonObject();
                    foreach (var (key, val) in dict)
                        obj[key] = DynamicToJsonNode(val);
                    return obj;
                }
            case IEnumerable enumerable:
                {
                    var array = new JsonArray();
                    foreach (var item in enumerable)
                        array.Add(DynamicToJsonNode(item));
                    return array;
                }
            default:
                return JsonValue.Create(value.ToString());
        }
    }

}
