using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsDiagnostic.Repair, Constants.PingetNouns.Package, DefaultParameterSetName = Constants.FoundSet, SupportsShouldProcess = true)]
[OutputType(typeof(PSRepairResult))]
public sealed class RepairPackageCmdlet : PackageCmdlet
{
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public PSPackageRepairMode Mode { get; set; } = PSPackageRepairMode.Default;

    [Parameter]
    public string? Log { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter AllowHashMismatch { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter Force { get; set; }

    protected override void ProcessRecord()
    {
        var target = PSCatalogPackage?.Id ?? Id ?? Name ?? string.Join(" ", Query ?? []);
        if (!ShouldProcess(target, "Repair package"))
            return;

        using var client = PingetClient.Open();
        var result = client.RepairPackage(PSCatalogPackage, Version, Id, Name, Moniker, Source, Query, AllowHashMismatch, Force, Log, Mode);
        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Value);
    }
}
