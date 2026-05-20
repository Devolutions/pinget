namespace Devolutions.Pinget.Cli.Helpers;

internal static class Text
{
    internal static bool Matches(string value, string query, bool exact) =>
    exact
        ? value.Equals(query, StringComparison.OrdinalIgnoreCase)
        : value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
