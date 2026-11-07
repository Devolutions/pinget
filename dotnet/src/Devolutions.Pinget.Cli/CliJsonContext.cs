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

/// <summary>
/// <c>pin list --output json|yaml</c> row. Same shape as the Rust CLI's serialized <c>PinRecord</c>
/// (PascalCase keys, pin type as its name).
/// </summary>
internal sealed record PinListEntry(string PackageId, string Version, string SourceId, string PinType)
{
    internal static List<PinListEntry> FromPins(IEnumerable<Devolutions.Pinget.Core.PinRecord> pins) =>
        pins.Select(pin => new PinListEntry(pin.PackageId, pin.Version, pin.SourceId, pin.PinType.ToString())).ToList();
}

[JsonSerializable(typeof(SourceExportPayload))]
[JsonSerializable(typeof(PackagesExportPayload))]
[JsonSerializable(typeof(List<PinListEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class CliJsonContext : JsonSerializerContext;
