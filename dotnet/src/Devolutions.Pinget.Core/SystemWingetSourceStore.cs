using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Devolutions.Pinget.Core;

internal static class SystemWingetSourceStore
{
    internal const string ProgramEnvironmentVariable = "PINGET_WINGET_PATH";

    private const string AppModelPackagesPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string AppInstallerFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    internal static string ProgramName { get; } = OperatingSystem.IsWindows() ? "winget.exe" : "winget";

    internal static Func<IReadOnlyList<string>, WingetCommandResult> CommandRunner { get; set; } = RunWinget;

    internal static bool IsSecureSettingsStub(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.TrimStart().StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase);

    internal static SourceStore Load()
    {
        var result = RunChecked(BuildExportArguments(), "export WinGet sources");
        return new SourceStore { Sources = ParseExport(result.Stdout) };
    }

    internal static void AddSource(string name, string arg, SourceKind kind, string trustLevel, bool explicitSource)
    {
        RunChecked(BuildAddArguments(name, arg, kind, trustLevel, explicitSource), $"add WinGet source '{name}'");
    }

    internal static void RemoveSource(string name)
    {
        RunChecked(["source", "remove", "--name", name, "--disable-interactivity"], $"remove WinGet source '{name}'");
    }

    internal static void ResetSource(string name)
    {
        RunChecked(["source", "reset", "--name", name, "--force", "--disable-interactivity"], $"reset WinGet source '{name}'");
    }

    internal static void ResetSources()
    {
        RunChecked(["source", "reset", "--force", "--disable-interactivity"], "reset WinGet sources");
    }

    internal static string UpdateSources(string? name)
    {
        var args = new List<string> { "source", "update" };
        if (!string.IsNullOrWhiteSpace(name))
        {
            args.Add("--name");
            args.Add(name);
        }

        args.Add("--disable-interactivity");
        var result = RunChecked(args, string.IsNullOrWhiteSpace(name) ? "update WinGet sources" : $"update WinGet source '{name}'");
        return string.IsNullOrWhiteSpace(result.Stdout) ? "Done" : result.Stdout.Trim();
    }

    internal static IReadOnlyList<string> BuildExportArguments() =>
        ["source", "export", "--disable-interactivity"];

    internal static IReadOnlyList<string> BuildAddArguments(
        string name,
        string arg,
        SourceKind kind,
        string trustLevel,
        bool explicitSource)
    {
        var args = new List<string>
        {
            "source",
            "add",
            "--name",
            name,
            "--arg",
            arg,
            "--type",
            FormatSourceType(kind),
            "--disable-interactivity",
        };

        if (!string.IsNullOrWhiteSpace(trustLevel))
        {
            args.Add("--trust-level");
            args.Add(trustLevel.Equals("Trusted", StringComparison.OrdinalIgnoreCase) ? "trusted" : "none");
        }

        if (explicitSource)
            args.Add("--explicit");

        return args;
    }

    internal static List<SourceRecord> ParseExport(string output)
    {
        var trimmed = output.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return [];

        if (TryParseJsonSources(trimmed, out var sources))
            return sources;

        var records = new List<SourceRecord>();
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (!TryParseJsonSource(line, out var source))
                throw new InvalidOperationException($"WinGet source export returned a non-source line: {line}");

            records.Add(source);
        }

