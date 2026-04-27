using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Get, Constants.PingetNouns.Version)]
[OutputType(typeof(string))]
public sealed class GetVersionCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        WriteObject(client.GetVersion());
    }
}
