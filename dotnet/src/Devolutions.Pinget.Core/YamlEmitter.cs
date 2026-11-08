using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Devolutions.Pinget.Core;

// Hand-rolled YAML emitter built on YamlDotNet's low-level event API. The
// high-level SerializerBuilder is reflection-based and trips IL3050 under
// NativeAOT; the Emitter is event-driven and AOT-safe. Inputs accepted:
// JsonNode, IDictionary<string,object?>, IEnumerable, strings, booleans,
// numeric primitives, and DateTime/DateTimeOffset/Guid. Unknown values are
// rendered via ToString() — matches what the reflection-based serializer
// would do for opaque types.
public static class YamlEmitter
{
    public static string EmitDocument(object? value)
    {
        using var writer = new StringWriter();
        EmitDocument(value, writer);
        return writer.ToString();
    }

    public static void EmitDocument(object? value, TextWriter writer)
    {
        var emitter = new Emitter(writer);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        EmitNode(emitter, value);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
    }

    private static void EmitNode(IEmitter emitter, object? value)
    {
        switch (value)
        {
            case null:
                EmitNull(emitter);
                return;

            case JsonNode node:
                EmitJsonNode(emitter, node);
                return;

            case string s:
                EmitScalar(emitter, s);
                return;

            case bool b:
                EmitPlainScalar(emitter, b ? "true" : "false");
                return;

            case sbyte or byte or short or ushort or int or uint or long or ulong:
                EmitPlainScalar(emitter, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                return;

            case float f:
                EmitPlainScalar(emitter, f.ToString("R", CultureInfo.InvariantCulture));
                return;

            case double d:
                EmitPlainScalar(emitter, d.ToString("R", CultureInfo.InvariantCulture));
                return;

            case decimal m:
                EmitPlainScalar(emitter, m.ToString(CultureInfo.InvariantCulture));
                return;

            case DateTime dt:
                EmitScalar(emitter, dt.ToString("o", CultureInfo.InvariantCulture));
                return;

            case DateTimeOffset dto:
                EmitScalar(emitter, dto.ToString("o", CultureInfo.InvariantCulture));
                return;

            case Guid g:
                EmitScalar(emitter, g.ToString("D", CultureInfo.InvariantCulture));
                return;

            case IDictionary<string, object?> stringDict:
                EmitMapping(emitter, stringDict);
                return;

            case IDictionary dict:
                EmitMapping(emitter, dict);
                return;

            case IEnumerable enumerable:
                EmitSequence(emitter, enumerable);
                return;

            default:
                EmitScalar(emitter, value.ToString() ?? "");
                return;
        }
    }

    private static void EmitJsonNode(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case null:
                EmitNull(emitter);
                return;

            case JsonObject obj:
                emitter.Emit(new MappingStart(anchor: null, tag: null, isImplicit: true, style: MappingStyle.Block));
                foreach (var (key, child) in obj)
                {
                    EmitScalar(emitter, key);
                    EmitJsonNode(emitter, child);
                }
                emitter.Emit(new MappingEnd());
                return;

            case JsonArray array:
                if (array.Count == 0)
                {
                    emitter.Emit(new SequenceStart(anchor: null, tag: null, isImplicit: true, style: SequenceStyle.Flow));
                    emitter.Emit(new SequenceEnd());
                    return;
                }
                emitter.Emit(new SequenceStart(anchor: null, tag: null, isImplicit: true, style: SequenceStyle.Block));
                foreach (var child in array)
                    EmitJsonNode(emitter, child);
                emitter.Emit(new SequenceEnd());
                return;

            case JsonValue jsonValue:
                EmitJsonScalar(emitter, jsonValue);
                return;

            default:
                EmitScalar(emitter, node.ToJsonString());
                return;
        }
    }

    private static void EmitJsonScalar(IEmitter emitter, JsonValue jsonValue)
    {
        switch (jsonValue.GetValueKind())
        {
            case JsonValueKind.String:
                EmitScalar(emitter, jsonValue.GetValue<string>());
                return;
            case JsonValueKind.True:
                EmitPlainScalar(emitter, "true");
                return;
            case JsonValueKind.False:
                EmitPlainScalar(emitter, "false");
                return;
            case JsonValueKind.Null:
                EmitNull(emitter);
                return;
            case JsonValueKind.Number:
                EmitPlainScalar(emitter, jsonValue.ToJsonString());
                return;
            default:
                EmitScalar(emitter, jsonValue.ToJsonString());
                return;
        }
    }

