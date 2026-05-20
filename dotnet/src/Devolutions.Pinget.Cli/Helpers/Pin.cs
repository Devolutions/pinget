using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class Pin
{
    internal static List<PinRecord> Filter(Repository repo, PackageQuery query)
    {
        IEnumerable<PinRecord> pins = repo.ListPins(query.Source);
        if (!string.IsNullOrWhiteSpace(query.Id))
            pins = pins.Where(pin => Text.Matches(pin.PackageId, query.Id, query.Exact));

        var needsCatalogResolution =
            !string.IsNullOrWhiteSpace(query.Query) ||
            !string.IsNullOrWhiteSpace(query.Name) ||
            !string.IsNullOrWhiteSpace(query.Moniker) ||
            !string.IsNullOrWhiteSpace(query.Tag) ||
            !string.IsNullOrWhiteSpace(query.Command);
        if (!needsCatalogResolution)
            return pins.ToList();

        var searchResult = repo.Search(query);
        var keys = searchResult.Matches
            .Select(match => $"{match.Id}|{match.SourceName ?? ""}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = searchResult.Matches
            .Select(match => match.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return pins
            .Where(pin => keys.Contains($"{pin.PackageId}|{pin.SourceId}") ||
                (string.IsNullOrWhiteSpace(pin.SourceId) && ids.Contains(pin.PackageId)))
            .ToList();
    }

    internal static PinRecord? FindMatching(ListMatch match, IReadOnlyList<PinRecord> pins)
    {
        PinRecord? sourceSpecific = null;
        PinRecord? sourceAgnostic = null;
        foreach (var pin in pins)
        {
            if (!pin.PackageId.Equals(match.Id, StringComparison.OrdinalIgnoreCase) &&
                !pin.PackageId.Equals(match.LocalId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pin.SourceId))
            {
                if (!string.IsNullOrWhiteSpace(match.SourceName) &&
                    pin.SourceId.Equals(match.SourceName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceSpecific = pin;
                    break;
                }
            }
            else if (sourceAgnostic is null)
            {
                sourceAgnostic = pin;
            }
        }

        return sourceSpecific ?? sourceAgnostic;
    }

}
