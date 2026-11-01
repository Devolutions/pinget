using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Pinget.Publisher;

internal sealed record ManifestFile(string RelativePath, string Hash);

internal sealed record MergedManifest(
    string PackageId,
    string Version,
    string Locale,
    string DefaultLocale,
    string RelativePath,
    byte[] Content,
    string Hash,
    JsonObject Model);

internal sealed record IndexedPackage(
    string Id,
    string Name,
    string? Moniker,
    string LatestVersion,
    IReadOnlyList<MergedManifest> Manifests,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Pfns,
    IReadOnlyList<string> ProductCodes,
    IReadOnlyList<string> UpgradeCodes,
    IReadOnlyList<string> NormalizedNames,
    IReadOnlyList<string> NormalizedPublishers,
    string? ArpMinimumVersion,
    string? ArpMaximumVersion,
    byte[] VersionData,
    byte[] CompressedVersionData,
    byte[] PackageHash);

internal sealed record ScanResult(
    IReadOnlyDictionary<string, string> Inventory,
    IReadOnlyList<IndexedPackage> Packages,
    IReadOnlyList<MergedManifest> Manifests);

internal static partial class ManifestScanner
{
    private static readonly HashSet<string> SupportedSchemaVersions = new(StringComparer.Ordinal)
    {
        "1.0.0", "1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0",
        "1.6.0", "1.7.0", "1.8.0", "1.9.0", "1.10.0",
    };

    private static readonly HashSet<string> DocumentTypes = new(StringComparer.Ordinal)
    {
        "singleton", "version", "installer", "defaultLocale", "locale",
    };

    public static ScanResult Scan(string inputDirectory, bool generateArtifacts = true)
    {
        var root = Path.GetFullPath(inputDirectory);
        if (!Directory.Exists(root))
            throw new PublisherException($"Manifest directory does not exist: {root}");
        RejectReparsePoint(root);

        var files = EnumerateYamlFiles(root);
        if (files.Count == 0)
            throw new PublisherException($"No YAML manifests were found under '{root}'.");

        var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var documents = new Dictionary<(string Id, string Version), List<ManifestDocument>>();
        var casing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 8 * 1024 * 1024)
                throw new PublisherException($"Manifest exceeds the 8 MiB limit: {relativePath}");
            var rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            inventory.Add(relativePath, rawHash);

            var model = ParseYaml(bytes, relativePath);
            var id = RequiredString(model, "PackageIdentifier", relativePath);
            var version = RequiredString(model, "PackageVersion", relativePath);
            var type = RequiredString(model, "ManifestType", relativePath);
            var schemaVersion = RequiredString(model, "ManifestVersion", relativePath);
            if (!IdentifierPattern().IsMatch(id))
                throw new PublisherException($"Unsafe PackageIdentifier '{id}' in {relativePath}.");
            if (casing.TryGetValue(id, out var existingId) && !string.Equals(existingId, id, StringComparison.Ordinal))
                throw new PublisherException($"PackageIdentifier differs only by casing: '{existingId}' and '{id}'.");
            casing[id] = id;
            if (!VersionPattern().IsMatch(version))
                throw new PublisherException($"Unsafe PackageVersion '{version}' in {relativePath}.");
            if (!DocumentTypes.Contains(type))
                throw new PublisherException($"Unsupported ManifestType '{type}' in {relativePath}.");
            if (!SupportedSchemaVersions.Contains(schemaVersion))
                throw new PublisherException($"Unsupported ManifestVersion '{schemaVersion}' in {relativePath}; supported versions are {string.Join(", ", SupportedSchemaVersions)}.");

            string? locale = OptionalString(model, "PackageLocale");
            if (type is "defaultLocale" or "locale")
            {
                if (string.IsNullOrWhiteSpace(locale) || !LocalePattern().IsMatch(locale))
                    throw new PublisherException($"Invalid or missing PackageLocale in {relativePath}.");
            }

