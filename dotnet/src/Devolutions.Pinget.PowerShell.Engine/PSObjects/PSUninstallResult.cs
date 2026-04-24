namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSUninstallResult
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Source { get; init; }

    public required string CorrelationData { get; init; }

    public uint UninstallerErrorCode { get; init; }

    public bool RebootRequired { get; init; }

    public string Status { get; init; } = "Unknown";

    public Exception ExtendedErrorCode => new PingetOperationException("Pinget uninstall operation result.", unchecked((int)UninstallerErrorCode));

    public bool Succeeded() => string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase);

    public string ErrorMessage() =>
        $"UninstallStatus '{Status}' UninstallerErrorCode '{UninstallerErrorCode}' ExtendedError '{ExtendedErrorCode.HResult}'";
}
