using System.Text.Json;
using Devolutions.Pinget.Core;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class Output
{
    internal static OutputFormat GetFormat(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "yaml" => OutputFormat.Yaml,
            _ => OutputFormat.Text,
        };

    internal static void WriteStructuredOutput(object value, OutputFormat output)
    {
        switch (output)
        {
            case OutputFormat.Json:
                if (value is SerializableShowManifest showManifest)
                    Console.WriteLine(JsonSerializer.Serialize(showManifest, PingetJsonContext.Default.SerializableShowManifest));
                else
                    Console.WriteLine(JsonSerializer.Serialize(value, Json.Options));
                break;
            case OutputFormat.Yaml:
                Console.Write(new SerializerBuilder().Build().Serialize(value));
                break;
            default:
                throw new InvalidOperationException("Text output should be handled separately.");
        }
    }

    internal static void WriteManifestStructuredOutput(object value, OutputFormat output)
    {
        if (output == OutputFormat.Yaml && value is List<Dictionary<string, object?>> documents)
        {
            var serializer = new SerializerBuilder().Build();
            foreach (var document in documents)
            {
                Console.Write("---\n");
                Console.Write(serializer.Serialize(document));
            }
            return;
        }

        WriteStructuredOutput(value, output);
    }
}
