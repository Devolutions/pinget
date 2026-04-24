using System.Management.Automation;
using Devolutions.Pinget.PowerShell.Engine.PSObjects;

namespace Devolutions.Pinget.PowerShell.Cmdlets.Common;

public abstract class PackageCmdlet : FinderCmdlet
{
    protected PackageCmdlet()
    {
        MatchOption = PSPackageFieldMatchOption.EqualsCaseInsensitive;
    }

    [Alias("InputObject")]
    [ValidateNotNull]
    [Parameter(
        ParameterSetName = Constants.GivenSet,
        Position = 0,
        ValueFromPipeline = true,
        ValueFromPipelineByPropertyName = true)]
    public PSCatalogPackage? PSCatalogPackage { get; set; }

    [Parameter(ValueFromPipelineByPropertyName = true)]
    public string? Version { get; set; }
}
