using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class PinTarget
{
    internal static SearchMatch ResolveSingleAvailable(Repository repo, PackageQuery query)
    {
        var result = repo.Search(query);
        if (result.Matches.Count == 0)
            throw new InvalidOperationException("No package matched the query.");
        if (result.Matches.Count > 1)
            throw new InvalidOperationException("Multiple packages matched the query; refine the query.");
        return result.Matches[0];
    }

    internal static ListMatch ResolveSingleInstalled(Repository repo, PackageQuery query)
    {
        var result = repo.List(new ListQuery
        {
            Query = query.Query,
            Id = query.Id,
            Name = query.Name,
            Moniker = query.Moniker,
            Tag = query.Tag,
            Command = query.Command,
            Source = query.Source,
            Exact = query.Exact,
            Count = 200,
        });
        if (result.Matches.Count == 0)
            throw new InvalidOperationException("No installed package matched the query.");
        if (result.Matches.Count > 1)
            throw new InvalidOperationException("Multiple installed packages matched the query; refine the query.");
        return result.Matches[0];
    }
}
