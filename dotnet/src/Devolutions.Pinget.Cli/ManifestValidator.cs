using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Devolutions.Pinget.Cli;

internal sealed record ManifestValidationReport(List<string> Errors, List<string> Warnings);

/// <summary>
/// <c>pinget validate</c>, ported from the Rust CLI (<c>validate_manifest_path</c>): every YAML file is
/// parsed, then checked for the required fields of its ManifestType. A missing path, a directory with no
/// YAML files, or an unreadable/unparsable file is a fatal error. JSON schema validation needs a schema
/// validator the C# CLI does not ship, so it reports the same YAML-only warning the Rust CLI uses when no
/// schema is available.
/// </summary>
internal static class ManifestValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static ManifestValidationReport Validate(string manifestPath)
    {
        if (!File.Exists(manifestPath) && !Directory.Exists(manifestPath))
            throw new InvalidOperationException($"Path does not exist: {manifestPath}");

        var files = Directory.Exists(manifestPath)
            ? Directory.EnumerateFiles(manifestPath)
                .Where(path => Path.GetExtension(path) is ".yaml" or ".yml")
                .ToList()
            : [manifestPath];

        if (files.Count == 0)
            throw new InvalidOperationException($"No YAML manifest files found in: {manifestPath}");

        var errors = new List<string>();
        var warnings = new List<string>();
        var schemaBase = FindSchemaDirectory();

        foreach (var file in files)
        {
            YamlMappingNode? root;
            try
            {
                root = ParseRoot(File.ReadAllBytes(file));
            }
            catch (Exception ex) when (ex is YamlException or DecoderFallbackException or InvalidOperationException)
            {
                throw new InvalidOperationException($"YAML parse error in {file}: {ex.Message}", ex);
            }

            var manifestType = (GetString(root, "ManifestType") ?? "singleton").ToLowerInvariant();
            var manifestVersion = GetString(root, "ManifestVersion") ?? "0.1.0";

            if (schemaBase is not null)
            {
                var schemaPath = Path.Combine(schemaBase, MapManifestVersion(manifestVersion), $"manifest.{manifestType}.{manifestVersion}.json");
                warnings.Add(File.Exists(schemaPath)
                    ? $"  {file}: JSON schema validation is not available in this build, performing YAML-only validation"
                    : $"  {file}: schema not found: {schemaPath}");
            }
            else
            {
                warnings.Add($"  {file}: schema directory not found, performing YAML-only validation");
            }

            if ((manifestType == "singleton" || manifestType == "installer") && !HasField(root, "Installers"))
                errors.Add($"  {file}: required field 'Installers' is missing");

            if (manifestType == "singleton" || manifestType == "defaultlocale")
            {
                foreach (var field in new[] { "PackageIdentifier", "PackageVersion" })
                {
                    if (!HasField(root, field) && manifestVersion != "0.1.0")
                        errors.Add($"  {file}: required field '{field}' is missing");
                }
            }
        }

        return new ManifestValidationReport(errors, warnings);
    }

    /// <summary>Prints the report like the Rust CLI and returns the exit code.</summary>
    internal static int Print(ManifestValidationReport report, bool ignoreWarnings)
    {
        if (report.Warnings.Count > 0 && !ignoreWarnings)
        {
            Console.WriteLine("Manifest validation warning.");
            foreach (var warning in report.Warnings)
                Console.WriteLine(warning);
        }

        if (report.Errors.Count > 0)
        {
            Console.WriteLine("Manifest validation failed.");
            foreach (var error in report.Errors)
                Console.WriteLine(error);
            return 1;
        }

        if (report.Warnings.Count == 0 || ignoreWarnings)
            Console.WriteLine("Manifest validation succeeded.");
        return 0;
    }

    /// <summary>Parses a single YAML document; a non-mapping root has no manifest fields.</summary>
    private static YamlMappingNode? ParseRoot(byte[] bytes)
    {
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        var content = bytes.AsSpan();
        if (content.StartsWith(utf8Bom))
            content = content[utf8Bom.Length..];

        var stream = new YamlStream();
        stream.Load(new StringReader(StrictUtf8.GetString(content)));
        if (stream.Documents.Count > 1)
            throw new InvalidOperationException("deserializing from YAML containing more than one document is not supported");

        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
    }

    private static bool HasField(YamlMappingNode? root, string name) =>
        root is not null && root.Children.ContainsKey(new YamlScalarNode(name));

    private static string? GetString(YamlMappingNode? root, string name) =>
        root is not null && root.Children.TryGetValue(new YamlScalarNode(name), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string MapManifestVersion(string version) =>
        version == "0.1.0" ? "preview" : version.StartsWith("1.", StringComparison.Ordinal) ? $"v{version}" : "latest";

    private static string? FindSchemaDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; depth < 10 && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, "schemas", "JSON", "manifests");
                if (Directory.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }
}
