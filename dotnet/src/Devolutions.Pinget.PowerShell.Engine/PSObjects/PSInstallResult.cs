namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSInstallResult
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Source { get; init; }

    public required string CorrelationData { get; init; }

    public uint InstallerErrorCode { get; init; }

    public bool RebootRequired { get; init; }

    public string Status { get; init; } = "Unknown";

    public Exception ExtendedErrorCode => new PingetOperationException("Pinget install operation result.", unchecked((int)InstallerErrorCode));

    public bool Succeeded() => string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase);

    public string ErrorMessage() =>
        $"InstallStatus '{Status}' InstallerErrorCode '{InstallerErrorCode}' ExtendedError '{ExtendedErrorCode.HResult}'";
}