        return records;
    }

    private static WingetCommandResult RunChecked(IReadOnlyList<string> args, string action)
    {
        var result = CommandRunner(args);
        if (result.ExitCode == 0)
            return result;

        var detail = string.Join(Environment.NewLine, new[] { result.Stderr, result.Stdout }.Where(text => !string.IsNullOrWhiteSpace(text)));
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Failed to {action}; winget exited with code {result.ExitCode}."
                : $"Failed to {action}; winget exited with code {result.ExitCode}:{Environment.NewLine}{detail.Trim()}");
    }

    private static WingetCommandResult RunWinget(IReadOnlyList<string> args)
    {
        var program = ResolveProgram();
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                $"Failed to run the WinGet source command with {program}: {ex.Message}", ex);
        }

        using var process = started
            ?? throw new InvalidOperationException($"Failed to run the WinGet source command with {program}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new WingetCommandResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    internal static string ResolveProgram() =>
        ResolveProgram(
            Environment.GetEnvironmentVariable(ProgramEnvironmentVariable),
            CurrentExecutableDirectory(),
            Environment.GetEnvironmentVariable("PATH"),
            FallbackDirectories,
            File.Exists);

    private static string? CurrentExecutableDirectory()
    {
        var executable = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executable) ? AppContext.BaseDirectory : Path.GetDirectoryName(executable);
    }

    /// <summary>
    /// WinGet ships as an App Execution Alias, so a host that inherited a PATH without
    /// %LOCALAPPDATA%\Microsoft\WindowsApps cannot spawn it by name at all. Look past the PATH
    /// before giving up, and let a host that already knows the location say so.
    /// <para>
    /// The directory of the running executable keeps the precedence it had while this started
    /// <c>winget</c> by name: Windows resolves a bare program name against the application
    /// directory before the PATH, and a host that ships its own copy relies on that.
    /// </para>
    /// </summary>
    internal static string ResolveProgram(
        string? configured,
        string? applicationDirectory,
        string? searchPath,
        Func<IEnumerable<string>> fallbackDirectories,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (fileExists(configured))
                return configured;

            var nested = Path.Combine(configured, ProgramName);
            if (fileExists(nested))
                return nested;

            throw new InvalidOperationException(
                $"{ProgramEnvironmentVariable} is set to {configured}, where no {ProgramName} was found.");
        }

        if (!string.IsNullOrWhiteSpace(applicationDirectory) &&
            FirstProgramIn([applicationDirectory], fileExists) is { } bundled)
        {
            return bundled;
        }

        var searchDirectories = (searchPath ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(entry => entry.Length is not 0);

        if (FirstProgramIn(searchDirectories, fileExists) is { } onPath)
            return onPath;

        // Enumerating the App Installer package locations reads the registry, so it stays
        // behind the PATH: the machines that already resolve winget by name pay nothing.
        if (FirstProgramIn(fallbackDirectories(), fileExists) is { } offPath)
            return offPath;

        throw new InvalidOperationException(
            $"{ProgramName} was not found on the PATH or in the App Installer install locations. " +
            $"Set {ProgramEnvironmentVariable} to its full path, or install the App Installer.");
    }

    private static string? FirstProgramIn(IEnumerable<string> directories, Func<string, bool> fileExists)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, ProgramName);
            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> FallbackDirectories()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var directories = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            directories.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));

        directories.AddRange(AppInstallerPackageDirectories());
        return directories;
    }

    private static List<string> AppInstallerPackageDirectories()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(AppModelPackagesPath);
            if (packages is null)
                return [];

            var found = new List<(string Version, string Directory)>();
            foreach (var packageFullName in packages.GetSubKeyNames())
            {
                if (ParsePackageFullName(packageFullName) is not { } parsed ||
                    !string.Equals(parsed.FamilyName, AppInstallerFamilyName, StringComparison.OrdinalIgnoreCase) ||
                    parsed.ResourceId.StartsWith("split.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var entry = packages.OpenSubKey(packageFullName);
                if (entry?.GetValue("PackageRootFolder") is not string directory ||
                    string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                found.Add((parsed.Version, directory));
            }

            found.Sort((left, right) => -RestSource.CompareVersionStrings(left.Version, right.Version));
            return found.Select(match => match.Directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    internal static (string Version, string FamilyName, string ResourceId)? ParsePackageFullName(string packageFullName)
    {
        var segments = packageFullName.Split('_');
        if (segments.Length < 5)
            return null;

        var name = string.Join("_", segments[..^4]);
        var version = segments[^4].Trim();
        var resourceId = segments[^2].Trim();
        var publisherId = segments[^1].Trim();
        if (name.Length is 0 || version.Length is 0 || publisherId.Length is 0)
            return null;

        var familyName = resourceId.Length is 0 ? $"{name}_{publisherId}" : $"{name}_{resourceId}_{publisherId}";
        return (version, familyName, resourceId);
    }

    private static bool TryParseJsonSources(string json, out List<SourceRecord> sources)
    {
        sources = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var sourceElement in root.EnumerateArray())
                    sources.Add(ParseSourceElement(sourceElement));
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Sources", out var sourcesElement) &&
                sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var sourceElement in sourcesElement.EnumerateArray())
                    sources.Add(ParseSourceElement(sourceElement));
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Name", out _))
            {
                sources.Add(ParseSourceElement(root));
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseJsonSource(string json, out SourceRecord source)
    {
        source = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("Name", out _))
            {
                return false;
            }

            source = ParseSourceElement(doc.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SourceRecord ParseSourceElement(JsonElement element)
    {
        var name = GetRequiredString(element, "Name");
        var type = GetRequiredString(element, "Type");
        var kind = ParseSourceKind(type);
        var arg = GetRequiredString(element, "Arg");
        var identifier = GetOptionalString(element, "Identifier") ??
                         GetOptionalString(element, "Data") ??
                         name;

        return new SourceRecord
        {
            Name = name,
            Kind = kind,
            Arg = arg,
            Identifier = identifier,
            TrustLevel = ParseTrustLevel(element),
            Explicit = GetOptionalBool(element, "Explicit"),
            Priority = GetOptionalInt(element, "Priority") ?? 0,
        };
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName) ??
        throw new InvalidOperationException($"WinGet source export did not include '{propertyName}'.");

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool GetOptionalBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static SourceKind ParseSourceKind(string type) =>
        type.Equals("Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase)
            ? SourceKind.PreIndexed
            : type.Equals("Microsoft.Rest", StringComparison.OrdinalIgnoreCase)
                ? SourceKind.Rest
                : throw new InvalidOperationException($"Unsupported WinGet source type '{type}'.");

    private static string FormatSourceType(SourceKind kind) => kind switch
    {
        SourceKind.PreIndexed => "Microsoft.PreIndexed.Package",
        SourceKind.Rest => "Microsoft.Rest",
        _ => throw new InvalidOperationException($"Unsupported source kind '{kind}'."),
    };

    private static string ParseTrustLevel(JsonElement element)
    {
        if (!element.TryGetProperty("TrustLevel", out var trustLevel))
            return "None";

        return trustLevel.ValueKind switch
        {
            JsonValueKind.Array when trustLevel.EnumerateArray().Any(IsTrustedValue) => "Trusted",
            JsonValueKind.String when IsTrustedValue(trustLevel) => "Trusted",
            JsonValueKind.Number when trustLevel.TryGetInt32(out var value) && value > 0 => "Trusted",
            _ => "None",
        };
    }

    private static bool IsTrustedValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Equals("Trusted", StringComparison.OrdinalIgnoreCase) == true;
}

internal sealed record WingetCommandResult(int ExitCode, string Stdout, string Stderr);
