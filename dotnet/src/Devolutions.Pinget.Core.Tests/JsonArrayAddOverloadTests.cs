using System.Text.Json.Nodes;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class JsonArrayAddOverloadTests
{
    [Fact]
    public void JsonArrayAdd_JsonNodeCast_ProducesIdenticalOutput_ToGenericAdd()
    {
        // RestSource.BuildSearchBody / AppendRequiredFilters previously called
        // filters.Add(new JsonObject {...}) which bound to JsonArray.Add<T>(T)
        // — a method annotated [RequiresDynamicCode]. We now cast the argument
        // to JsonNode? so overload resolution picks JsonArray.Add(JsonNode?).
        // Confirm the two paths emit byte-identical JSON.
        var withGenericAdd = new JsonArray();
        withGenericAdd.Add(new JsonObject
        {
            ["PackageMatchField"] = "PackageIdentifier",
            ["RequestMatch"] = new JsonObject
            {
                ["KeyWord"] = "Microsoft.PowerToys",
                ["MatchType"] = "Exact",
            },
        });

        var withCastedAdd = new JsonArray();
        withCastedAdd.Add((JsonNode?)new JsonObject
        {
            ["PackageMatchField"] = "PackageIdentifier",
            ["RequestMatch"] = new JsonObject
            {
                ["KeyWord"] = "Microsoft.PowerToys",
                ["MatchType"] = "Exact",
            },
        });

        Assert.Equal(withGenericAdd.ToJsonString(), withCastedAdd.ToJsonString());
    }
}
