using System.Collections;
using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsDiagnostic.Test, Constants.PingetNouns.UserSetting)]
[Alias("tpgus", "Test-PingetUserSettings")]
[OutputType(typeof(bool))]
public sealed class TestUserSettingCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)]
    public Hashtable UserSettings { get; set; } = new();

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter IgnoreNotSet { get; set; }

    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        WriteObject(client.TestUserSettings(HashtableConverter.ToJsonObject(UserSettings), IgnoreNotSet));
    }
}
