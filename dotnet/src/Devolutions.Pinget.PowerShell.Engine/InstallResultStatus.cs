namespace Devolutions.Pinget.PowerShell.Engine;

internal static class InstallResultStatus
{
    /// <summary>
    /// Maps an install/uninstall/repair result to its PowerShell <c>Status</c>. A no-op result also
    /// reports success, so <c>NoOp</c> has to be checked first or it is never reported.
    /// </summary>
    internal static string From(bool success, bool noOp) => noOp ? "NoOp" : success ? "Ok" : "Error";
}
