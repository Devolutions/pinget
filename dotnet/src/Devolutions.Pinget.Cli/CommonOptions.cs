using System.CommandLine;
using Devolutions.Pinget.Cli.Extensions;

namespace Devolutions.Pinget.Cli;

internal static class CommonOptions
{
    internal static Option<string?> Query => new Option<string?>("--query", "Query").WithAliases("-q");

    internal static Option<string?> Id => new("--id", "Filter by id");

    internal static Option<string?> Name => new("--name", "Filter by name");

    internal static Option<string?> Moniker => new("--moniker", "Filter by moniker");

    internal static Option<string?> Source => new Option<string?>("--source", "Source name").WithAliases("-s");

    internal static Option<bool> Exact => new Option<bool>("--exact", "Exact match").WithAliases("-e");

    internal static Option<int?> Count => new Option<int?>("--count", "Max results").WithAliases("-n");

    internal static Option<string?> Version => new Option<string?>("--version", "Version").WithAliases("-v");
}
