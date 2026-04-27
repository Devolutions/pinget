using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Remove, Constants.PingetNouns.Source)]
public sealed class RemoveSourceCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    public string Name { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        client.RemoveSource(Name);
    }
}
