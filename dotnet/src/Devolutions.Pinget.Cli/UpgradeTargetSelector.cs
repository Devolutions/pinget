using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal static class UpgradeTargetSelector
{
    internal const string AmbiguousMessage =
        "Multiple installed packages found matching input criteria. Please refine the input.";

    /// <summary>
    /// Picks the rows <c>upgrade</c> will install. <c>--all</c> takes every upgradeable row. A named
    /// upgrade (query, <c>--id</c>, <c>--name</c>, <c>--moniker</c>) targets one package, as in WinGet:
    /// the list filter is a substring match, so an exact match wins, and any remaining ambiguity is an
    /// error instead of upgrading every package that happens to contain the text. <c>--id</c> is
    /// compared only to the id, <c>--name</c> only to the name, and the query to either.
    /// <see cref="ListMatch"/> carries no moniker, so a moniker selector narrows only through the list
    /// query itself.
    /// </summary>
    internal static List<ListMatch> Select(
        IEnumerable<ListMatch> matches,
        bool all,
        string? query,
        string? id,
        string? name,
        string? moniker)
    {
        var upgradeable = matches.Where(match => match.AvailableVersion is not null).ToList();
        var hasSelector = query is not null || id is not null || name is not null || moniker is not null;
        if (all || !hasSelector || DistinctIdCount(upgradeable) <= 1)
            return upgradeable;

        var exact = upgradeable
            .Where(match =>
                (id is not null && match.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
                (name is not null && match.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                (query is not null &&
                    (match.Id.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                     match.Name.Equals(query, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (exact.Count > 0 && DistinctIdCount(exact) == 1)
            return exact;

        var candidates = string.Join(Environment.NewLine, upgradeable.Select(match => $"  {match.Name} ({match.Id})"));
        throw new InvalidOperationException($"{AmbiguousMessage}{Environment.NewLine}{candidates}");
    }

    private static int DistinctIdCount(IEnumerable<ListMatch> rows)
        => rows.Select(match => match.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
