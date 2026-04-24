using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsCommon.Find, Constants.PingetNouns.Package)]
[Alias("fdpgp")]
[OutputType(typeof(PSFoundCatalogPackage))]
public sealed class FindPackageCmdlet : FinderExtendedCmdlet
{
    protected override void ProcessRecord()
    {
        using var client = PingetClient.Open();
        var result = client.FindPackages(Id, Name, Moniker, Source, Query, Tag, Command, Count, MatchOption);
        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Items, enumerateCollection: true);
    }
}
