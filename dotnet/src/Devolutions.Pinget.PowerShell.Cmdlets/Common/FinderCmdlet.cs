using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

public abstract class FinderCmdlet : PSCmdlet
{
    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Id { get; set; }

    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Name { get; set; }

    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Moniker { get; set; }

    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Source { get; set; }

    [Parameter(
        ParameterSetName = Constants.FoundSet,
        Position = 0,
        ValueFromPipelineByPropertyName = true,
        ValueFromRemainingArguments = true)]
    public string[]? Query { get; set; }

    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public PSPackageFieldMatchOption MatchOption { get; set; } = PSPackageFieldMatchOption.ContainsCaseInsensitive;
}
