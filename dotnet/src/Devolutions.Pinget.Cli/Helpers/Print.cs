using Devolutions.Pinget.Core;

namespace Devolutions.Pinget.Cli.Helpers;

internal static class Print
{
    internal static void Info()
    {
        Version();
        Console.WriteLine("Pure C# subset of Pinget (portable winget)");
        Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }

    internal static void Version() => Console.WriteLine($"pinget v{Consts.Version}");

    internal static void Search(SearchResponse result)
    {
        Warnings(result.Warnings);
        if (result.Matches.Count == 0) { Console.WriteLine("No package matched the supplied query."); return; }

        bool showMatch = result.Matches.Any(m => m.MatchCriteria is not null);
        if (showMatch)
        {
            Console.WriteLine("{0,-32} {1,-40} {2,-18} {3,-24} Source", "Name", "Id", "Version", "Match");
            foreach (var m in result.Matches)
            {
                Console.WriteLine(
                    "{0,-32} {1,-40} {2,-18} {3,-24} {4}",
                    Trunc(m.Name, 32),
                    Trunc(m.Id, 40),
                    m.Version ?? "Unknown",
                    Trunc(m.MatchCriteria ?? "", 24),
                    m.SourceName);
            }
        }
        else
        {
            Console.WriteLine("{0,-36} {1,-42} {2,-18} Source", "Name", "Id", "Version");
            foreach (var m in result.Matches)
            {
                Console.WriteLine(
                    "{0,-36} {1,-42} {2,-18} {3}",
                    Trunc(m.Name, 36),
                    Trunc(m.Id, 42),
                    m.Version ?? "Unknown",
                    m.SourceName);
            }
        }

        if (result.Truncated) Console.WriteLine($"<additional entries truncated due to result limit>");
    }

    internal static void Warnings(List<string> warnings) { foreach (var w in warnings) Console.Error.WriteLine($"warning: {w}"); }
    
    internal static void Table(string[] headers, List<string[]> rows)
    {
        if (headers.Length == 0) return;
        int cols = headers.Length;
        var widths = headers.Select(h => h.Length).ToArray();
        var hasData = new bool[cols];

        foreach (var row in rows)
            for (int i = 0; i < Math.Min(cols, row.Length); i++)
                if (!string.IsNullOrEmpty(row[i]))
                {
                    hasData[i] = true;
                    widths[i] = Math.Max(widths[i], row[i].Length);
                }

        for (int i = 0; i < cols; i++)
            if (!hasData[i]) widths[i] = 0;

        var spaceAfter = Enumerable.Repeat(true, cols).ToArray();
        spaceAfter[^1] = false;
        for (int i = cols - 1; i >= 1; i--)
        {
            if (widths[i] == 0) spaceAfter[i - 1] = false;
            else break;
        }

        int totalWidth = widths.Zip(spaceAfter, (w, s) => w + (s ? 1 : 0)).Sum();
        int consoleWidth = 119;
        try { consoleWidth = Math.Max(1, Console.WindowWidth - 2); } catch { }
        if (totalWidth >= consoleWidth)
        {
            int extra = totalWidth - consoleWidth + 1;
            while (extra > 0)
            {
                int target = 0;
                for (int i = 1; i < cols; i++)
                    if (widths[i] > widths[target]) target = i;
                if (widths[target] > 1) widths[target]--;
                extra--;
            }
            totalWidth = Math.Max(0, consoleWidth - 1);
        }

        TableLine(headers, widths, spaceAfter);
        Console.WriteLine(new string('-', totalWidth));
        foreach (var row in rows)
            TableLine(row, widths, spaceAfter);
    }

    internal static void Versions(VersionsResult result)
    {
        Warnings(result.Warnings);
        Console.WriteLine($"Found {result.Package.Name} [{result.Package.Id}]");
        Console.WriteLine("Version");
        Console.WriteLine(new string('-', 40));
        foreach (var v in result.Versions)
        {
            Console.Write(v.Version);
            if (!string.IsNullOrEmpty(v.Channel)) Console.Write($" [{v.Channel}]");
            Console.WriteLine();
        }
    }

