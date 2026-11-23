namespace Devolutions.Pinget.PowerShell.Engine.PSObjects;

public sealed class PSSourceResult
{
    public required string Name { get; init; }

    public required string Argument { get; init; }

    public required string Type { get; init; }

    public string TrustLevel { get; init; } = "None";

    public string? Publisher { get; init; }

    public string[] TrustedCa { get; init; } = [];

    public bool Explicit { get; init; }

    public int Priority { get; init; }

    public required string Identifier { get; init; }

    public DateTime? LastUpdate { get; init; }

    public string? SourceVersion { get; init; }
}
