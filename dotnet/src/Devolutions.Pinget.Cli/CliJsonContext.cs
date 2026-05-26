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

internal sealed record PackageExportSourceDetails(string Name, string Argument, string Type);

internal sealed record PackageExportSource(
    PackageExportSourceDetails SourceDetails,
    List<PackageExportEntry> Packages);

internal sealed record PackagesExportPayload(string Schema, List<PackageExportSource> Sources);

[JsonSerializable(typeof(SourceExportPayload))]
[JsonSerializable(typeof(PackagesExportPayload))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CliJsonContext : JsonSerializerContext;
