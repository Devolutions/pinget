namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSRepairResult
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Source { get; init; }

    public required string CorrelationData { get; init; }

    public uint RepairErrorCode { get; init; }

    public bool RebootRequired { get; init; }

    public string Status { get; init; } = "Unknown";

    public Exception ExtendedErrorCode => new PingetOperationException("Pinget repair operation result.", unchecked((int)RepairErrorCode));

    public bool Succeeded() => string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase);

    public string ErrorMessage() =>
        $"RepairStatus '{Status}' RepairErrorCode '{RepairErrorCode}' ExtendedError '{ExtendedErrorCode.HResult}'";
}
