namespace Devolutions.Pinget.PowerShell.Engine;

public sealed record CollectionResult<T>(IReadOnlyList<T> Items, IReadOnlyList<string> Warnings, bool Truncated = false);

public sealed record CommandResult<T>(T Value, IReadOnlyList<string> Warnings);
