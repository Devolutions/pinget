using System.Text.Json.Serialization;

namespace Devolutions.Pinget.Core;

[JsonConverter(typeof(JsonStringEnumConverter<SourceKind>))]
public enum SourceKind
{
    PreIndexed,
    Rest
}

public enum PinType
{
    Pinning,
    Blocking,
    Gating
}

public record SourceRecord
{
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Arg { get; init; }
    public required string Identifier { get; init; }
    public string TrustLevel { get; set; } = "None";
    public bool Explicit { get; set; }
    public int Priority { get; set; }
    public DateTime? LastUpdate { get; set; }
    public string? SourceVersion { get; set; }
}

/// <summary>
/// Library-hosting options for the Pinget core API.
/// </summary>
public record RepositoryOptions
{
    /// <summary>
    /// Storage root for source state and caches. A null value uses the real Desktop App Installer / WinGet state on Windows.
    /// </summary>
    public string? AppRoot { get; init; }
    public string UserAgent { get; init; } = "pinget-dotnet/0.1";
    public Action<RepositoryWarning>? Diagnostics { get; init; }
}

public record RepositoryWarning
{
    public required string Operation { get; init; }
    public required string SourceName { get; init; }
    public required SourceKind SourceKind { get; init; }
    public required string SourceArg { get; init; }
    public string? SourceIdentifier { get; init; }
    public string? RequestUri { get; init; }
    public string? CachePath { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
    public int? HttpStatusCode { get; init; }
}

public class SourceSearchException : InvalidOperationException
{
    public SourceSearchException(RepositoryWarning warning, Exception innerException)
        : base($"Failed during source operation '{warning.Operation}' for '{warning.SourceName}': {warning.Message}", innerException)
    {
        Warning = warning;
    }

    public RepositoryWarning Warning { get; }
}

public class MultiplePackageMatchesException : InvalidOperationException
{
    public MultiplePackageMatchesException(IEnumerable<SearchMatch> matches)
        : base($"multiple packages matched: {FormatChoices(matches)}")
    {
        Matches = matches.ToList();
    }

    public IReadOnlyList<SearchMatch> Matches { get; }

