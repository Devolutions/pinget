using System.Text.Json.Serialization;

namespace Devolutions.Pinget.Cli;

internal sealed record SourceExportEntry(
    string Name,
    string Type,
    string Arg,
    string Data,
    string Identifier,
    string TrustLevel,
    bool Explicit,
    int Priority);

internal sealed record SourceExportPayload(List<SourceExportEntry> Sources);

internal sealed record PackageExportEntry(
    string PackageIdentifier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version);

internal sealed record PackageExportSourceDetails(string Name, string Argument, string Type, string Identifier = "");

internal sealed record PackageExportSource(
    PackageExportSourceDetails SourceDetails,
    List<PackageExportEntry> Packages);

internal sealed record PackagesExportPayload(
    [property: JsonPropertyName("$schema")] string Schema,
    List<PackageExportSource> Sources,
    string CreationDate = "",
    string WinGetVersion = "");

[JsonSerializable(typeof(SourceExportPayload))]
[JsonSerializable(typeof(PackagesExportPayload))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CliJsonContext : JsonSerializerContext;
