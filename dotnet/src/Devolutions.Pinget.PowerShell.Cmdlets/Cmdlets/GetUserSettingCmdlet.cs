using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Get, Constants.PingetNouns.UserSetting)]
[OutputType(typeof(System.Collections.Hashtable))]
public sealed class GetUserSettingCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        WriteObject(HashtableConverter.ToHashtable(client.GetUserSettings()));
    }
}
