using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsData.Update, Constants.PingetNouns.Package, DefaultParameterSetName = Constants.FoundSet, SupportsShouldProcess = true)]
[OutputType(typeof(PSInstallResult))]
public sealed class UpdatePackageCmdlet : InstallCmdlet
{
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter IncludeUnknown { get; set; }

    protected override void ProcessRecord()
    {
        var target = PSCatalogPackage?.Id ?? Id ?? Name ?? string.Join(" ", Query ?? []);
        if (!ShouldProcess(target, "Update package"))
            return;

        using var client = PingetClient.Open();
        var result = client.UpdatePackages(
            PSCatalogPackage,
            Version,
            Id,
            Name,
            Moniker,
            Source,
            Query,
            AllowHashMismatch,
            Override,
            Custom,
            Location,
            Log,
            Force,
            Header,
            SkipDependencies,
            Locale,
            Scope,
            Architecture,
            Mode,
            InstallerType,
            IncludeUnknown);

        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Items, enumerateCollection: true);
    }
}
