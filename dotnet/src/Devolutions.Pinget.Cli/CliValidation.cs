namespace Devolutions.Pinget.Cli;

internal static class CliValidation
{
    internal const string CountOutOfBoundsMessage = "The requested number of results must be between 1 and 1000.";

    /// <summary><c>--count</c> accepts 1..1000, as in WinGet (and the Rust CLI).</summary>
    internal static bool IsValidCount(int count) => count is >= 1 and <= 1000;

    /// <summary>WinGet's <c>ValidateArgumentDependency</c> error (<c>DependencyArgumentMissing</c>).</summary>
    internal static string DependencyArgumentMissing(string argument, string dependency) =>
        $"Argument {argument} can only be used with {dependency}";

    /// <summary>WinGet's <c>ValidateExclusiveArguments</c> error (<c>MultipleExclusiveArgumentsProvided</c>).</summary>
    internal static string MultipleExclusiveArguments(params string[] names) =>
        $"Multiple mutually exclusive arguments provided: {string.Join('|', names)}";
}
