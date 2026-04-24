using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsLifecycle.Assert, Constants.PingetNouns.PingetPackageManager, DefaultParameterSetName = Constants.IntegrityVersionSet)]
[Alias("apgpm")]
public sealed class AssertPackageManagerCmdlet : PingetPackageManagerCmdlet
{
    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        client.AssertPackageManager(ParameterSetName == Constants.IntegrityLatestSet ? null : Version, Latest, IncludePrerelease);
    }
}
