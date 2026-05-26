using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Cli;
using Xunit;
using YamlDotNet.Serialization;

namespace Devolutions.Pinget.Core.Tests;

public class StructuredOutputSerializerTests
{
    [Fact]
    public void SerializeJson_NullNode_ReturnsNullLiteral()
    {
        Assert.Equal("null", StructuredOutputSerializer.SerializeJson((JsonNode?)null));
    }

    [Fact]
    public void SerializeJson_Node_UsesIndentedFormatting()
    {
        var node = new JsonObject
        {
            ["Name"] = "winget",
            ["Priority"] = 0,
        };

        var json = StructuredOutputSerializer.SerializeJson(node);

        Assert.Contains("\"Name\"", json);
        Assert.Contains('\n', json);
    }

    [Fact]
    public void DynamicToJsonNode_Null_ReturnsNull()
    {
        Assert.Null(StructuredOutputSerializer.DynamicToJsonNode(null));
    }

    [Theory]
    [InlineData("hello", JsonValueKind.String)]
    [InlineData(true, JsonValueKind.True)]
    [InlineData(false, JsonValueKind.False)]
    [InlineData(42, JsonValueKind.Number)]
    [InlineData(9999999999L, JsonValueKind.Number)]
    [InlineData(3.14, JsonValueKind.Number)]
    public void DynamicToJsonNode_Primitives_ProduceMatchingJsonValueKind(object value, JsonValueKind expectedKind)
    {
        var node = StructuredOutputSerializer.DynamicToJsonNode(value);
        Assert.NotNull(node);
        Assert.Equal(expectedKind, node.GetValueKind());
    }

    [Fact]
    public void DynamicToJsonNode_Dictionary_ProducesJsonObject()
    {
        var dict = new Dictionary<string, object?>
        {
            ["Name"] = "PowerToys",
            ["Version"] = "0.99.0",
            ["Count"] = 3,
        };

        var node = StructuredOutputSerializer.DynamicToJsonNode(dict);

        var obj = Assert.IsType<JsonObject>(node);
        Assert.Equal("PowerToys", (string?)obj["Name"]);
        Assert.Equal("0.99.0", (string?)obj["Version"]);
        Assert.Equal(3, (int?)obj["Count"]);
    }

    [Fact]
    public void DynamicToJsonNode_List_ProducesJsonArray()
    {
        var list = new List<object?> { "one", 2, null, true };

        var node = StructuredOutputSerializer.DynamicToJsonNode(list);

        var array = Assert.IsType<JsonArray>(node);
        Assert.Equal(4, array.Count);
        Assert.Equal("one", (string?)array[0]);
        Assert.Equal(2, (int?)array[1]);
        Assert.Null(array[2]);
        Assert.True((bool?)array[3]);
    }

    [Fact]
    public void DynamicToJsonNode_NestedStructure_ProducesMatchingJsonTree()
    {
        var dict = new Dictionary<string, object?>
        {
            ["PackageIdentifier"] = "Microsoft.PowerToys",
            ["Installers"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["InstallerType"] = "exe",
                    ["InstallerSha256"] = "ABC",
                }
            },
        };

        var node = StructuredOutputSerializer.DynamicToJsonNode(dict);
        var json = node!.ToJsonString();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Microsoft.PowerToys", doc.RootElement.GetProperty("PackageIdentifier").GetString());
        var installer = doc.RootElement.GetProperty("Installers")[0];
        Assert.Equal("exe", installer.GetProperty("InstallerType").GetString());
        Assert.Equal("ABC", installer.GetProperty("InstallerSha256").GetString());
    }

    [Fact]
    public void DynamicToJsonNode_ExistingJsonNode_IsDeepCloned()
    {
        var source = new JsonObject { ["Name"] = "winget" };

        var cloned = StructuredOutputSerializer.DynamicToJsonNode(source);

        Assert.NotSame(source, cloned);
        Assert.Equal("winget", (string?)cloned!["Name"]);

        source["Name"] = "changed";
        Assert.Equal("winget", (string?)cloned["Name"]);
    }

    [Fact]
    public void JsonNodeToDynamic_Null_ReturnsNull()
    {
        Assert.Null(StructuredOutputSerializer.JsonNodeToDynamic(null));
    }

    [Fact]
    public void JsonNodeToDynamic_JsonObject_ProducesStringDictionary()
    {
        var node = new JsonObject
        {
            ["Name"] = "winget",
            ["Priority"] = 7,
            ["Explicit"] = false,
            ["LastUpdate"] = (JsonNode?)null,
        };

        var result = StructuredOutputSerializer.JsonNodeToDynamic(node);

        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("winget", dict["Name"]);
        Assert.Equal(7L, dict["Priority"]);
        Assert.Equal(false, dict["Explicit"]);
        Assert.Null(dict["LastUpdate"]);
    }

    [Fact]
    public void JsonNodeToDynamic_JsonArray_ProducesObjectList()
    {
        var node = new JsonArray("a", 1, true);

        var result = StructuredOutputSerializer.JsonNodeToDynamic(node);

        var list = Assert.IsType<List<object?>>(result);
        Assert.Equal(new object?[] { "a", 1L, true }, list);
    }

    [Fact]
    public void JsonNodeToDynamic_Number_PrefersInt64WhenIntegral()
    {
        var node = JsonNode.Parse("42")!;
        Assert.Equal(42L, StructuredOutputSerializer.JsonNodeToDynamic(node));
    }

    [Fact]
    public void JsonNodeToDynamic_Number_FallsBackToDoubleWhenFractional()
    {
        var node = JsonNode.Parse("3.14")!;
        Assert.Equal(3.14, (double)StructuredOutputSerializer.JsonNodeToDynamic(node)!, precision: 10);
    }

    [Fact]
    public void JsonNodeToDynamic_OutputIsAcceptedByYamlDotNet()
    {
        // Regression: the settings YAML branch (WriteJsonNode in Program.cs)
        // feeds the dynamic tree directly into YamlDotNet's serializer. Make
        // sure the shape we produce is one YamlDotNet can render without
        // throwing or emitting opaque CLR type names.
        var input = new JsonObject
        {
            ["telemetry"] = new JsonObject { ["disable"] = true },
            ["network"] = new JsonObject
            {
                ["downloader"] = "wininet",
                ["doProgressTimeoutInSeconds"] = 60,
            },
            ["tags"] = new JsonArray("a", "b"),
        };

        var dynamic = StructuredOutputSerializer.JsonNodeToDynamic(input);
        var yaml = new SerializerBuilder().Build().Serialize(dynamic!);

        Assert.Contains("telemetry:", yaml);
        Assert.Contains("disable: true", yaml);
        Assert.Contains("doProgressTimeoutInSeconds: 60", yaml);
        Assert.Contains("- a", yaml);
        Assert.DoesNotContain("JsonElement", yaml);
        Assert.DoesNotContain("System.", yaml);
    }
}
