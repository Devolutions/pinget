using System.Text.Json;
using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli;

internal sealed record ImportPackage(string Id, string? SourceName, string? Version, JsonElement Entry = default);

internal static class ImportCommandHandler
{
    /// <summary>
    /// Reads a WinGet import file. A malformed file is reported as <see cref="InvalidDataException"/>
    /// with a clear message instead of a raw JSON/KeyNotFound exception.
    /// </summary>
    internal static List<ImportPackage> ParseImportFile(string json, bool ignoreVersions)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid import file: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("Sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Invalid import file: missing 'Sources' array");
            }

            var packages = new List<ImportPackage>();
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Invalid import file: each 'Sources' entry must be an object");

                var sourceName = source.TryGetProperty("SourceDetails", out var sourceDetails) && sourceDetails.ValueKind == JsonValueKind.Object
                    ? GetJsonString(sourceDetails, "Name")
                    : null;
                if (!source.TryGetProperty("Packages", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    var id = entry.ValueKind == JsonValueKind.Object ? GetJsonString(entry, "PackageIdentifier") : null;
                    if (string.IsNullOrWhiteSpace(id))
                        throw new InvalidDataException("Invalid import file: package entry is missing 'PackageIdentifier'");

                    var version = ignoreVersions ? null : GetJsonString(entry, "Version") ?? GetJsonString(entry, "PackageVersion");
                    // Clone so the entry outlives the JsonDocument disposed below.
                    packages.Add(new ImportPackage(id, sourceName, version, entry.Clone()));
                }
            }

            return packages;
        }
    }

    internal static int Run(
        IEnumerable<ImportPackage> packages,
        bool dryRun,
        bool ignoreUnavailable,
        bool noUpgrade,
        Func<ImportPackage, bool> isInstalled,
        Func<ImportPackage, InstallResult> install,
        Func<ImportPackage, bool>? isAvailable = null)
    {
        int total = 0;
        int found = 0;
        int skipped = 0;
        bool hasFailures = false;

        foreach (var package in packages)
        {
            if (dryRun)
            {
                // Like the Rust CLI, a dry run resolves each package against its source so a
                // missing package shows up before anything is installed.
                bool available;
                try
                {
                    available = isAvailable?.Invoke(package) ?? true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error searching for {package.Id}: {ex.Message}");
                    available = false;
                }

                if (available)
                {
                    Console.WriteLine($"[dry-run] Would install: {package.Id}");
                    found++;
                }
                else if (ignoreUnavailable)
                {
                    Console.WriteLine($"[dry-run] Ignoring unavailable package: {package.Id}");
                }
                else
                {
                    Console.WriteLine($"[dry-run] Not found: {package.Id}");
                }
            }
            else
            {
                try
                {
                    if (noUpgrade && isInstalled(package))
                    {
                        Console.WriteLine($"[no-upgrade] Skipping already installed package: {package.Id}");
                        skipped++;
                    }
                    else
                    {
                        Console.Write($"Installing {package.Id}...");
                        var result = install(package);
                        if (result.NoOp)
                        {
                            Console.WriteLine(" no-op");
                            PrintWarnings(result.Warnings);
                            skipped++;
                        }
                        else
                        {
                            PrintWarnings(result.Warnings);
                            Console.WriteLine(result.Success ? " done" : $" failed (exit {result.ExitCode})");
                            hasFailures |= !result.Success;
                        }
                    }
                }
                catch (Exception ex) when (ignoreUnavailable && CanIgnoreUnavailableImportFailure(ex))
                {
                    Console.WriteLine(" unavailable");
                    CliSession.WriteWarning($"Skipping unavailable package '{package.Id}': {ex.Message}");
                    skipped++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($" error: {ex.Message}");
                    hasFailures = true;
                }
            }
            total++;
        }

        if (!dryRun && skipped > 0)
            Console.WriteLine($"Skipped {skipped} package(s).");
        Console.WriteLine(dryRun
            ? $"{found} of {total} package(s) would be installed."
            : $"{total} package(s) processed.");
        return hasFailures ? 1 : 0;
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool CanIgnoreUnavailableImportFailure(Exception ex) =>
        ex is InvalidOperationException &&
        (ex.Message.Contains("No package matched the query", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("No package matched the supplied query", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("No applicable installer found", StringComparison.OrdinalIgnoreCase));

    private static void PrintWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
            CliSession.WriteWarning(warning);
    }
}
