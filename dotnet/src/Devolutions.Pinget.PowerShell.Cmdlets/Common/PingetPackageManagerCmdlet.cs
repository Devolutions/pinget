using System.Management.Automation;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

public abstract class PingetPackageManagerCmdlet : PSCmdlet
{
    [Parameter(ParameterSetName = Constants.IntegrityVersionSet, ValueFromPipelineByPropertyName = true)]
    public string Version { get; set; } = string.Empty;

    [Parameter(ParameterSetName = Constants.IntegrityLatestSet, ValueFromPipelineByPropertyName = true)]
    public SwitchParameter Latest { get; set; }

    [Parameter(ParameterSetName = Constants.IntegrityLatestSet, ValueFromPipelineByPropertyName = true)]
    [Parameter(ParameterSetName = Constants.IntegrityVersionSet, ValueFromPipelineByPropertyName = true)]
    public SwitchParameter IncludePrerelease { get; set; }
}
