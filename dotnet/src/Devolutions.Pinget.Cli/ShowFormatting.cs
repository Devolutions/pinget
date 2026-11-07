using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

/// <summary>Text lines of the <c>show</c> output that match the Rust CLI's formatting.</summary>
internal static class ShowFormatting
{
    /// <summary>A documentation entry: <c>  label: url</c>, or just <c>  url</c> when it has no label.</summary>
    internal static string DocumentationLine(Documentation documentation)
        => string.IsNullOrEmpty(documentation.Label)
            ? $"  {documentation.Url}"
            : $"  {documentation.Label}: {documentation.Url}";

    /// <summary>
    /// The selected installer's dependencies: package dependencies (with WinGet's <c>[&gt;= MinimumVersion]</c>
    /// suffix), then the Windows feature, Windows library and external ones.
    /// </summary>
    internal static List<string> InstallerDependencyLines(Installer installer)
    {
        var lines = new List<string>();
        var dependencies = installer.Dependencies;
        var packages = dependencies.PackageDependencies.Count == 0
            ? installer.PackageDependencies
            : dependencies.PackageDependencies
                .Select(dependency => dependency.MinimumVersion is { } minimum
                    ? $"{dependency.PackageIdentifier} [>= {minimum}]"
                    : dependency.PackageIdentifier)
                .ToList();

        void Add(string label, List<string> values)
        {
            if (values.Count > 0)
                lines.Add($"  {label}: {string.Join(", ", values)}");
        }

        Add("Dependencies", packages);
        Add("Windows Features", dependencies.WindowsFeatures);
        Add("Windows Libraries", dependencies.WindowsLibraries);
        Add("External Dependencies", dependencies.ExternalDependencies);
        return lines;
    }
}
