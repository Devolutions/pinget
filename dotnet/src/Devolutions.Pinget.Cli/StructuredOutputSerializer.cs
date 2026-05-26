using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Devolutions.Pinget.Core;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Cli;

public static class StructuredOutputSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public static string SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    public static string SerializeJson(JsonNode? node) =>
        node?.ToJsonString(JsonOptions) ?? "null";

    public static string SerializeYaml(object value) =>
        new SerializerBuilder().Build().Serialize(value);

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

    internal static object? JsonNodeToDynamic(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                {
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (key, child) in obj)
                        dict[key] = JsonNodeToDynamic(child);
                    return dict;
                }
            case JsonArray array:
                {
                    var list = new List<object?>(array.Count);
                    foreach (var child in array)
                        list.Add(JsonNodeToDynamic(child));
                    return list;
                }
            case JsonValue jsonValue:
                return ConvertJsonValue(jsonValue);
            default:
                return node.ToJsonString();
        }
    }

    private static object? ConvertJsonValue(JsonValue jsonValue)
    {
        // JsonValue can be backed either by a parsed JsonElement
        // (JsonNode.Parse) or by a strongly-typed primitive built via
        // JsonValue.Create<T>. The JsonElement form supports cross-type
        // GetValue calls; the primitive form requires an exact T match.
        switch (jsonValue.GetValueKind())
        {
            case JsonValueKind.String:
                return jsonValue.GetValue<string>();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                if (jsonValue.TryGetValue<long>(out var lv)) return lv;
                if (jsonValue.TryGetValue<int>(out var iv)) return (long)iv;
                if (jsonValue.TryGetValue<short>(out var sv)) return (long)sv;
                if (jsonValue.TryGetValue<byte>(out var bv)) return (long)bv;
                if (jsonValue.TryGetValue<uint>(out var uiv)) return (long)uiv;
                if (jsonValue.TryGetValue<double>(out var dv)) return dv;
                if (jsonValue.TryGetValue<float>(out var fv)) return (double)fv;
                if (jsonValue.TryGetValue<decimal>(out var mv)) return (double)mv;
                if (jsonValue.TryGetValue<JsonElement>(out var elem))
                {
                    if (elem.TryGetInt64(out var el)) return el;
                    return elem.GetDouble();
                }
                return jsonValue.ToJsonString();
            default:
                return jsonValue.ToJsonString();
        }
    }
}
