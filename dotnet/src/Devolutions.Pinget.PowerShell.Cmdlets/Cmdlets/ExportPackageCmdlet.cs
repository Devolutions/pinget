using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Cmdlets.Common;
using Devolutions.Pinget.PowerShell.Engine;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Cmdlets;

[Cmdlet(VerbsData.Export, Constants.PingetNouns.Package, DefaultParameterSetName = Constants.FoundSet, SupportsShouldProcess = true)]
[Alias("epgp")]
[OutputType(typeof(PSDownloadResult))]
public sealed class ExportPackageCmdlet : InstallerSelectionCmdlet
{
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? DownloadDirectory { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public SwitchParameter SkipMicrosoftStoreLicense { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public PSWindowsPlatform Platform { get; set; } = PSWindowsPlatform.Default;

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? TargetOSVersion { get; set; }

    protected override void ProcessRecord()
    {
        var target = PSCatalogPackage?.Id ?? Id ?? Name ?? string.Join(' ', Query ?? []);
        if (!ShouldProcess(target, "Export package"))
            return;

        using var client = PingetClient.Open();
        var result = client.DownloadPackage(
            PSCatalogPackage,
            Version,
            Id,
            Name,
            Moniker,
            Source,
            Query,
            AllowHashMismatch,
            SkipDependencies,
            Locale,
            Scope,
            Architecture,
            InstallerType,
            DownloadDirectory,
            SkipMicrosoftStoreLicense,
            Platform,
            TargetOSVersion);

        foreach (var warning in result.Warnings)
            WriteWarning(warning);
        WriteObject(result.Value);
    }
}