    internal static void Show(ShowResult result)
    {
        Warnings(result.Warnings);
        Console.WriteLine($"Found {result.Package.Name} [{result.Package.Id}]");
        var m = result.Manifest;
        Console.WriteLine($"Version: {m.Version}");
        PrintOpt("Publisher", m.Publisher);
        PrintOpt("Publisher Url", m.PublisherUrl);
        PrintOpt("Publisher Support Url", m.PublisherSupportUrl);
        PrintOpt("Author", m.Author);
        PrintOpt("Moniker", m.Moniker);
        if (m.Description is not null)
        {
            Console.WriteLine("Description:");
            foreach (var line in m.Description.Split('\n'))
                Console.WriteLine($"  {line.TrimEnd()}");
        }
        PrintOpt("Homepage", m.PackageUrl);
        PrintOpt("License", m.License);
        PrintOpt("License Url", m.LicenseUrl);
        PrintOpt("Privacy Url", m.PrivacyUrl);
        PrintOpt("Copyright", m.Copyright);
        PrintOpt("Copyright Url", m.CopyrightUrl);
        PrintOpt("Release Notes Url", m.ReleaseNotesUrl);
        if (m.Documentation.Count > 0)
        {
            Console.WriteLine("Documentation:");
            foreach (var doc in m.Documentation)
                Console.WriteLine($"  {doc.Label ?? "Link"}: {doc.Url}");
        }

        if (result.Manifest.PackageDependencies.Count > 0)
        {
            Console.Write("Dependencies:");
            Console.WriteLine($" {string.Join(", ", result.Manifest.PackageDependencies)}");
        }

        if (m.Tags.Count > 0) { Console.WriteLine("Tags:"); foreach (var t in m.Tags) Console.WriteLine($"  {t}"); }

        if (result.SelectedInstaller is Installer inst)
        {
            Console.WriteLine("Installer:");
            PrintOpt("  Type", inst.InstallerType);
            PrintOpt("  Architecture", inst.Architecture);
            PrintOpt("  Locale", inst.Locale);
            PrintOpt("  Scope", inst.Scope);
            PrintOpt("  Url", inst.Url);
            PrintOpt("  Sha256", inst.Sha256);
            PrintOpt("  ProductCode", inst.ProductCode);
            PrintOpt("  ReleaseDate", inst.ReleaseDate);
        }
        else if (m.Installers.Count > 0)
        {
            Console.WriteLine("Installer:");
            Console.WriteLine("  No applicable installer found; see logs for more details.");
        }
    }

    internal static void ListResult(ListResponse result, bool details, bool upgrade)
    {
        Warnings(result.Warnings);
        if (result.Matches.Count == 0) { Console.WriteLine("No installed package found matching input criteria."); return; }

        if (details)
        {
            int total = result.Matches.Count;
            for (int idx = 0; idx < total; idx++)
            {
                var m = result.Matches[idx];
                if (total > 1)
                    Console.WriteLine($"({idx + 1}/{total}) {m.Name} [{m.Id}]");
                else
                    Console.WriteLine($"{m.Name} [{m.Id}]");
                PrintOpt("Version", m.InstalledVersion);
                PrintOpt("Publisher", m.Publisher);
                if (m.LocalId != m.Id) PrintOpt("Local Identifier", m.LocalId);
                PrintOpt("Source", m.SourceName);
                PrintOpt("Available", m.AvailableVersion);
            }
        }
        else
        {
            bool showAvailable = result.Matches.Any(m => !string.IsNullOrEmpty(m.AvailableVersion));
            string[] headers = showAvailable
                ? ["Name", "Id", "Version", "Available", "Source"]
                : ["Name", "Id", "Version", "Source"];
            var rows = result.Matches.Select(m => showAvailable
                ? new[] { m.Name, m.Id, m.InstalledVersion, m.AvailableVersion ?? "", m.SourceName ?? "" }
                : new[] { m.Name, m.Id, m.InstalledVersion, m.SourceName ?? "" }).ToList();
            Table(headers, rows);
        }

        if (result.Truncated) Console.WriteLine($"<additional entries truncated due to result limit>");
        if (upgrade) Console.WriteLine($"{result.Matches.Count} upgrades available.");
    }

