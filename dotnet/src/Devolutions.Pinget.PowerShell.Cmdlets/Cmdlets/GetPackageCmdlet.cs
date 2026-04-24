using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Get, Constants.PingetNouns.Package)]
[Alias("gpgp")]
[OutputType(typeof(PSInstalledCatalogPackage))]
public sealed class GetPackageCmdlet : FinderExtendedCmdlet
{
    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        var result = client.GetInstalledPackages(Id, Name, Moniker, Source, Query, Tag, Command, Count, MatchOption);
        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Items, enumerateCollection: true);
    }
}
