using System.CommandLine;
using System.CommandLine.Parsing;

namespace Devolutions.Pinget.Cli;

/// <summary>
/// Flags WinGet accepts on every command (<c>Argument::GetCommon</c>) plus <c>--accept-source-agreements</c>,
/// so scripts written for WinGet parse unchanged. Only <c>--disable-interactivity</c> and
/// <c>--ignore-warnings</c> change behavior; the rest are accepted for compatibility (as in the Rust CLI).
/// </summary>
internal sealed class CliCommonOptions
{
    internal Option<bool> DisableInteractivity { get; } =
        new("--disable-interactivity", "Suppress prompts; package agreements must be accepted on the command line");

    internal Option<bool> IgnoreWarnings { get; } =
        new(["--ignore-warnings", "--nowarn"], "Suppress warning output");

    internal Option<bool> AcceptSourceAgreements { get; } =
        new("--accept-source-agreements", "Accepted for compatibility with WinGet") { IsHidden = true };

    internal Option<bool> Wait { get; } =
        new("--wait", "Accepted for compatibility with WinGet") { IsHidden = true };

    internal Option<bool> VerboseLogs { get; } =
        new(["--verbose-logs", "--verbose"], "Accepted for compatibility with WinGet") { IsHidden = true };

    internal Option<bool> NoVt { get; } =
        new("--no-vt", "Accepted for compatibility with WinGet") { IsHidden = true };

    internal Option<bool> NoProgress { get; } =
        new("--no-progress", "Accepted for compatibility with WinGet") { IsHidden = true };

    internal IEnumerable<Option> All =>
        [DisableInteractivity, IgnoreWarnings, AcceptSourceAgreements, Wait, VerboseLogs, NoVt, NoProgress];

    internal void AddTo(Command root)
    {
        foreach (var option in All)
            root.AddGlobalOption(option);
    }

    internal void Apply(ParseResult parseResult)
    {
        CliSession.DisableInteractivity = parseResult.GetValueForOption(DisableInteractivity);
        CliSession.IgnoreWarnings = parseResult.GetValueForOption(IgnoreWarnings);
    }
}

/// <summary>Process-wide CLI state set from the common options before a command runs.</summary>
internal static class CliSession
{
    internal static bool DisableInteractivity { get; set; }

    internal static bool IgnoreWarnings { get; set; }

    /// <summary>The agreements prompt needs a terminal on both ends and must not run under --disable-interactivity.</summary>
    internal static bool AgreementsPromptAllowed(bool disableInteractivity, bool inputRedirected, bool errorRedirected) =>
        !disableInteractivity && !inputRedirected && !errorRedirected;

    /// <summary>Writes a <c>warning:</c> line to stderr unless --ignore-warnings was given.</summary>
    internal static void WriteWarning(string message)
    {
        if (!IgnoreWarnings)
            Console.Error.WriteLine($"warning: {message}");
    }
}
