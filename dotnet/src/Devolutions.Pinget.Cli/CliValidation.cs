namespace Devolutions.Pinget.Cli;

internal static class CliValidation
{
    internal const string CountOutOfBoundsMessage = "The requested number of results must be between 1 and 1000.";

    /// <summary><c>--count</c> accepts 1..1000, as in WinGet (and the Rust CLI).</summary>
    internal static bool IsValidCount(int count) => count is >= 1 and <= 1000;
}
