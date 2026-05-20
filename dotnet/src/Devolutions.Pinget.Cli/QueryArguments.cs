using System.CommandLine;

namespace Devolutions.Pinget.Cli;

internal static class QueryArguments
{
    internal static Argument<string?> Package => new("query", () => null, "Package query");
    internal static Argument<string?> Search => new("query", () => null, "Search query");
}
