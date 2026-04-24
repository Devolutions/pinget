using System.Collections;
using System.Text.Json.Nodes;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

internal static class HashtableConverter
{
    public static Hashtable ToHashtable(JsonObject value)
    {
        var result = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value)
        {
            result[entry.Key] = ToPowerShellValue(entry.Value);
        }

        return result;
    }

    public static JsonObject ToJsonObject(Hashtable value)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in value)
        {
            if (entry.Key is null)
                continue;

            result[entry.Key.ToString()!] = ToJsonNode(entry.Value);
        }

        return result;
    }

    private static object? ToPowerShellValue(JsonNode? value) => value switch
    {
        null => null,
        JsonObject obj => ToHashtable(obj),
        JsonArray array => new ArrayList(array.Select(ToPowerShellValue).ToList()),
        JsonValue scalar => scalar.TryGetValue<bool>(out var boolValue) ? boolValue
            : scalar.TryGetValue<long>(out var longValue) ? longValue
            : scalar.TryGetValue<double>(out var doubleValue) ? doubleValue
            : scalar.TryGetValue<string>(out var stringValue) ? stringValue
            : scalar.ToJsonString(),
        _ => value.ToJsonString(),
    };

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        Hashtable table => ToJsonObject(table),
        IDictionary dictionary => ToJsonObject(ToHashtable(dictionary)),
        ArrayList list => new JsonArray(list.Cast<object?>().Select(ToJsonNode).ToArray()),
        IEnumerable enumerable when value is not string => new JsonArray(enumerable.Cast<object?>().Select(ToJsonNode).ToArray()),
        bool boolValue => JsonValue.Create(boolValue),
        byte byteValue => JsonValue.Create(byteValue),
        short shortValue => JsonValue.Create(shortValue),
        int intValue => JsonValue.Create(intValue),
        long longValue => JsonValue.Create(longValue),
        float floatValue => JsonValue.Create(floatValue),
        double doubleValue => JsonValue.Create(doubleValue),
        decimal decimalValue => JsonValue.Create(decimalValue),
        string stringValue => JsonValue.Create(stringValue),
        _ => JsonValue.Create(value.ToString()),
    };

    private static Hashtable ToHashtable(IDictionary value)
    {
        var result = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in value)
        {
            if (entry.Key is null)
                continue;

            result[entry.Key.ToString()!] = entry.Value;
        }

        return result;
    }
}
