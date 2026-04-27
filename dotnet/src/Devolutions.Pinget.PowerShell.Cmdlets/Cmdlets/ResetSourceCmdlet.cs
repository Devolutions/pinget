using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Reset, Constants.PingetNouns.Source, DefaultParameterSetName = Constants.DefaultSet)]
public sealed class ResetSourceCmdlet : PSCmdlet
{
    [Parameter(
        Position = 0,
        Mandatory = true,
        ParameterSetName = Constants.DefaultSet,
        ValueFromPipeline = true,
        ValueFromPipelineByPropertyName = true)]
    public string? Name { get; set; }

    [Parameter(ParameterSetName = Constants.OptionalSet, ValueFromPipelineByPropertyName = true)]
    public SwitchParameter All { get; set; }

    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        client.ResetSource(Name, All);
    }
}