    private static void EmitMapping(IEmitter emitter, IDictionary<string, object?> dict)
    {
        emitter.Emit(new MappingStart(anchor: null, tag: null, isImplicit: true, style: MappingStyle.Block));
        foreach (var (key, value) in dict)
        {
            EmitScalar(emitter, key);
            EmitNode(emitter, value);
        }
        emitter.Emit(new MappingEnd());
    }

    private static void EmitMapping(IEmitter emitter, IDictionary dict)
    {
        emitter.Emit(new MappingStart(anchor: null, tag: null, isImplicit: true, style: MappingStyle.Block));
        foreach (DictionaryEntry entry in dict)
        {
            var keyString = entry.Key as string ?? entry.Key.ToString() ?? "";
            EmitScalar(emitter, keyString);
            EmitNode(emitter, entry.Value);
        }
        emitter.Emit(new MappingEnd());
    }

    private static void EmitSequence(IEmitter emitter, IEnumerable enumerable)
    {
        var items = new List<object?>();
        foreach (var item in enumerable)
            items.Add(item);

        if (items.Count == 0)
        {
            emitter.Emit(new SequenceStart(anchor: null, tag: null, isImplicit: true, style: SequenceStyle.Flow));
            emitter.Emit(new SequenceEnd());
            return;
        }

        emitter.Emit(new SequenceStart(anchor: null, tag: null, isImplicit: true, style: SequenceStyle.Block));
        foreach (var item in items)
            EmitNode(emitter, item);
        emitter.Emit(new SequenceEnd());
    }

    // Booleans and numbers: always plain, so they read back as their native type.
    private static void EmitPlainScalar(IEmitter emitter, string value)
    {
        emitter.Emit(new Scalar(
            anchor: null,
            tag: null,
            value: value,
            style: ScalarStyle.Any,
            isPlainImplicit: true,
            isQuotedImplicit: true));
    }

    // Strings: plain unless a YAML reader would resolve the plain scalar to something
    // other than a string ("1.10", "true", "null", "", ...). Clearing isPlainImplicit
    // makes the emitter quote it, which is what serde_yaml does on the Rust side.
    private static void EmitScalar(IEmitter emitter, string value)
    {
        emitter.Emit(new Scalar(
            anchor: null,
            tag: null,
            value: value,
            style: ScalarStyle.Any,
            isPlainImplicit: !ResolvesToNonString(value),
            isQuotedImplicit: true));
    }

    private static readonly HashSet<string> NonStringWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "~", "null", "true", "false", "yes", "no", "on", "off", "y", "n",
        ".inf", "+.inf", "-.inf", ".nan",
    };

    // YAML 1.1 and 1.2 core-schema ints/floats: decimal, 0x hex, 0o / leading-0 octal,
    // 0b binary, underscores, exponents, and 1.1 sexagesimal (1:30).
    private static readonly System.Text.RegularExpressions.Regex NumberPattern = new(
        @"^(?:[-+]?(?:0x[0-9a-f_]+|0o[0-7_]+|0b[01_]+|[0-9][0-9_]*(?::[0-5]?[0-9])+(?:\.[0-9_]*)?|(?:[0-9][0-9_]*)?\.?[0-9_]+(?:e[-+]?[0-9]+)?|[0-9][0-9_]*\.(?:e[-+]?[0-9]+)?))$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool ResolvesToNonString(string value) =>
        value.Length == 0 ||
        NonStringWords.Contains(value) ||
        NumberPattern.IsMatch(value);

    private static readonly TagName NullTag = new("tag:yaml.org,2002:null");

    private static void EmitNull(IEmitter emitter)
    {
        // Reproduce YamlDotNet's high-level "Channel: " output for a null
        // mapping value. Setting the null tag tells the emitter the empty
        // value is genuinely null, so it won't promote it to a quoted
        // empty string ('').
        emitter.Emit(new Scalar(
            anchor: default,
            tag: NullTag,
            value: string.Empty,
            style: ScalarStyle.Plain,
            isPlainImplicit: true,
            isQuotedImplicit: true));
    }
}
