using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Core;

namespace Pinget.Publisher;

/// <summary>Explicit local inputs; dependency identities are mapped without downloading packages.</summary>
public sealed record HomebrewImportOptions(
    string FormulaJson, string BottleArchive, string ManifestOutput, string BottleMirror,
    string Tag, string TargetOS, string Architecture, string BaseUrl);

/// <summary>Bounded offline import of one native formula bottle target.</summary>
public static class HomebrewImporter
{
    public static JsonObject Import(HomebrewImportOptions options)
    {
        if ((options.Tag, options.TargetOS, options.Architecture) is not
            (("arm64_sonoma", "macOS", "arm64") or ("sonoma", "macOS", "x64") or
             ("x86_64_linux", "Linux", "x64") or ("aarch64_linux", "Linux", "arm64")))
            throw Invalid("unsupported target: select a Sonoma macOS or native glibc Linux bottle with its matching architecture");
        if (!PublicUrl(options.BaseUrl))
            throw Invalid("base URL must be a public HTTPS URL without credentials, escapes, query, fragment, or dot segments");
        var formulaPath = SafePath(options.FormulaJson);
        var archivePath = SafePath(options.BottleArchive);
        var output = SafePath(options.ManifestOutput);
        var mirror = SafePath(options.BottleMirror);
        if (Within(output, mirror) || Within(mirror, output)
            || new[] { formulaPath, archivePath }.Any(p => Within(p, output) || Within(p, mirror)))
            throw Invalid("formula JSON, bottle input, manifest output and bottle mirror must be disjoint; output directories must not overlap");

        using var formulaStream = File.OpenRead(formulaPath);
        if (formulaStream.Length > 8 * 1024 * 1024)
            throw Invalid("formula JSON must be a regular file no larger than 8 MiB");
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int count;
        while ((count = formulaStream.Read(chunk)) != 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > 8 * 1024 * 1024)
                throw Invalid("formula JSON exceeds the 8 MiB limit");
        }
        var bytes = buffer.ToArray();
        JsonObject formula;
        try
        {
            formula = JsonNode.Parse(bytes) as JsonObject
                ?? throw Invalid("provide one pinned formula object, not an API list or cask");
        }
        catch (JsonException)
        {
            throw Invalid("invalid formula JSON; provide one pinned formula object, not an API list or cask");
        }
        var name = Text(formula, "name");
        var tap = Text(formula, "tap");
        if (!Token(name, "@+._-") || tap.Split('/').Length != 2 || !tap.Split('/').All(p => Token(p, "._-")))
            throw Invalid("invalid formula name or qualified owner/tap identity");
        if (formula.ContainsKey("token"))
            throw Invalid("casks are unsupported; provide a formula JSON object");
        var fullName = Text(formula, "full_name");
        if (fullName != $"{tap}/{name}" && !(tap == "homebrew/core" && fullName == name))
            throw Invalid("formula full_name does not match name and tap");
        foreach (var field in new[] { "recommended_dependencies", "optional_dependencies", "uses_from_macos", "requirements", "conflicts_with" })
        {
            if (formula[field] is JsonArray { Count: 0 })
                continue;
            if (!formula.ContainsKey(field))
                continue;
            throw Invalid($"unsupported {field}: optional dependencies, platform requirements and conflicts require explicit curation");
        }
        if (formula["dependencies"] is not JsonArray dependencyNames)
            throw Invalid("formula dependencies must be an array of runtime formula names");
        var dependencies = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var value in dependencyNames)
        {
            if (value is not JsonValue dependencyValue || !dependencyValue.TryGetValue<string>(out var dependency))
                throw Invalid("runtime dependency must be a formula name");
            var parts = dependency.Split('/');
            var dependencyTap = "homebrew/core";
            string dependencyName;
            if (parts.Length == 1 && Token(parts[0], "@+._-"))
                dependencyName = parts[0];
            else if (parts.Length == 3 && Token(parts[0], "._-") && Token(parts[1], "._-") && Token(parts[2], "@+._-"))
            {
                dependencyTap = parts[0] + "/" + parts[1];
                dependencyName = parts[2];
            }
            else
                throw Invalid("runtime dependency must be an exact formula or owner/tap/formula identity");
            var id = HomebrewPackageIds.Create(dependencyTap, dependencyName);
            var mapped = new JsonObject { ["PackageIdentifier"] = id };
            if (formula.ContainsKey("pinget_runtime_dependencies"))
            {
                if (formula["pinget_runtime_dependencies"] is not JsonArray pins)
                    throw Invalid("pinget_runtime_dependencies must be an array");
                var pin = pins.FirstOrDefault(pin => pin?["full_name"]?.GetValue<string>() == dependency)
                    ?? throw Invalid("curated runtime dependency metadata must cover every dependency");
                var version = Text(pin, "pkg_version");
                if (!Token(version, ".+_-"))
                    throw Invalid("invalid runtime dependency pkg_version");
                mapped["MinimumVersion"] = $"{version}.bottle.{Integer(pin, "bottle_rebuild")}";
            }
            dependencies[id] = mapped;
        }
        if (formula.ContainsKey("variations") && formula["variations"] is not JsonObject { Count: 0 })
            throw Invalid("target-specific formula variations are unsupported; provide a pinned formula without variations");
        foreach (var field in new[] { "disabled", "deprecated" })
            if (formula.ContainsKey(field) && (formula[field] is not JsonValue flag || !flag.TryGetValue<bool>(out var value) || value))
                throw Invalid("disabled or deprecated formulas are unsupported");
        var upstream = Text(formula["versions"], "stable");
        if (upstream.Length > 128 || !Token(upstream, ".+_-") || upstream.StartsWith('.'))
            throw Invalid("stable version must be a safe nonempty version token");
        var revision = Integer(formula, "revision");
        var versionScheme = Integer(formula, "version_scheme");
        if (versionScheme != 0)
            throw Invalid("offline native import currently supports version_scheme 0 only");
        if (formula["keg_only"] is not JsonValue kegValue || !kegValue.TryGetValue<bool>(out var kegOnly))
            throw Invalid("formula keg_only must be boolean");
        var aliases = Names(formula, "aliases");
        var oldNames = Names(formula, "oldnames");
        var bottle = (formula["bottle"] as JsonObject)?["stable"] as JsonObject;
        var rebuild = Integer(bottle, "rebuild");
        var selected = (bottle?["files"] as JsonObject)?[options.Tag] as JsonObject;
        if (selected is null)
            throw Invalid("pinned formula has no selected bottle; provide JSON containing the selected target");
        var sha256 = Text(selected, "sha256").ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw Invalid("selected bottle sha256 must have 64 hexadecimal digits");
        if (!PublicUrl(Text(selected, "url")))
            throw Invalid("selected bottle URL must be public HTTPS without credentials or query/fragment");
        var cellar = Text(selected, "cellar");
        if (cellar is not ("any" or "any_skip_relocation"))
            throw Invalid("fixed-cellar bottles are unsupported; select any or any_skip_relocation");
        var description = OptionalText(formula, "desc", 4096);
        var homepage = OptionalText(formula, "homepage", 2048);
        if (homepage is not null && !PublicUrl(homepage))
            throw Invalid("formula homepage must be public HTTPS without credentials, escapes, query, fragment, or dot segments");
        var license = OptionalText(formula, "license", 256);
        if (license is not null && !license.All(c => char.IsAsciiLetterOrDigit(c) || " .-+()".Contains(c)))
            throw Invalid("formula license must be an SPDX expression");
        var packageIdentifier = HomebrewPackageIds.Create(tap, name);
        var kegVersion = revision == 0 ? upstream : $"{upstream}_{revision}";
        var packageVersion = $"{kegVersion}.bottle.{rebuild}";
        using var archive = File.OpenRead(archivePath);
        if (archive.Length == 0)
            throw Invalid("bottle archive must be a nonempty regular file");
        if (Hash(archive) != sha256)
            throw Invalid("bottle SHA-256 mismatch; provide the exact archive selected by the pinned formula");
        var bottleFile = $"{sha256}.tar.gz";
        var url = $"{options.BaseUrl.TrimEnd('/')}/{bottleFile}";
        var manifestFile = $"{packageIdentifier}.{packageVersion}.yaml";
        var manifest = new JsonObject
        {
            ["PackageIdentifier"] = packageIdentifier,
            ["PackageVersion"] = packageVersion,
            ["PackageName"] = name,
            ["Publisher"] = "Unknown",
            ["PackageLocale"] = "en-US",
            ["ManifestType"] = "singleton",
            ["ManifestVersion"] = "1.10.0",
            ["Installers"] = new JsonArray(new JsonObject
            {
                ["Architecture"] = options.Architecture,
                ["Platform"] = new JsonArray(options.TargetOS),
                ["InstallerType"] = "homebrew-bottle",
                ["InstallerUrl"] = url,
                ["InstallerSha256"] = sha256,
            }),
            ["Pinget"] = new JsonObject
            {
                ["Homebrew"] = new JsonObject
                {
                    ["SchemaVersion"] = 1,
                    ["Formula"] = name,
                    ["Tap"] = tap,
                    ["UpstreamVersion"] = upstream,
                    ["Revision"] = revision,
                    ["VersionScheme"] = versionScheme,
                    ["KegOnly"] = kegOnly,
                    ["Aliases"] = aliases,
                    ["OldNames"] = oldNames,
                    ["Bottles"] = new JsonArray(new JsonObject
                    {
                        ["Url"] = url,
                        ["Sha256"] = sha256,
                        ["TargetOS"] = options.TargetOS,
                        ["Architecture"] = options.Architecture,
                        ["BottleTag"] = options.Tag,
                        ["Rebuild"] = rebuild,
                        ["Relocation"] = cellar,
                    }),
                },
            },
        };
        var bottleManifest = manifest["Pinget"]!["Homebrew"]!["Bottles"]![0]!.AsObject();
        if (options.TargetOS == "Linux")
        {
            var minimum = Text(selected, "minimum_glibc");
            if (!Version.TryParse(minimum, out _) || Text(selected, "loader") != "glibc")
                throw Invalid("Linux bottle requires curated minimum_glibc and loader=glibc from its OCI receipt metadata");
            bottleManifest["MinimumGlibc"] = minimum;
            bottleManifest["Loader"] = "glibc";
        }
        else
            bottleManifest["MinimumOSVersion"] = "14.0";
        if (dependencies.Count > 0)
            manifest["Installers"]![0]!["Dependencies"] = new JsonObject
            {
                ["PackageDependencies"] = new JsonArray(dependencies.Values.Select(value => (JsonNode)value.DeepClone()).ToArray()),
            };
        if (description is not null)
            manifest["ShortDescription"] = description;
        if (homepage is not null)
            manifest["PackageUrl"] = homepage;
        if (license is not null)
            manifest["License"] = license;
        var manifestBytes = Encoding.UTF8.GetBytes(YamlEmitter.EmitDocument(manifest));
        Repository.ParseYamlManifest(manifestBytes);
        var report = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["packageIdentifier"] = packageIdentifier,
            ["packageVersion"] = packageVersion,
            ["formulaJsonSha256"] = Hash(bytes),
            ["bottleSha256"] = sha256,
            ["bottleTag"] = options.Tag,
            ["bottleFile"] = bottleFile,
            ["manifestFile"] = manifestFile,
            ["dependencyPolicy"] = "mapped-runtime-dependencies",
            ["runtimeDependencies"] = new JsonArray(dependencies.Values.Select(value => (JsonNode)value.DeepClone()).ToArray()),
        };
        var reportBytes = Encoding.UTF8.GetBytes(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var manifestPath = Path.Combine(output, manifestFile);
        var reportPath = Path.Combine(output, $"{manifestFile}.import.json");
        var bottlePath = Path.Combine(mirror, bottleFile);
        var haveBottle = CheckExisting(bottlePath, sha256);
        var haveManifest = CheckExisting(manifestPath, Hash(manifestBytes));
        var haveReport = CheckExisting(reportPath, Hash(reportBytes));
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(mirror);
        if (!haveBottle)
        {
            archive.Position = 0;
            using (var destination = new FileStream(bottlePath, FileMode.CreateNew, FileAccess.Write))
            {
                archive.CopyTo(destination);
                destination.Flush(true);
            }
            bool matches;
            using (var copied = File.OpenRead(bottlePath))
                matches = Hash(copied) == sha256;
            if (!matches)
            {
                File.Delete(bottlePath);
                throw Invalid("bottle changed during mirroring; no manifest was written");
            }
        }
        if (!haveManifest)
            WriteNew(manifestPath, manifestBytes);
        if (!haveReport)
            WriteNew(reportPath, reportBytes);
        return report;
    }

    private static PublisherException Invalid(string message) => new(message);

    private static string Text(JsonNode? value, string field)
    {
        if (value is JsonObject obj && obj[field] is JsonValue node && node.TryGetValue<string>(out var text)
            && text.Length is > 0 and <= 256 && !text.Any(char.IsControl))
            return text;
        throw Invalid($"formula JSON requires a nonempty string: {field}");
    }

    private static string? OptionalText(JsonObject formula, string field, int maxLength)
    {
        if (!formula.TryGetPropertyValue(field, out var value) || value is null)
            return null;
        if (value is JsonValue node && node.TryGetValue<string>(out var text) && text is not null)
        {
            if (text.Length == 0)
                return null;
            if (text.Length <= maxLength && !text.Any(char.IsControl))
                return text;
        }
        throw Invalid($"formula JSON field must be a string of at most {maxLength} characters: {field}");
    }

    private static uint Integer(JsonNode? value, string field)
    {
        if (value is JsonObject obj && obj[field] is JsonValue node && node.TryGetValue<uint>(out var number))
            return number;
        throw Invalid($"formula JSON requires an unsigned 32-bit integer: {field}");
    }

    private static bool Token(string value, string extra) =>
        value.Length > 0 && value.Any(char.IsAsciiLetterOrDigit)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || extra.Contains(c));

    // Excludes credentials and signed URLs rather than attempting to redact secrets after import.
    private static bool PublicUrl(string value)
    {
        if (!value.StartsWith("https://", StringComparison.Ordinal))
            return false;
        var parts = value[8..].Split('/', 2);
        var authority = parts[0].Split(':');
        var path = parts.Length == 2 ? parts[1] : "";
        return authority.Length <= 2 && authority[0].Length > 0
            && authority[0].Split('.').All(p => Token(p, "-") && !p.StartsWith('-') && !p.EndsWith('-'))
            && (authority.Length == 1 || (ushort.TryParse(authority[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port) && port > 0))
            && path.All(c => char.IsAsciiLetterOrDigit(c) || "-._~/:".Contains(c))
            && !path.Split('/').Any(p => p is "." or "..");
    }

    private static JsonArray Names(JsonObject formula, string field)
    {
        if (formula[field] is not JsonArray list)
            throw Invalid($"formula {field} must be an array");
        var values = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var name) || !Token(name, "@+._-"))
                throw Invalid("formula aliases and oldnames must contain formula names");
            values.Add(name);
        }
        return new JsonArray(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    private static string SafePath(string path)
    {
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
            throw Invalid("input/output paths must not contain '..'");
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("input/output paths must not contain symbolic links or reparse points");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootLength = Path.GetPathRoot(full)?.Length ?? 0;
        return trimmed.Length >= rootLength ? trimmed : full;
    }

    private static bool Within(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(directory, comparison)
            || path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    private static bool CheckExisting(string path, string expectedHash)
    {
        SafePath(path);
        if (Directory.Exists(path))
            throw Invalid("destination must be a regular file");
        if (!File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        if (Hash(stream) != expectedHash)
            throw Invalid("destination already exists with different bytes; use a clean output directory");
        return true;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
