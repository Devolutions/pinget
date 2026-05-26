using Devolutions.Pinget.Core;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class YamlRoundtripTests
{
    [Fact]
    public void SavePackagedStore_OutputIsLoadableByParsePackagedSourceStore()
    {
        // Regression: SavePackagedStore writes the sources state via
        // YamlEmitter; ParsePackagedSourceStore reads it back through a
        // hand-rolled line parser. Make sure the format the emitter
        // produces is one the reader still recognises end-to-end.
        var sourceDocumentYaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["Sources"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = "winget",
                    ["Type"] = "Microsoft.PreIndexed.Package",
                    ["Arg"] = "https://cdn.winget.microsoft.com/cache",
                    ["Data"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    ["TrustLevel"] = "Trusted",
                    ["Explicit"] = false,
                    ["Priority"] = 0,
                    ["IsTombstone"] = false,
                },
            },
        });

        var metadataDocumentYaml = YamlEmitter.EmitDocument(new Dictionary<string, object?>
        {
            ["Sources"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = "winget",
                    ["LastUpdate"] = 1700000000L,
                    ["SourceVersion"] = "v2.0",
                },
            },
        });

        var store = SourceStoreManager.ParsePackagedSourceStore(sourceDocumentYaml, metadataDocumentYaml);

        Assert.NotNull(store);
        // ParsePackagedSourceStore merges the YAML entries on top of the
        // hardcoded defaults, so we look up the specific source we wrote
        // rather than asserting the total source count.
        var source = Assert.Single(store!.Sources, s => string.Equals(s.Name, "winget", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SourceKind.PreIndexed, source.Kind);
        Assert.Equal("https://cdn.winget.microsoft.com/cache", source.Arg);
        Assert.Equal("Microsoft.Winget.Source_8wekyb3d8bbwe", source.Identifier);
        Assert.Equal("Trusted", source.TrustLevel);
        Assert.False(source.Explicit);
        Assert.Equal(0, source.Priority);
        Assert.Equal("v2.0", source.SourceVersion);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000L).UtcDateTime, source.LastUpdate);
    }

    [Fact]
    public void ParseV2VersionDataDocument_ParsesAliasedKeys()
    {
        // The mszyml schema uses short aliases (v/rP/s256H/aMiV/aMaV).
        // The new hand-rolled parser must honour those aliases to match
        // what the old YamlDotNet DeserializerBuilder produced.
        var yaml = """
vD:
  - v: 1.0.0
    rP: manifests/m/microsoft/powertoys/1.0.0
    s256H: abc123
    aMiV: 1.0.0
    aMaV: 1.0.0
  - v: 2.0.0
    rP: manifests/m/microsoft/powertoys/2.0.0
    s256H: def456
""";

        var doc = PreIndexedSource.ParseV2VersionDataDocument(yaml);

        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Versions.Count);

        Assert.Equal("1.0.0", doc.Versions[0].Version);
        Assert.Equal("manifests/m/microsoft/powertoys/1.0.0", doc.Versions[0].ManifestRelativePath);
        Assert.Equal("abc123", doc.Versions[0].ManifestHash);
        Assert.Equal("1.0.0", doc.Versions[0].ArpMinVersion);
        Assert.Equal("1.0.0", doc.Versions[0].ArpMaxVersion);

        Assert.Equal("2.0.0", doc.Versions[1].Version);
        Assert.Equal("def456", doc.Versions[1].ManifestHash);
        Assert.Null(doc.Versions[1].ArpMinVersion);
        Assert.Null(doc.Versions[1].ArpMaxVersion);
    }

    [Fact]
    public void ParseV2VersionDataDocument_IgnoresUnknownKeys()
    {
        // Reproduces the .IgnoreUnmatchedProperties() behaviour of the
        // previous DeserializerBuilder configuration.
        var yaml = """
vD:
  - v: 1.0.0
    rP: x
    s256H: y
    unknownField: should-be-ignored
  - v: 2.0.0
    rP: z
    s256H: w
extraTopLevel:
  ignoreMe: true
""";

        var doc = PreIndexedSource.ParseV2VersionDataDocument(yaml);

        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Versions.Count);
        Assert.Equal("1.0.0", doc.Versions[0].Version);
        Assert.Equal("2.0.0", doc.Versions[1].Version);
    }

    [Fact]
    public void ParseV2VersionDataDocument_EmptyDocument_ReturnsEmptyVersions()
    {
        var yaml = "vD: []\n";

        var doc = PreIndexedSource.ParseV2VersionDataDocument(yaml);

        Assert.NotNull(doc);
        Assert.Empty(doc!.Versions);
    }
}