            var key = (id, version);
            if (!documents.TryGetValue(key, out var group))
                documents.Add(key, group = []);
            group.Add(new ManifestDocument(type, locale, relativePath, model));
        }

        var merged = MergeDocuments(documents);
        RejectOutputCollisions(merged);
        foreach (var group in merged.GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase))
            _ = ValidateVersions(group);
        return generateArtifacts ? BuildPackages(inventory, merged) : new ScanResult(inventory, [], merged);
    }

    private static List<string> EnumerateYamlFiles(string root)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            RejectReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else if (Path.GetExtension(entry).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(entry).Equals(".yml", StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }
        }
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PublisherException($"Symbolic links and reparse points are not allowed in publisher paths: {path}");
    }

    private static JsonObject ParseYaml(byte[] bytes, string path)
    {
        try
        {
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                throw new PublisherException($"Expected one YAML mapping document in {path}.");
            return (JsonObject)ConvertNode(mapping, 0);
        }
        catch (PublisherException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PublisherException($"Could not parse YAML manifest '{path}': {ex.Message}", ex);
        }
    }

    private static JsonNode ConvertNode(YamlNode node, int depth)
    {
        if (depth > 100)
            throw new PublisherException("YAML nesting depth exceeds the 100-level limit.");
        return node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping, depth + 1),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(child => ConvertNode(child, depth + 1)).ToArray()),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => throw new PublisherException($"Unsupported YAML node type: {node.GetType().Name}"),
        };
    }

    private static JsonObject ConvertMapping(YamlMappingNode mapping, int depth)
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrEmpty(key.Value))
                throw new PublisherException("Manifest mapping keys must be non-empty strings.");
            if (result.ContainsKey(key.Value))
                throw new PublisherException($"Duplicate YAML mapping key '{key.Value}'.");
            result.Add(key.Value, IsStringField(key.Value)
                ? ConvertStringNode(pair.Value, depth) : ConvertNode(pair.Value, depth));
        }

        return result;
    }

    private static JsonNode ConvertStringNode(YamlNode node, int depth)
    {
        if (depth > 100)
            throw new PublisherException("YAML nesting depth exceeds the 100-level limit.");
        return node switch
        {
            YamlScalarNode scalar => JsonValue.Create(scalar.Value ?? string.Empty)!,
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(child => ConvertStringNode(child, depth + 1)).ToArray()),
            _ => ConvertNode(node, depth),
        };
    }

    private static bool IsStringField(string key) =>
        key.EndsWith("Sha256", StringComparison.Ordinal) || key.EndsWith("Url", StringComparison.Ordinal)
        || key is "PackageIdentifier" or "PackageVersion" or "ManifestVersion" or "ManifestType"
            or "PackageLocale" or "DefaultLocale" or "PackageName" or "Publisher" or "Moniker"
            or "DisplayName" or "DisplayVersion" or "MinimumVersion" or "MinimumOSVersion"
            or "ProductCode" or "UpgradeCode" or "PackageFamilyName" or "InstallerLocale"
            or "Architecture" or "InstallerType" or "Scope" or "FileType" or "IconFileType"
            or "License" or "Copyright" or "ShortDescription" or "Description" or "ReleaseNotes"
            or "ReleaseDate" or "RelativeFilePath" or "PortableCommandAlias" or "Commands" or "Tags"
            or "FileExtensions" or "Protocols" or "Capabilities" or "RestrictedCapabilities"
            or "InstallerSwitches" or "InstallLocation"
            or "InstallModes" or "Platform" or "UpgradeBehavior" or "NestedInstallerType"
            or "IconResolution" or "IconTheme" or "AuthenticationType" or "Argument"
            or "Command" or "Custom" or "Silent" or "SilentWithProgress" or "Interactive"
            or "Log" or "Repair" or "Uninstall" or "Update" or "Channel"
            or "WindowsFeatures" or "WindowsLibraries" or "ExternalDependencies" or "DocumentLabel"
            or "InvocationParameter" or "ReturnResponse" or "AllowedMarkets" or "ExcludedMarkets"
            or "UnsupportedOSArchitectures" or "UnsupportedArguments" or "ElevationRequirement"
            or "RepairBehavior" or "InstallerIdentifier" or "ProductId" or "StoreProductId";

    private static JsonNode ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
            && (value is "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)))
            return null!;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)))
            return JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase))!;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
            && long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var integer)
            && !value.Contains('.'))
            return JsonValue.Create(integer)!;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
            && ulong.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var unsignedInteger))
            return JsonValue.Create(unsignedInteger)!;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
            && (value.Contains('.') || value.Contains('e') || value.Contains('E'))
            && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floating))
        {
            if (!double.IsFinite(floating))
                throw new PublisherException("Non-finite YAML numbers cannot be preserved in JSON-compatible manifests.");
            return JsonNode.Parse(FormatFloatingPoint(floating))!;
        }
        return JsonValue.Create(value)!;
    }

    private static string FormatFloatingPoint(double value)
    {
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
        var sign = text.StartsWith('-') ? "-" : string.Empty;
        text = text.TrimStart('-');
        var exponentOffset = text.IndexOf('e');
        var exponent = exponentOffset < 0 ? 0 : int.Parse(text[(exponentOffset + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var mantissa = exponentOffset < 0 ? text : text[..exponentOffset];
        var point = mantissa.IndexOf('.');
        if (point >= 0)
            exponent -= mantissa.Length - point - 1;
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
            return sign + "0.0";
        while (digits.Length > 1 && digits.EndsWith('0'))
        {
            digits = digits[..^1];
            exponent++;
        }
        var decimalPosition = digits.Length + exponent;
        if (decimalPosition > 0 && decimalPosition <= 16)
            return sign + (decimalPosition >= digits.Length
                ? digits + new string('0', decimalPosition - digits.Length) + ".0"
                : digits.Insert(decimalPosition, "."));
        if (decimalPosition <= 0 && decimalPosition > -5)
            return sign + "0." + new string('0', -decimalPosition) + digits;
        var scientificExponent = decimalPosition - 1;
        return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : string.Empty)
            + "e" + (scientificExponent >= 0 ? "+" : string.Empty)
            + scientificExponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<MergedManifest> MergeDocuments(
        Dictionary<(string Id, string Version), List<ManifestDocument>> documentGroups)
    {
        var result = new List<MergedManifest>();
        foreach (var entry in documentGroups.OrderBy(x => x.Key.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Key.Version, StringComparer.Ordinal))
        {
            var docs = entry.Value;
            var singleton = docs.Where(d => d.Type == "singleton").ToList();
            if (singleton.Count > 1)
                throw new PublisherException($"Duplicate singleton manifest for {entry.Key.Id} {entry.Key.Version}.");
            if (singleton.Count != 0)
            {
                if (docs.Count != 1)
                    throw new PublisherException($"Singleton and multi-file manifests conflict for {entry.Key.Id} {entry.Key.Version}.");
                var singletonModel = (JsonObject)singleton[0].Model.DeepClone();
                var locale = OptionalString(singletonModel, "PackageLocale")
                    ?? throw new PublisherException($"Singleton manifest is missing PackageLocale: {singleton[0].Path}");
                if (!LocalePattern().IsMatch(locale))
                    throw new PublisherException($"Invalid PackageLocale '{locale}' in {singleton[0].Path}.");
                AddMerged(result, entry.Key.Id, entry.Key.Version, locale, locale, singletonModel);
                continue;
            }

            var versionDocs = docs.Where(d => d.Type == "version").ToList();
            var installerDocs = docs.Where(d => d.Type == "installer").ToList();
            var defaultDocs = docs.Where(d => d.Type == "defaultLocale").ToList();
            var localeDocs = docs.Where(d => d.Type == "locale").ToList();
            if (versionDocs.Count != 1 || defaultDocs.Count != 1)
                throw new PublisherException($"Multi-file set {entry.Key.Id} {entry.Key.Version} requires exactly one version and defaultLocale manifest; missing or duplicate documents are not allowed.");
            if (installerDocs.Count != 1 || installerDocs[0].Model["Installers"] is not JsonArray { Count: > 0 })
                throw new PublisherException($"Multi-file set {entry.Key.Id} {entry.Key.Version} requires exactly one installer manifest with nonempty Installers.");
            var defaultLocale = defaultDocs[0].Locale!;
            var declaredDefaultLocale = RequiredString(versionDocs[0].Model, "DefaultLocale", versionDocs[0].Path);
            if (!string.Equals(declaredDefaultLocale, defaultLocale, StringComparison.OrdinalIgnoreCase))
                throw new PublisherException($"DefaultLocale '{declaredDefaultLocale}' does not match defaultLocale document '{defaultLocale}'.");
            var localeGroups = localeDocs.GroupBy(d => d.Locale!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in localeGroups)
                if (group.Value.Count != 1)
                    throw new PublisherException($"Duplicate locale manifest '{group.Key}' for {entry.Key.Id} {entry.Key.Version}.");
            if (localeGroups.ContainsKey(defaultLocale))
                throw new PublisherException($"A locale manifest duplicates the default locale '{defaultLocale}' for {entry.Key.Id} {entry.Key.Version}.");
            var model = new JsonObject();
            MergeObject(model, versionDocs[0].Model);
            MergeObject(model, installerDocs[0].Model);
            MergeObject(model, defaultDocs[0].Model);
            var localizations = model["Localization"] as JsonArray ?? new JsonArray();
            if (model["Localization"] is null)
                model["Localization"] = localizations;
            foreach (var localeGroup in localeGroups.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var localization = (JsonObject)localeGroup.Value[0].Model.DeepClone();
                foreach (var key in new[] { "ManifestType", "ManifestVersion", "PackageIdentifier", "PackageVersion" })
                    localization.Remove(key);
                localizations.Add((JsonNode)localization);
            }
            if (localizations.Count == 0)
                model.Remove("Localization");
            model["ManifestType"] = "singleton";
            AddMerged(result, entry.Key.Id, entry.Key.Version, defaultLocale, defaultLocale, model);
        }
        return result;
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Key is "ManifestType" or "PackageIdentifier" or "PackageVersion" or "ManifestVersion"
                && target.ContainsKey(pair.Key))
                continue;
            if (pair.Value is JsonObject child && target[pair.Key] is JsonObject existing)
                MergeObject(existing, child);
            else
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static void AddMerged(List<MergedManifest> target, string id, string version, string locale, string defaultLocale, JsonObject model)
    {
        ValidatePathSegment(locale, "PackageLocale");
        _ = RequiredString(model, "PackageName", id);
        _ = RequiredString(model, "Publisher", id);
        if (model["Installers"] is not JsonArray { Count: > 0 })
            throw new PublisherException($"Manifest {id} {version} requires nonempty Installers.");
        var content = CanonicalJson(model);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var relativePath = $"manifests/{id}/{version}/{locale}/{hash[..16]}.yaml";
        target.Add(new MergedManifest(id, version, locale, defaultLocale, relativePath, content, hash, model));
    }

    private static ScanResult BuildPackages(SortedDictionary<string, string> inventory, List<MergedManifest> manifests)
    {
        var packages = new List<IndexedPackage>();
        foreach (var group in manifests.GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var versions = ValidateVersions(group);
            var latestVersion = versions[^1];
            var latest = group.First(m => m.Version.Equals(latestVersion, StringComparison.OrdinalIgnoreCase)
                && m.Locale.Equals(m.DefaultLocale, StringComparison.OrdinalIgnoreCase));
            var all = group.ToArray();
            var sidecarYaml = BuildVersionData(all);
            var compressed = MsZipCompressor.Compress(sidecarYaml);
            var tags = Values(all, "Tags");
            var commands = Values(all, "Commands");
            var pfns = Values(all, "PackageFamilyName");
            var products = Values(all, "ProductCode");
            var upgradeCodes = Values(all, "UpgradeCode");
            var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
            var normalizedPublishers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manifest in all)
            {
                foreach (var pair in AppEntries(manifest.Model))
                {
                    if (pair.DisplayName is not null)
                        normalizedNames.Add(NormalizeName(pair.DisplayName));
                    if (pair.Publisher is not null)
                        normalizedPublishers.Add(NormalizePublisher(pair.Publisher));
                    if (pair.ProductCode is not null)
                        products = products.Append(pair.ProductCode).ToArray();
                    if (pair.UpgradeCode is not null)
                        upgradeCodes = upgradeCodes.Append(pair.UpgradeCode).ToArray();
                }
                foreach (var scope in CatalogScopes(manifest.Model))
                {
                    var name = OptionalString(scope, "PackageName");
                    var publisher = OptionalString(scope, "Publisher");
                    if (name is not null)
                        normalizedNames.Add(NormalizeName(name));
                    if (publisher is not null)
                        normalizedPublishers.Add(NormalizePublisher(publisher));
                }
            }

            var arpVersions = all.SelectMany(m => AppEntries(m.Model))
                .SelectMany(entry => new[] { entry.DisplayVersion })
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .OrderBy(v => v, VersionComparer.Instance)
                .ToArray();
            packages.Add(new IndexedPackage(
                group.Key,
                RequiredString(latest.Model, "PackageName", latest.RelativePath),
                OptionalString(latest.Model, "Moniker"),
                latestVersion,
                all,
                tags,
                commands,
                pfns,
                DistinctTerms(products),
                DistinctTerms(upgradeCodes),
                DistinctTerms(normalizedNames),
                DistinctTerms(normalizedPublishers),
                arpVersions.FirstOrDefault(),
                arpVersions.LastOrDefault(),
                sidecarYaml,
                compressed,
                System.Security.Cryptography.SHA256.HashData(compressed)));
        }

        return new ScanResult(inventory, packages, manifests);
    }

    private static string[] ValidateVersions(IEnumerable<MergedManifest> group)
    {
        if (group.GroupBy(m => m.Version, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Select(m => m.Version).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new PublisherException("Package version differs only by casing.");
        var versions = group.Select(m => m.Version).OrderBy(v => v, VersionComparer.Instance).ToArray();
        for (var index = 1; index < versions.Length; index++)
            if (VersionComparer.Instance.Compare(versions[index - 1], versions[index]) == 0)
                throw new PublisherException($"Package versions '{versions[index - 1]}' and '{versions[index]}' compare equally; refusing ambiguous latest-version selection.");
        return versions;
    }

    private static byte[] BuildVersionData(IReadOnlyList<MergedManifest> manifests)
    {
        var sorted = manifests.OrderByDescending(m => m.Version, VersionComparer.Instance)
            .ThenBy(m => m.Locale, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder("sV: \"1.0\"\nvD:\n");
        foreach (var manifest in sorted)
        {
            builder.Append("  - v: ").Append(QuoteYaml(manifest.Version)).Append('\n');
            var arpVersions = AppEntries(manifest.Model)
                .Select(entry => entry.DisplayVersion)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .OrderBy(value => value, VersionComparer.Instance)
                .ToArray();
            if (arpVersions.Length > 0)
            {
                builder.Append("    aMiV: ").Append(QuoteYaml(arpVersions[0])).Append('\n');
                builder.Append("    aMaV: ").Append(QuoteYaml(arpVersions[^1])).Append('\n');
            }
            builder.Append("    rP: ").Append(QuoteYaml(manifest.RelativePath)).Append('\n');
            builder.Append("    s256H: ").Append(QuoteYaml(manifest.Hash)).Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<MergedManifest> manifests, string key) =>
        DistinctTerms(manifests.SelectMany(m => CatalogScopes(m.Model).SelectMany(obj => StringValues(obj[key]))));

    private static IReadOnlyList<string> DistinctTerms(IEnumerable<string> terms) =>
        terms.Where(s => !string.IsNullOrWhiteSpace(s)).Order(StringComparer.Ordinal)
            .GroupBy(s => new string(s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray()), StringComparer.Ordinal)
            .Select(g => g.First()).Order(StringComparer.Ordinal).ToArray();

    private static IEnumerable<JsonObject> CatalogScopes(JsonObject model)
    {
        yield return model;
        foreach (var key in new[] { "Installers", "Localization" })
            if (model[key] is JsonArray array)
                foreach (var item in array.OfType<JsonObject>())
                    yield return item;
    }

    private static IEnumerable<string> StringValues(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            foreach (var item in array)
                if (item is JsonValue json && json.TryGetValue<string>(out var text) && text is not null)
                    yield return text;
        }
        else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text is not null)
        {
            yield return text;
        }
    }

    private static IEnumerable<(string? DisplayName, string? Publisher, string? ProductCode, string? UpgradeCode, string? DisplayVersion)> AppEntries(JsonObject model)
    {
        foreach (var obj in CatalogScopes(model))
        {
            if (obj["AppsAndFeaturesEntries"] is JsonArray entries)
                foreach (var entry in entries.OfType<JsonObject>())
                    yield return (
                        OptionalString(entry, "DisplayName"),
                        OptionalString(entry, "Publisher"),
                        OptionalString(entry, "ProductCode"),
                        OptionalString(entry, "UpgradeCode"),
                        OptionalString(entry, "DisplayVersion"));
        }
    }

    private static IEnumerable<JsonObject> Descendants(JsonObject model)
    {
        yield return model;
        foreach (var child in model.Select(pair => pair.Value))
        {
            if (child is JsonObject nested)
                foreach (var item in Descendants(nested))
                    yield return item;
            else if (child is JsonArray array)
                foreach (var nestedObject in array.OfType<JsonObject>())
                    foreach (var item in Descendants(nestedObject))
                        yield return item;
        }
    }

    private static string NormalizeName(string value)
    {
        var normalized = Devolutions.Pinget.Core.NameNormalization.NormalizeName(value).Name;
        return normalized;
    }

    private static string NormalizePublisher(string value) =>
        Devolutions.Pinget.Core.NameNormalization.NormalizePublisher(value);

    private static string QuoteYaml(string value) =>
        $"\"{JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";

    private static byte[] CanonicalJson(JsonObject model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(writer, model);
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteCanonical(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                if (node is null)
                    writer.WriteNullValue();
                else
                    node.WriteTo(writer);
                break;
        }
    }

    private static void RejectOutputCollisions(IEnumerable<MergedManifest> manifests)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
            if (!paths.Add(manifest.RelativePath))
                throw new PublisherException($"Manifest output path collision: {manifest.RelativePath}");
    }

    private static void ValidatePathSegment(string value, string label)
    {
        if (value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\') || value.Contains(':'))
            throw new PublisherException($"Unsafe {label} path component: '{value}'.");
    }

    private static string RequiredString(JsonObject model, string key, string path)
    {
        var value = OptionalString(model, key);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new PublisherException($"Required field '{key}' is missing in {path}.");
    }

    private static string? OptionalString(JsonObject model, string key) =>
        model[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed record ManifestDocument(string Type, string? Locale, string Path, JsonObject Model);

    private sealed class VersionComparer : IComparer<string>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            var left = ParsedVersion.Parse(x ?? string.Empty);
            var right = ParsedVersion.Parse(y ?? string.Empty);
            if (left.IsLatest != right.IsLatest)
                return left.IsLatest ? 1 : -1;
            if (left.IsUnknown != right.IsUnknown)
                return left.IsUnknown ? -1 : 1;
            for (var index = 0; index < Math.Max(left.Parts.Count, right.Parts.Count); index++)
            {
                var part = ComparePart(
                    index < left.Parts.Count ? left.Parts[index] : new VersionPart(0, string.Empty),
                    index < right.Parts.Count ? right.Parts[index] : new VersionPart(0, string.Empty));
                if (part != 0)
                    return part;
            }
            return left.Approximation.CompareTo(right.Approximation);
        }

        private static int ComparePart(VersionPart left, VersionPart right)
        {
            var integer = left.Integer.CompareTo(right.Integer);
            if (integer != 0)
                return integer;
            if (left.Other.Length == 0)
                return right.Other.Length == 0 ? 0 : 1;
            if (right.Other.Length == 0)
                return -1;
            return StringComparer.Ordinal.Compare(left.Other, right.Other);
        }

        private sealed record ParsedVersion(bool IsLatest, bool IsUnknown, int Approximation, IReadOnlyList<VersionPart> Parts)
        {
            public static ParsedVersion Parse(string source)
            {
                var value = source.Trim();
                var approximation = 0;
                if (value.StartsWith("< ", StringComparison.OrdinalIgnoreCase))
                {
                    approximation = -1;
                    value = value[2..];
                }
                else if (value.StartsWith("> ", StringComparison.OrdinalIgnoreCase))
                {
                    approximation = 1;
                    value = value[2..];
                }
                var firstDigit = value.IndexOfAny("0123456789".ToCharArray());
                var firstSeparator = value.IndexOf('.');
                if (firstDigit >= 0 && (firstSeparator < 0 || firstDigit < firstSeparator))
                    value = value[firstDigit..];
                var parts = value.Split('.').Select(VersionPart.Parse).ToList();
                while (parts.Count > 0 && parts[^1].Integer == 0 && parts[^1].Other.Length == 0)
                    parts.RemoveAt(parts.Count - 1);
                var latest = parts.Count == 1 && parts[0].Integer == 0 && parts[0].Other.Equals("latest", StringComparison.OrdinalIgnoreCase);
                var unknown = parts.Count == 1 && parts[0].Integer == 0 && parts[0].Other.Equals("unknown", StringComparison.OrdinalIgnoreCase);
                return new ParsedVersion(latest, unknown, approximation, parts);
            }
        }

        private readonly record struct VersionPart(ulong Integer, string Other)
        {
            public static VersionPart Parse(string raw)
            {
                var part = raw.Trim();
                var digits = 0;
                while (digits < part.Length && char.IsAsciiDigit(part[digits]))
                    digits++;
                if (digits == 0 || !ulong.TryParse(part.AsSpan(0, digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var integer))
                    return new VersionPart(0, part.ToLowerInvariant());
                return new VersionPart(integer, part[digits..].ToLowerInvariant());
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern();
}
