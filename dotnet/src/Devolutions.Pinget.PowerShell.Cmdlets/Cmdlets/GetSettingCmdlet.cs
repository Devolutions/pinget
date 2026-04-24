using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Get, Constants.PingetNouns.Setting)]
[Alias("gpgse", "Get-PingetSettings")]
public sealed class GetSettingCmdlet : PSCmdlet
{
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter AsPlainText { get; set; }

    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        var settings = client.GetAdminSettings();
        WriteObject(AsPlainText ? settings.ToJsonString(new() { WriteIndented = true }) : HashtableConverter.ToHashtable(settings));
    }
}
