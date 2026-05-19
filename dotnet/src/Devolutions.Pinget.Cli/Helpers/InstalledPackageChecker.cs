using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class InstalledPackageChecker
{
    internal static bool IsPresent(Repository repo, string packageId, string? sourceName) =>
        repo.List(new ListQuery
        {
            Id = packageId,
            Source = sourceName,
            Exact = true,
            Count = 1,
        }).Matches.Count > 0;
}
