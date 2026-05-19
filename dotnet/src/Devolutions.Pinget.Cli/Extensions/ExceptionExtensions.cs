namespace Devolutions.Pinget.Cli.Extensions;

internal static class ExceptionExtensions
{
    extension(Exception ex)
    {
        internal bool CanIgnoreUnavailableImportFailure => ex is InvalidOperationException &&
            (ex.Message.Contains("No package matched the query", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("No applicable installer found", StringComparison.OrdinalIgnoreCase));
    }
}
