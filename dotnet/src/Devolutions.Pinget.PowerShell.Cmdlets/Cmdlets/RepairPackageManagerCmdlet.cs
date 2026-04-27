using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsDiagnostic.Repair, Constants.PingetNouns.PingetPackageManager, DefaultParameterSetName = Constants.IntegrityVersionSet)]
[OutputType(typeof(int))]
public sealed class RepairPackageManagerCmdlet : PingetPackageManagerCmdlet
{
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter AllUsers { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter Force { get; set; }

    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        var result = client.RepairPackageManager(
            ParameterSetName == Constants.IntegrityLatestSet ? null : Version,
            Latest,
            IncludePrerelease,
            AllUsers,
            Force);
        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Value);
    }
}
