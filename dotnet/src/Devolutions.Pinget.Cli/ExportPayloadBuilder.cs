using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal static class ExportPayloadBuilder
{
    internal const string Schema = "https://aka.ms/winget-packages.schema.2.0.json";

    /// <summary>
    /// Groups installed packages by the source they correlate to. Packages with no source
    /// cannot be re-imported and are skipped; <paramref name="sourceFilter"/> keeps one source.
    /// </summary>
    internal static PackagesExportPayload Build(
        IEnumerable<ListMatch> matches,
        IReadOnlyList<SourceRecord> sources,
        string? sourceFilter,
        bool includeVersions,
        string wingetVersion = "",
        DateTimeOffset? creationDate = null)
    {
        var groups = matches
            .Where(match => !string.IsNullOrWhiteSpace(match.SourceName))
            .Where(match => sourceFilter is null ||
                match.SourceName!.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(match => match.SourceName!, StringComparer.OrdinalIgnoreCase);

        var exportSources = new List<PackageExportSource>();
        foreach (var group in groups)
        {
            var record = sources.FirstOrDefault(source => source.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            var details = new PackageExportSourceDetails(
                Name: record?.Name ?? group.Key,
                Argument: record?.Arg ?? "",
                Type: record?.Kind switch
                {
                    SourceKind.Rest => "Microsoft.Rest",
                    _ => "Microsoft.PreIndexed.Package",
                },
                Identifier: record?.Identifier ?? "");
            var packages = group
                .Select(match => new PackageExportEntry(
                    PackageIdentifier: match.Id,
                    Version: includeVersions ? match.InstalledVersion : null))
                .ToList();
            exportSources.Add(new PackageExportSource(details, packages));
        }

        var created = (creationDate ?? DateTimeOffset.UtcNow).UtcDateTime;
        return new PackagesExportPayload(
            Schema,
            exportSources,
            CreationDate: created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            WinGetVersion: wingetVersion);
    }
}