    private static string FormatChoices(IEnumerable<SearchMatch> matches) =>
        string.Join(", ", matches.Take(10).Select(m => $"{m.Name} [{m.Id}] ({m.SourceName})"));
}

public record PackageQuery
{
    public string? Query { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Moniker { get; init; }
    public string? Tag { get; init; }
    public string? Command { get; init; }
    public string? Source { get; init; }
    public int? Count { get; init; }
    public bool Exact { get; init; }
    public string? Version { get; init; }
    public string? Channel { get; init; }
    public string? Locale { get; init; }
    public string? InstallerType { get; init; }
    public string? InstallerArchitecture { get; init; }
    public string? InstallScope { get; init; }
    public string? Platform { get; init; }
    public string? OsVersion { get; init; }
}

public record ListQuery
{
    public string? Query { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Moniker { get; init; }
    public string? Tag { get; init; }
    public string? Command { get; init; }
    public string? ProductCode { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public int? Count { get; init; }
    public bool Exact { get; init; }
    public string? InstallScope { get; init; }
    public bool UpgradeOnly { get; init; }
    public bool IncludeUnknown { get; init; }
    public bool IncludePinned { get; init; }
}

public record SearchMatch
{
    public required string SourceName { get; init; }
    public required SourceKind SourceKind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Moniker { get; init; }
    public string? Version { get; init; }
    public string? Channel { get; init; }
    public string? MatchCriteria { get; init; }
}

public record SearchResponse
{
    public required List<SearchMatch> Matches { get; init; }
    public required List<string> Warnings { get; init; }
    public List<RepositoryWarning> SourceWarnings { get; init; } = [];
    public bool Truncated { get; init; }
}

public record ListMatch
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public required string LocalId { get; init; }
    public required string InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public string? SourceName { get; init; }
    public string? Publisher { get; init; }
    public string? Scope { get; init; }
    public string? InstallerCategory { get; init; }
    public string? InstallLocation { get; init; }
    public List<string> PackageFamilyNames { get; init; } = [];
    public List<string> ProductCodes { get; init; } = [];
    public List<string> UpgradeCodes { get; init; } = [];
}

public record ListResponse
{
    public required List<ListMatch> Matches { get; init; }
    public required List<string> Warnings { get; init; }
    public bool Truncated { get; init; }
}

public record VersionKey
{
    public required string Version { get; init; }
    public required string Channel { get; init; }
}

public record Documentation
{
    public string? Label { get; init; }
    public required string Url { get; init; }
}

public record PackageAgreement
{
    public string? Label { get; init; }
    public string? Text { get; init; }
    public string? Url { get; init; }
}

public record Installer
{
    public string? Architecture { get; init; }
    public string? InstallerType { get; init; }
    public string? NestedInstallerType { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public string? ProductCode { get; init; }
    public string? Locale { get; init; }
    public string? Scope { get; init; }
    public string? ReleaseDate { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? UpgradeCode { get; init; }
    public List<string> Platforms { get; init; } = [];
    public string? MinimumOsVersion { get; init; }
    public InstallerSwitches Switches { get; init; } = new();
    public List<string> Commands { get; init; } = [];
    public List<string> PackageDependencies { get; init; } = [];
}

public record InstallerSwitches
{
    public string? Silent { get; init; }
    public string? SilentWithProgress { get; init; }
    public string? Interactive { get; init; }
    public string? Custom { get; init; }
    public string? Log { get; init; }
    public string? InstallLocation { get; init; }

    public InstallerSwitches MergeWith(InstallerSwitches fallback) => new()
    {
        Silent = Silent ?? fallback.Silent,
        SilentWithProgress = SilentWithProgress ?? fallback.SilentWithProgress,
        Interactive = Interactive ?? fallback.Interactive,
        Custom = Custom ?? fallback.Custom,
        Log = Log ?? fallback.Log,
        InstallLocation = InstallLocation ?? fallback.InstallLocation,
    };

    public bool IsEmpty() =>
        string.IsNullOrWhiteSpace(Silent) &&
        string.IsNullOrWhiteSpace(SilentWithProgress) &&
        string.IsNullOrWhiteSpace(Interactive) &&
        string.IsNullOrWhiteSpace(Custom) &&
        string.IsNullOrWhiteSpace(Log) &&
        string.IsNullOrWhiteSpace(InstallLocation);
}

public enum InstallerMode
{
    Interactive,
    SilentWithProgress,
    Silent,
}

public record Manifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Channel { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? ShortDescription { get; init; }
    public string? Moniker { get; init; }
    public string? PackageUrl { get; init; }
    public string? PublisherUrl { get; init; }
    public string? PublisherSupportUrl { get; init; }
    public string? License { get; init; }
    public string? LicenseUrl { get; init; }
    public string? PrivacyUrl { get; init; }
    public string? Author { get; init; }
    public string? Copyright { get; init; }
    public string? CopyrightUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<PackageAgreement> Agreements { get; init; } = [];
    public List<string> PackageDependencies { get; init; } = [];
    public List<Documentation> Documentation { get; init; } = [];
    public List<Installer> Installers { get; init; } = [];
}

public record InstallRequest
{
    public required PackageQuery Query { get; init; }
    public string? ManifestPath { get; init; }
    public InstallerMode Mode { get; init; } = InstallerMode.SilentWithProgress;
    public string? LogPath { get; init; }
    public string? Custom { get; init; }
    public string? Override { get; init; }
    public string? InstallLocation { get; init; }
    public bool SkipDependencies { get; init; }
    public bool DependenciesOnly { get; init; }
    public bool AcceptPackageAgreements { get; init; }
    public bool Force { get; init; }
    public string? Rename { get; init; }
    public bool UninstallPrevious { get; init; }
    public bool IgnoreSecurityHash { get; init; }
    public string? DependencySource { get; init; }
    public bool NoUpgrade { get; init; }
}

public record RepairRequest
{
    public required PackageQuery Query { get; init; }
    public string? ManifestPath { get; init; }
    public string? ProductCode { get; init; }
    public InstallerMode Mode { get; init; } = InstallerMode.SilentWithProgress;
    public string? LogPath { get; init; }
    public bool AcceptPackageAgreements { get; init; }
    public bool Force { get; init; }
    public bool IgnoreSecurityHash { get; init; }
}

public record UninstallRequest
{
    public required PackageQuery Query { get; init; }
    public string? ManifestPath { get; init; }
    public string? ProductCode { get; init; }
    public InstallerMode Mode { get; init; } = InstallerMode.SilentWithProgress;
    public bool AllVersions { get; init; }
    public bool Force { get; init; }
    public bool Purge { get; init; }
    public bool Preserve { get; init; }
    public string? LogPath { get; init; }
}

public record ShowResult
{
    public required SearchMatch Package { get; init; }
    public required Manifest Manifest { get; init; }
    public Installer? SelectedInstaller { get; init; }
    public List<string> CachedFiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<RepositoryWarning> SourceWarnings { get; init; } = [];
    public required object StructuredDocument { private get; init; }

    public object ToStructuredDocument() => StructuredOutput.CollapseManifestDocuments(StructuredDocument);

    public SerializableShowManifest ToSerializableManifest()
    {
        var document = ToStructuredDocument() as Dictionary<string, object?> ??
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        string? GetString(string key) =>
            document.TryGetValue(key, out var value) && value is not null ? value.ToString() : null;

        return new SerializableShowManifest
        {
            PackageIdentifier = Manifest.Id,
            PackageName = Manifest.Name,
            PackageVersion = Manifest.Version,
            Channel = string.IsNullOrWhiteSpace(Manifest.Channel) ? null : Manifest.Channel,
            SourceName = Package.SourceName,
            SourceKind = Package.SourceKind,
            Publisher = Manifest.Publisher,
            Author = Manifest.Author,
            Moniker = Manifest.Moniker,
            Description = Manifest.Description,
            ShortDescription = Manifest.ShortDescription ?? GetString("ShortDescription"),
            PackageUrl = Manifest.PackageUrl,
            PublisherUrl = Manifest.PublisherUrl,
            PublisherSupportUrl = Manifest.PublisherSupportUrl,
            License = Manifest.License,
            LicenseUrl = Manifest.LicenseUrl,
            PrivacyUrl = Manifest.PrivacyUrl,
            Copyright = Manifest.Copyright,
            CopyrightUrl = Manifest.CopyrightUrl,
            ReleaseNotes = Manifest.ReleaseNotes,
            ReleaseNotesUrl = Manifest.ReleaseNotesUrl,
            Tags = Manifest.Tags,
            PackageDependencies = Manifest.PackageDependencies,
            Documentation = Manifest.Documentation,
            Agreements = Manifest.Agreements,
            Installers = Manifest.Installers.Select(SerializableInstaller.FromInstaller).ToList(),
            SelectedInstaller = SelectedInstaller is null ? null : SerializableInstaller.FromInstaller(SelectedInstaller),
            CachedFiles = CachedFiles,
            Warnings = Warnings,
            SourceWarnings = SourceWarnings,
        };
    }
}

public record SerializableShowManifest
{
    public required string PackageIdentifier { get; init; }
    public required string PackageName { get; init; }
    public required string PackageVersion { get; init; }
    public string? Channel { get; init; }
    public required string SourceName { get; init; }
    public required SourceKind SourceKind { get; init; }
    public string? Publisher { get; init; }
    public string? Author { get; init; }
    public string? Moniker { get; init; }
    public string? Description { get; init; }
    public string? ShortDescription { get; init; }
    public string? PackageUrl { get; init; }
    public string? PublisherUrl { get; init; }
    public string? PublisherSupportUrl { get; init; }
    public string? License { get; init; }
    public string? LicenseUrl { get; init; }
    public string? PrivacyUrl { get; init; }
    public string? Copyright { get; init; }
    public string? CopyrightUrl { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> PackageDependencies { get; init; } = [];
    public List<Documentation> Documentation { get; init; } = [];
    public List<PackageAgreement> Agreements { get; init; } = [];
    public List<SerializableInstaller> Installers { get; init; } = [];
    public SerializableInstaller? SelectedInstaller { get; init; }
    public List<string> CachedFiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<RepositoryWarning> SourceWarnings { get; init; } = [];
}

public record SerializableInstaller
{
    public string? Architecture { get; init; }
    public string? InstallerType { get; init; }
    public string? NestedInstallerType { get; init; }
    public string? InstallerUrl { get; init; }
    public string? InstallerSha256 { get; init; }
    public string? ProductCode { get; init; }
    public string? InstallerLocale { get; init; }
    public string? Scope { get; init; }
    public string? ReleaseDate { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? UpgradeCode { get; init; }
    public List<string> Platforms { get; init; } = [];
    public string? MinimumOsVersion { get; init; }
    public InstallerSwitches Switches { get; init; } = new();
    public List<string> Commands { get; init; } = [];
    public List<string> PackageDependencies { get; init; } = [];

    public static SerializableInstaller FromInstaller(Installer installer) => new()
    {
        Architecture = installer.Architecture,
        InstallerType = installer.InstallerType,
        NestedInstallerType = installer.NestedInstallerType,
        InstallerUrl = installer.Url,
        InstallerSha256 = installer.Sha256,
        ProductCode = installer.ProductCode,
        InstallerLocale = installer.Locale,
        Scope = installer.Scope,
        ReleaseDate = installer.ReleaseDate,
        PackageFamilyName = installer.PackageFamilyName,
        UpgradeCode = installer.UpgradeCode,
        Platforms = installer.Platforms,
        MinimumOsVersion = installer.MinimumOsVersion,
        Switches = installer.Switches,
        Commands = installer.Commands,
        PackageDependencies = installer.PackageDependencies,
    };
}

[JsonSerializable(typeof(SerializableShowManifest))]
[JsonSerializable(typeof(List<SerializableShowManifest>))]
[JsonSerializable(typeof(RepositoryWarning))]
[JsonSerializable(typeof(List<RepositoryWarning>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class PingetJsonContext : JsonSerializerContext;

public record VersionsResult
{
    public required SearchMatch Package { get; init; }
    public required List<VersionKey> Versions { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<RepositoryWarning> SourceWarnings { get; init; } = [];
}

public record CacheWarmResult
{
    public required SearchMatch Package { get; init; }
    public List<string> CachedFiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<RepositoryWarning> SourceWarnings { get; init; } = [];
}

public record SourceUpdateResult
{
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Detail { get; init; }
}

public record PinRecord
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string SourceId { get; init; }
    public required PinType PinType { get; init; }
}

public record InstallResult
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string InstallerPath { get; init; }
    public required string InstallerType { get; init; }
    public int ExitCode { get; init; }
    public bool Success { get; init; }
    public bool NoOp { get; init; }
    public List<string> Warnings { get; init; } = [];
}

// Internal type for installed package tracking
internal record InstalledPackage
{
    public required string Name { get; init; }
    public required string LocalId { get; init; }
    public required string InstalledVersion { get; init; }
    public string? Publisher { get; init; }
    public string? Scope { get; init; }
    public string? InstallerCategory { get; init; }
    public string? InstallLocation { get; init; }
    public List<string> PackageFamilyNames { get; init; } = [];
    public List<string> ProductCodes { get; init; } = [];
    public List<string> UpgradeCodes { get; init; } = [];
    public SearchMatch? Correlated { get; set; }
}

internal enum SearchSemantics
{
    Many,
    Single
}

internal record LocatedMatch
{
    public required SearchMatch Display { get; init; }
    public required int SourceIndex { get; init; }
    public required MatchLocator Locator { get; init; }
}

internal abstract record MatchLocator;

internal record PreIndexedV1Locator(long PackageRowId) : MatchLocator;
internal record PreIndexedV2Locator(long PackageRowId, string PackageHash) : MatchLocator;
internal record RestLocator(string PackageId, List<VersionKey> Versions) : MatchLocator;

internal static class StructuredOutput
{
    public static Dictionary<string, object?> CollapseManifestDocuments(object structuredDocument)
    {
        return structuredDocument switch
        {
            Dictionary<string, object?> document => CollapseManifestDocument(document),
            List<Dictionary<string, object?>> documents => CollapseManifestDocuments(documents),
            _ => throw new InvalidOperationException("Unexpected manifest document shape.")
        };
    }

    internal static List<Dictionary<string, object?>> CollapseManifestResults(IEnumerable<object> structuredDocuments)
        => structuredDocuments.Select(CollapseManifestDocuments).ToList();

    private static Dictionary<string, object?> CollapseManifestDocument(Dictionary<string, object?> document)
    {
        var manifestType = document.TryGetValue("ManifestType", out var value) ? value?.ToString() : null;
        if (string.Equals(manifestType, "merged", StringComparison.OrdinalIgnoreCase))
        {
            var singleton = new Dictionary<string, object?>(document, StringComparer.OrdinalIgnoreCase);
            singleton["ManifestType"] = "singleton";
            return singleton;
        }

        return new Dictionary<string, object?>(document, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> CollapseManifestDocuments(List<Dictionary<string, object?>> documents)
    {
        if (documents.Count == 0)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (documents.Count == 1)
            return CollapseManifestDocument(documents[0]);

        var version = documents.FirstOrDefault(d => string.Equals(GetString(d, "ManifestType"), "version", StringComparison.OrdinalIgnoreCase));
        var defaultLocale = documents.FirstOrDefault(d => string.Equals(GetString(d, "ManifestType"), "defaultLocale", StringComparison.OrdinalIgnoreCase));
        var installer = documents.FirstOrDefault(d => string.Equals(GetString(d, "ManifestType"), "installer", StringComparison.OrdinalIgnoreCase));

        var singleton = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        CopyKeys(singleton, version, "PackageIdentifier", "PackageVersion");
        CopyAllExcept(singleton, defaultLocale, "ManifestType", "ManifestVersion");
        CopyAllExcept(singleton, installer, "ManifestType", "ManifestVersion");

        if (!singleton.ContainsKey("PackageLocale"))
        {
            var packageLocale = GetString(defaultLocale, "PackageLocale") ?? GetString(version, "DefaultLocale");
            if (!string.IsNullOrWhiteSpace(packageLocale))
                singleton["PackageLocale"] = packageLocale;
        }

        singleton["ManifestType"] = "singleton";
        singleton["ManifestVersion"] =
            GetString(installer, "ManifestVersion") ??
            GetString(defaultLocale, "ManifestVersion") ??
            GetString(version, "ManifestVersion") ??
            "1.10.0";

        singleton.Remove("DefaultLocale");
        return singleton;
    }

    public static Dictionary<string, object?> ManifestDocument(Manifest manifest)
    {
        var document = new Dictionary<string, object?>
        {
            ["PackageIdentifier"] = manifest.Id,
            ["PackageName"] = manifest.Name,
            ["PackageVersion"] = manifest.Version,
            ["Tags"] = manifest.Tags,
            ["Agreements"] = manifest.Agreements.Select(AgreementDocument).ToList(),
            ["Documentations"] = manifest.Documentation.Select(DocumentationDocument).ToList(),
            ["Installers"] = manifest.Installers.Select(InstallerDocument).ToList(),
        };

        AddString(document, "Channel", manifest.Channel);
        AddString(document, "Publisher", manifest.Publisher);
        AddString(document, "Description", manifest.Description);
        AddString(document, "Moniker", manifest.Moniker);
        AddString(document, "PackageUrl", manifest.PackageUrl);
        AddString(document, "PublisherUrl", manifest.PublisherUrl);
        AddString(document, "PublisherSupportUrl", manifest.PublisherSupportUrl);
        AddString(document, "License", manifest.License);
        AddString(document, "LicenseUrl", manifest.LicenseUrl);
        AddString(document, "PrivacyUrl", manifest.PrivacyUrl);
        AddString(document, "Author", manifest.Author);
        AddString(document, "Copyright", manifest.Copyright);
        AddString(document, "CopyrightUrl", manifest.CopyrightUrl);
        AddString(document, "ReleaseNotes", manifest.ReleaseNotes);
        AddString(document, "ReleaseNotesUrl", manifest.ReleaseNotesUrl);

        if (manifest.PackageDependencies.Count > 0)
            document["Dependencies"] = PackageDependenciesDocument(manifest.PackageDependencies);

        return document;
    }

    public static Dictionary<string, object?> DocumentationDocument(Documentation documentation)
    {
        var document = new Dictionary<string, object?> { ["DocumentUrl"] = documentation.Url };
        AddString(document, "DocumentLabel", documentation.Label);
        return document;
    }

    public static Dictionary<string, object?> AgreementDocument(PackageAgreement agreement)
    {
        var document = new Dictionary<string, object?>();
        AddString(document, "AgreementLabel", agreement.Label);
        AddString(document, "Agreement", agreement.Text);
        AddString(document, "AgreementUrl", agreement.Url);
        return document;
    }

    public static Dictionary<string, object?> InstallerDocument(Installer installer)
    {
        var document = new Dictionary<string, object?>();
        AddString(document, "Architecture", installer.Architecture);
        AddString(document, "InstallerType", installer.InstallerType);
        AddString(document, "NestedInstallerType", installer.NestedInstallerType);
        AddString(document, "InstallerUrl", installer.Url);
        AddString(document, "InstallerSha256", installer.Sha256);
        AddString(document, "ProductCode", installer.ProductCode);
        AddString(document, "InstallerLocale", installer.Locale);
        AddString(document, "Scope", installer.Scope);
        AddString(document, "ReleaseDate", installer.ReleaseDate);
        AddString(document, "PackageFamilyName", installer.PackageFamilyName);
        AddString(document, "UpgradeCode", installer.UpgradeCode);
        if (installer.Platforms.Count > 0)
            document["Platform"] = installer.Platforms;
        AddString(document, "MinimumOSVersion", installer.MinimumOsVersion);
        if (installer.Commands.Count > 0)
            document["Commands"] = installer.Commands;
        if (installer.PackageDependencies.Count > 0)
            document["Dependencies"] = PackageDependenciesDocument(installer.PackageDependencies);
        if (!installer.Switches.IsEmpty())
            document["InstallerSwitches"] = InstallerSwitchesDocument(installer.Switches);
        return document;
    }

    public static Dictionary<string, object?> InstallerSwitchesDocument(InstallerSwitches switches)
    {
        var document = new Dictionary<string, object?>();
        AddString(document, "Silent", switches.Silent);
        AddString(document, "SilentWithProgress", switches.SilentWithProgress);
        AddString(document, "Interactive", switches.Interactive);
        AddString(document, "Custom", switches.Custom);
        AddString(document, "Log", switches.Log);
        AddString(document, "InstallLocation", switches.InstallLocation);
        return document;
    }

    private static Dictionary<string, object?> PackageDependenciesDocument(IEnumerable<string> packageDependencies) =>
        new()
        {
            ["PackageDependencies"] = packageDependencies
                .Select(packageId => new Dictionary<string, object?> { ["PackageIdentifier"] = packageId })
                .ToList()
        };

    private static void AddString(Dictionary<string, object?> document, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            document[key] = value;
    }

    private static void CopyAllExcept(Dictionary<string, object?> target, Dictionary<string, object?>? source, params string[] excludedKeys)
    {
        if (source is null)
            return;

        var excluded = new HashSet<string>(excludedKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (!excluded.Contains(key))
                target[key] = value;
        }
    }

    private static void CopyKeys(Dictionary<string, object?> target, Dictionary<string, object?>? source, params string[] keys)
    {
        if (source is null)
            return;

        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value))
                target[key] = value;
        }
    }

    private static string? GetString(Dictionary<string, object?>? source, string key) =>
        source is not null && source.TryGetValue(key, out var value) ? value?.ToString() : null;
}
