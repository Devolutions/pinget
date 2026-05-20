using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class PinQuery
{
    internal static PackageQuery Create(
    string? query,
    string? id,
    string? name,
    string? moniker,
    string? tag,
    string? command,
    string? source,
    bool exact) =>
    new()
    {
        Query = query,
        Id = id,
        Name = name,
        Moniker = moniker,
        Tag = tag,
        Command = command,
        Source = source,
        Exact = exact,
        Count = 200,
    };

    internal static void EnsureProvided(PackageQuery query, string commandName)
    {
        if (string.IsNullOrWhiteSpace(query.Query) &&
            string.IsNullOrWhiteSpace(query.Id) &&
            string.IsNullOrWhiteSpace(query.Name) &&
            string.IsNullOrWhiteSpace(query.Moniker) &&
            string.IsNullOrWhiteSpace(query.Tag) &&
            string.IsNullOrWhiteSpace(query.Command))
        {
            throw new InvalidOperationException($"{commandName} requires a query or explicit filter.");
        }
    }
}