    internal static void Sources(List<SourceRecord> sources)
    {
        Console.WriteLine($"{"Name",-12} {"Trust",-8} {"Explicit",-8} Argument");
        foreach (var s in sources)
            Console.WriteLine($"{s.Name,-12} {s.TrustLevel,-8} {s.Explicit.ToString().ToLowerInvariant(),-8} {s.Arg}");
    }

    internal static void PackageActionResult(InstallResult result, string action, string actionPastTense)
    {
        Warnings(result.Warnings);
        var target = string.IsNullOrWhiteSpace(result.Version)
            ? result.PackageId
            : $"{result.PackageId} v{result.Version}";
        if (result.NoOp)
            Console.WriteLine($"No changes were made for {target}.");
        else if (result.Success)
            Console.WriteLine($"Successfully {actionPastTense} {target}");
        else
            Console.Error.WriteLine($"Failed to {action} {target} (exit code: {result.ExitCode})");
    }

    internal static void ErrorLookup(string input)
    {
        if (!long.TryParse(input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? input[2..] : input, System.Globalization.NumberStyles.HexNumber, null, out var code))
        {
            Console.Error.WriteLine($"error: Could not parse '{input}' as an error code");
            return;
        }

        var lookup = LookupHresult(code);
        if (lookup is not null)
        {
            // APPINSTALLER codes (0x8A15xxxx): show symbol on same line
            if ((code & 0xFFFF0000L) == unchecked((long)0x8A150000))
                Console.WriteLine($"0x{code:x8} : {lookup.Value.Symbol}");
            else
                Console.WriteLine($"0x{code:x8}");
            Console.WriteLine(lookup.Value.Description);
        }
        else
        {
            Console.WriteLine($"0x{code:x8}");
            Console.WriteLine("  Unknown error code");
        }
    }

    static void TableLine(string[] values, int[] widths, bool[] spaceAfter)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(values.Length, widths.Length); i++)
        {
            if (widths[i] == 0) continue;
            var val = values[i] ?? "";
            if (val.Length > widths[i])
            {
                sb.Append(Trunc(val, widths[i]));
                if (spaceAfter[i]) sb.Append(' ');
            }
            else
            {
                sb.Append(val);
                if (spaceAfter[i]) sb.Append(' ', widths[i] - val.Length + 1);
            }
        }
        Console.WriteLine(sb.ToString().TrimEnd());
    }

    static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + ".";

    static void PrintOpt(string label, string? value) { if (value is not null) Console.WriteLine($"{label}: {value}"); }

    static (string Symbol, string Description)? LookupHresult(long code)
    {
        return (code & 0xFFFF0000L) switch
        {
            0x8A150000 => (code & 0xFFFF) switch
            {
                0x0001 => ("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "An unexpected error occurred."),
                0x0002 => ("APPINSTALLER_CLI_ERROR_INVALID_CL_ARGUMENTS", "Invalid command line arguments."),
                0x0003 => ("APPINSTALLER_CLI_ERROR_COMMAND_FAILED", "The command failed."),
                0x0004 => ("APPINSTALLER_CLI_ERROR_MANIFEST_FAILED", "Opening the manifest failed."),
                0x0007 => ("APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER", "No applicable installer found."),
                0x000E => ("APPINSTALLER_CLI_ERROR_PACKAGE_NOT_FOUND", "No package matched the query."),
                0x0010 => ("APPINSTALLER_CLI_ERROR_SOURCE_NAME_ALREADY_EXISTS", "A source with the given name already exists."),
                0x0012 => ("APPINSTALLER_CLI_ERROR_NO_SOURCES_DEFINED", "No sources are configured."),
                0x0013 => ("APPINSTALLER_CLI_ERROR_MULTIPLE_APPLICATIONS_FOUND", "Multiple packages matched the query."),
                0x0016 => ("APPINSTALLER_CLI_ERROR_NO_APPLICABLE_UPGRADE", "No applicable upgrade found."),
                _ => null,
            },
            _ => code switch
            {
                unchecked(0x80004005) => ("E_FAIL", "Unspecified error"),
                unchecked(0x80070005) => ("E_ACCESSDENIED", "General access denied error"),
                unchecked(0x80070057) => ("E_INVALIDARG", "One or more arguments are not valid"),
                unchecked(0x8007000E) => ("E_OUTOFMEMORY", "Failed to allocate necessary memory"),
                _ => null,
            }
        };
    }
}
