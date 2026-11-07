using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Engine;

/// <summary>
/// Runs the per-package part of <c>Update-PingetPackage</c>. A package that fails to update is recorded
/// as an <c>Error</c> result plus a warning, and the loop moves on, so one failure neither skips the
/// remaining packages nor loses the results already produced (the CLI upgrade loop behaves the same way).
/// </summary>
internal static class UpdateBatch
{
    internal static List<PSInstallResult> Run<T>(
        IEnumerable<T> matches,
        Func<T, (string Id, string? Name, string? Source)> identity,
        Func<T, PSInstallResult> update,
        List<string> warnings)
    {
        var results = new List<PSInstallResult>();
        foreach (var match in matches)
        {
            try
            {
                results.Add(update(match));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var (id, name, source) = identity(match);
                warnings.Add(FailureWarning(id, ex));
                results.Add(FailureResult(id, name, source));
            }
        }

        return results;
    }

    internal static string FailureWarning(string id, Exception exception) => $"Failed to update {id}: {exception.Message}";

    internal static PSInstallResult FailureResult(string id, string? name, string? source) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? id : name!,
        Source = string.IsNullOrWhiteSpace(source) ? null : source,
        CorrelationData = string.Empty,
        Status = "Error",
    };
}
