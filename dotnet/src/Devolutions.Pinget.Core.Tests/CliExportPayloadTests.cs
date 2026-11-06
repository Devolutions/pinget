using System.Text.Json;
using Devolutions.Pinget.Cli;
using Xunit;

namespace Devolutions.Pinget.Core.Tests;

public class CliExportPayloadTests
{
    [Fact]
    public void PackagesExportPayload_OmitsVersionWhenNull()
    {
        // Regression: the legacy code conditionally inserted the "Version" key
        // into a Dictionary<string,object>. The new typed record relies on
        // [JsonIgnore(WhenWritingNull)] to reproduce that wire-format shape;
        // make sure the field is genuinely absent (not serialized as null).
        var payload = new PackagesExportPayload(
            Schema: "https://aka.ms/winget-packages.schema.2.0.json",
            Sources:
            [
                new PackageExportSource(
                    SourceDetails: new PackageExportSourceDetails(
                        Name: "winget",
                        Argument: "https://cdn.winget.microsoft.com/cache",
                        Type: "Microsoft.PreIndexed"),
                    Packages:
                    [
                        new PackageExportEntry(PackageIdentifier: "Microsoft.PowerToys", Version: null)
                    ])
            ]);

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(payload, CliJsonContext.Default.PackagesExportPayload));
        var package = document.RootElement.GetProperty("Sources")[0].GetProperty("Packages")[0];

        Assert.Equal("Microsoft.PowerToys", package.GetProperty("PackageIdentifier").GetString());
        Assert.False(package.TryGetProperty("Version", out _));
    }

    [Fact]
    public void PackagesExportPayload_IncludesVersionWhenPresent()
    {
        var payload = new PackagesExportPayload(
            Schema: "https://aka.ms/winget-packages.schema.2.0.json",
            Sources:
            [
                new PackageExportSource(
                    SourceDetails: new PackageExportSourceDetails(
                        Name: "winget",
                        Argument: "https://cdn.winget.microsoft.com/cache",
                        Type: "Microsoft.PreIndexed"),
                    Packages:
                    [
                        new PackageExportEntry(PackageIdentifier: "Microsoft.PowerToys", Version: "0.99.0")
                    ])
            ]);

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(payload, CliJsonContext.Default.PackagesExportPayload));
        var package = document.RootElement.GetProperty("Sources")[0].GetProperty("Packages")[0];

        Assert.Equal("0.99.0", package.GetProperty("Version").GetString());
    }

    [Fact]
    public void PackagesExportPayload_MatchesWingetSchemaShape()
    {
        // The export command produces files consumed by `winget import`. Lock
        // down the top-level shape so a future refactor doesn't accidentally
        // rename fields (e.g. SourceDetails → sourceDetails, or Argument → Arg).
        var payload = new PackagesExportPayload(
            Schema: "https://aka.ms/winget-packages.schema.2.0.json",
            Sources:
            [
                new PackageExportSource(
                    SourceDetails: new PackageExportSourceDetails(
                        Name: "winget",
                        Argument: "https://cdn.winget.microsoft.com/cache",
                        Type: "Microsoft.PreIndexed"),
                    Packages: [])
            ]);

        using var document = JsonDocument.Parse(StructuredOutputSerializer.SerializeJson(payload, CliJsonContext.Default.PackagesExportPayload));
        var root = document.RootElement;

        Assert.Equal("https://aka.ms/winget-packages.schema.2.0.json", root.GetProperty("Schema").GetString());
        var source = root.GetProperty("Sources")[0];
        var details = source.GetProperty("SourceDetails");
        Assert.Equal("winget", details.GetProperty("Name").GetString());
        Assert.Equal("https://cdn.winget.microsoft.com/cache", details.GetProperty("Argument").GetString());
        Assert.Equal("Microsoft.PreIndexed", details.GetProperty("Type").GetString());
        Assert.False(details.TryGetProperty("Arg", out _));
        Assert.True(source.GetProperty("Packages").ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public void SourceExportPayload_OmitsInternalAnonymousArtifacts()
    {
        // Anonymous-type serialization in trim/AOT mode can leak compiler-
        // generated names. Confirm the explicit record produces nothing of
        // the kind.
        var payload = new SourceExportPayload(
        [
            new SourceExportEntry(
                Name: "winget",
                Type: "Microsoft.PreIndexed.Package",
                Arg: "https://cdn.winget.microsoft.com/cache",
                Data: "Microsoft.Winget.Source_8wekyb3d8bbwe",
                Identifier: "Microsoft.Winget.Source_8wekyb3d8bbwe",
                TrustLevel: "Trusted",
                Explicit: false,
                Priority: 0)
        ]);

        var json = StructuredOutputSerializer.SerializeJson(payload, CliJsonContext.Default.SourceExportPayload);

        Assert.DoesNotContain("<>", json);
        Assert.DoesNotContain("AnonymousType", json);
        Assert.DoesNotContain("k__BackingField", json);
    }

    private static ListMatch Installed(string id, string? source) => new()
    {
        Name = id.Split('.')[^1],
        Id = id,
        LocalId = id,
        InstalledVersion = "1.0",
        SourceName = source,
    };

    private static readonly SourceRecord[] Sources =
    [
        new() { Name = "winget", Kind = SourceKind.PreIndexed, Arg = "https://cdn.winget.microsoft.com/cache", Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe" },
        new() { Name = "contoso", Kind = SourceKind.Rest, Arg = "https://contoso.test/api", Identifier = "contoso" },
    ];

    [Fact]
    public void ExportPayloadBuilder_SourceFilterKeepsPackagesWithoutSpaces()
    {
        var payload = ExportPayloadBuilder.Build(
            [Installed("Git.Git", "winget"), Installed("Contoso.Tool", "contoso")], Sources, "winget", includeVersions: false);

        var source = Assert.Single(payload.Sources);
        Assert.Equal("winget", source.SourceDetails.Name);
        Assert.Equal("Git.Git", Assert.Single(source.Packages).PackageIdentifier);
    }

    [Fact]
    public void ExportPayloadBuilder_GroupsBySourceAndSkipsUnsourcedPackages()
    {
        var payload = ExportPayloadBuilder.Build(
            [Installed("Git.Git", "winget"), Installed("Contoso.Tool", "contoso"), Installed("Local.Only", null)],
            Sources,
            sourceFilter: null,
            includeVersions: true);

        Assert.Equal(["winget", "contoso"], payload.Sources.Select(s => s.SourceDetails.Name).ToArray());
        var contoso = payload.Sources[1];
        Assert.Equal("Microsoft.Rest", contoso.SourceDetails.Type);
        Assert.Equal("https://contoso.test/api", contoso.SourceDetails.Argument);
        Assert.Equal("1.0", Assert.Single(contoso.Packages).Version);
        Assert.DoesNotContain(payload.Sources.SelectMany(s => s.Packages), p => p.PackageIdentifier == "Local.Only");
    }
}
