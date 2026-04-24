using System.ComponentModel.DataAnnotations;
using System.Management.Automation;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

public abstract class FinderExtendedCmdlet : FinderCmdlet
{
    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Tag { get; set; }

    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public string? Command { get; set; }

    [ValidateRange(Constants.CountLowerBound, Constants.CountUpperBound)]
    [Parameter(ParameterSetName = Constants.FoundSet, ValueFromPipelineByPropertyName = true)]
    public uint Count { get; set; }
}
