using System.Text.Json.Nodes;
using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class YamlEmitterTests
{
    [Fact]
    public void EmitDocument_Null_ProducesEmptyDocument()
    {
        // Null root maps to an empty document — no scalar value, no
        // mapping, no sequence. The emitter still produces the document
        // markers and a trailing newline.
        var yaml = YamlEmitter.EmitDocument(null);
        Assert.DoesNotContain("''", yaml);
        Assert.DoesNotContain("null", yaml);
    }

    [Fact]
    public void EmitDocument_PlainString_RoundsThroughCleanly()
    {
        // A regular alphabetic string emits as a plain scalar (no quotes).
        // Matches YamlDotNet's high-level Serialize(string) behavior.
        var yaml = YamlEmitter.EmitDocument("winget");
        Assert.Contains("winget", yaml);
        Assert.DoesNotContain("'winget'", yaml);
    }

    [Fact]
    public void EmitDocument_Dictionary_ProducesBlockMapping()
    {
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["Name"] = "winget",
            ["Priority"] = 0,
            ["Explicit"] = false,
        });

        Assert.Contains("Name: winget", yaml);
        Assert.Contains("Priority: 0", yaml);
        Assert.Contains("Explicit: false", yaml);
    }

    [Fact]
    public void EmitDocument_NestedListInDict_ProducesBlockSequence()
    {
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["Sources"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = "a",
                    ["Priority"] = 1,
                },
                new Dictionary<string, object?>
                {
                    ["Name"] = "b",
                    ["Priority"] = 2,
                },
            },
        });

        Assert.Contains("Sources:", yaml);
        Assert.Contains("- Name: a", yaml);
        Assert.Contains("- Name: b", yaml);
    }

    [Fact]
    public void EmitDocument_EmptyList_ProducesFlowEmpty()
    {
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["Warnings"] = new List<object?>(),
        });

        Assert.Contains("Warnings: []", yaml);
    }

    [Fact]
    public void EmitDocument_NullValueInDict_ProducesEmptyAfterColon()
    {
        // Reproduce YamlDotNet's high-level behavior: null mapping value
        // becomes "key: " with no quotes, not "key: ''" — downstream
        // parsers treat empty unquoted as null but '' as empty string.
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["LastUpdate"] = (object?)null,
            ["Name"] = "winget",
        }).ReplaceLineEndings("\n");

        Assert.Contains("LastUpdate: \n", yaml);
        Assert.DoesNotContain("LastUpdate: ''", yaml);
        Assert.Contains("Name: winget", yaml);
    }

    [Fact]
    public void EmitDocument_JsonObject_FollowsPropertyOrder()
    {
        var node = new JsonObject
        {
            ["First"] = "1",
            ["Second"] = "2",
            ["Third"] = "3",
        };

        var yaml = YamlEmitter.EmitDocument(node);
        var firstIdx = yaml.IndexOf("First");
        var secondIdx = yaml.IndexOf("Second");
        var thirdIdx = yaml.IndexOf("Third");

        Assert.True(firstIdx >= 0 && secondIdx > firstIdx && thirdIdx > secondIdx,
            "Properties should be emitted in declaration order");
    }

    [Fact]
    public void EmitDocument_JsonObjectNullProperty_ProducesEmptyAfterColon()
    {
        var node = new JsonObject
        {
            ["Channel"] = null,
            ["Name"] = "x",
        };

        var yaml = YamlEmitter.EmitDocument(node).ReplaceLineEndings("\n");

        Assert.Contains("Channel: \n", yaml);
        Assert.DoesNotContain("Channel: ''", yaml);
        Assert.DoesNotContain("Channel: null", yaml);
    }

    [Fact]
    public void EmitDocument_JsonArray_ProducesBlockSequence()
    {
        var node = JsonNode.Parse("""[ "a", 1, true, null ]""")!;
        var yaml = YamlEmitter.EmitDocument(node);

        Assert.Contains("- a", yaml);
        Assert.Contains("- 1", yaml);
        Assert.Contains("- true", yaml);
    }

    [Fact]
    public void EmitDocument_DateTime_UsesRoundTripIsoFormat()
    {
        var dt = new DateTime(2026, 5, 26, 12, 34, 56, DateTimeKind.Utc);
        var yaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["When"] = dt,
        });

        Assert.Contains("When: 2026-05-26T12:34:56", yaml);
    }
}
