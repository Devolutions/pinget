using System.Text.Json;
using Devolutions.Pinget.Core;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Cli;

public static class StructuredOutputSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    public static string SerializeJson(object value) =>
        value is SerializableShowManifest showManifest
            ? JsonSerializer.Serialize(showManifest, PingetJsonContext.Default.SerializableShowManifest)
            : JsonSerializer.Serialize(value, JsonOptions);

    public static string SerializeYaml(object value) =>
        new SerializerBuilder().Build().Serialize(value);
}