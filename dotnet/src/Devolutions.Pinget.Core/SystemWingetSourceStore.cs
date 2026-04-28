using System.Diagnostics;
using System.Text.Json;

namespace Devolutions.Pinget.Core;

internal static class SystemWingetSourceStore
{
    private const string WingetExecutable = "winget";

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

        return OrderSources(records);
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
        var psi = new ProcessStartInfo(WingetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start winget.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new WingetCommandResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
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
                sources = OrderSources(sources);
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Sources", out var sourcesElement) &&
                sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var sourceElement in sourcesElement.EnumerateArray())
                    sources.Add(ParseSourceElement(sourceElement));
                sources = OrderSources(sources);
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

    private static List<SourceRecord> OrderSources(List<SourceRecord> sources) =>
        sources
            .OrderBy(source => source.Explicit)
            .ThenByDescending(source => source.Priority)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal sealed record WingetCommandResult(int ExitCode, string Stdout, string Stderr);
