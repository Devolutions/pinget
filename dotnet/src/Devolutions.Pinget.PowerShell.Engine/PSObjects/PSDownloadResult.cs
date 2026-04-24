namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSDownloadResult
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Source { get; init; }

    public required string CorrelationData { get; init; }

    public string Status { get; init; } = "Ok";

    public Exception ExtendedErrorCode => new PingetOperationException("Pinget download operation result.", 0);

    public required string Version { get; init; }

    public required string DownloadDirectory { get; init; }

    public required string DownloadedInstallerPath { get; init; }

    public bool Succeeded() => string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase);

    public string ErrorMessage() =>
        $"DownloadStatus '{Status}' ExtendedError '{ExtendedErrorCode.HResult}'";
}
