using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Devolutions.Pinget.Core;

internal sealed record AgentSkillManifest(
    string Id, string Version, string Name, string? Publisher,
    Uri ArtifactUrl, string ArtifactSha256, string? SkillPath,
    AgentSkillSource? Source = null, string? PluginPath = null);

internal sealed record AgentSkillSource(
    string Type, Uri? Url = null, string? Sha256 = null, string? Format = null,
    string? Commit = null, Guid? Id = null, string? VersionId = null);

internal static partial class AgentSkillInstaller
{
    private const int MaxArchiveBytes = 10 * 1024 * 1024;
    private const long MaxEntryBytes = 25L * 1024 * 1024;
    private const long MaxExtractedBytes = 25L * 1024 * 1024;
    private const int MaxEntries = 1000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex SkillNamePattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PluginNamePattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*){2,}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> PluginFields = new(StringComparer.Ordinal)
    {
        "$schema", "name", "version", "description", "author", "homepage",
        "repository", "license", "keywords", "extensions",
    };
    private const string PluginSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    private const string DiscoverySchema = "https://schemas.agentskills.io/discovery/0.2.0/schema.json";

    internal static AgentSkillManifest? TryParse(byte[] bytes)
    {
        var documents = Repository.ParseYamlManifestDocuments(bytes);
        if (documents is not Dictionary<string, object?> doc)
        {
            if (documents is List<Dictionary<string, object?>> list &&
                list.Any(d => d.TryGetValue("ManifestType", out var type) &&
                              string.Equals(type as string, "pinget.agentSkill", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("AgentSkill manifests must contain exactly one YAML document.");
            return null;
        }

        if (!doc.TryGetValue("ManifestType", out var kind) ||
            !string.Equals(kind as string, "pinget.agentSkill", StringComparison.OrdinalIgnoreCase))
        {
            if (doc.ContainsKey("AgentSkill") || doc.ContainsKey("PingetManifestVersion"))
                throw new InvalidOperationException("AgentSkill fields require ManifestType: pinget.agentSkill.");
            return null;
        }

        static string Required(Dictionary<string, object?> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"AgentSkill manifest requires a nonempty {key} string.");
            return text.Trim();
        }

        if (Required(doc, "PingetManifestVersion") != "1")
            throw new InvalidOperationException("Unsupported PingetManifestVersion; expected 1.");
        var id = Required(doc, "PackageIdentifier");
        var version = Required(doc, "PackageVersion");
        var name = Required(doc, "PackageName");
        if (doc["PackageName"] is not string originalName || originalName != name ||
            name.Length is < 1 or > 64 || !SkillNamePattern.IsMatch(name))
            throw new InvalidOperationException("AgentSkill PackageName must be a valid skill slug.");
        var publisher = doc.TryGetValue("Publisher", out var publisherValue) && publisherValue is not null
            ? Required(doc, "Publisher") : null;
        if (!doc.TryGetValue("AgentSkill", out var skillValue) || skillValue is not Dictionary<string, object?> skill)
            throw new InvalidOperationException("AgentSkill must be a YAML mapping.");
        if (skill.Keys.Except(new[] { "Source", "ArtifactUrl", "ArtifactSha256", "SkillPath", "PluginPath" },
                StringComparer.Ordinal).Any())
            throw new InvalidOperationException("AgentSkill contains unsupported fields.");
        var hasSource = skill.ContainsKey("Source");
        if (hasSource && (skill.ContainsKey("ArtifactUrl") || skill.ContainsKey("ArtifactSha256")))
            throw new InvalidOperationException("AgentSkill Source and legacy ArtifactUrl/ArtifactSha256 are mutually exclusive.");
        AgentSkillSource? source = null;
        Uri? uri = null;
        string? digest = null;
        if (hasSource)
        {
            if (skill["Source"] is not Dictionary<string, object?> sourceMap)
                throw new InvalidOperationException("AgentSkill Source must be a mapping.");
            var type = Required(sourceMap, "Type");
            var fields = type switch
            {
                "archive" => new[] { "Type", "Url", "Sha256", "Format" },
                "git" => new[] { "Type", "Url", "Commit" },
                "notion-skill" or "notion-plugin" => new[] { "Type", "Id", "VersionId" },
                "well-known" => new[] { "Type", "IndexUrl", "Sha256" },
                _ => throw new InvalidOperationException("Unsupported AgentSkill Source Type."),
            };
            if (sourceMap.Keys.Except(fields, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("AgentSkill Source has fields not valid for its Type.");
            switch (type)
            {
                case "archive":
                    uri = HttpsUrl(Required(sourceMap, "Url"));
                    digest = Sha256(Required(sourceMap, "Sha256"));
                    var format = Required(sourceMap, "Format");
                    if (format is not ("zip" or "tar.gz" or "skill-md"))
                        throw new InvalidOperationException("AgentSkill archive Format must be zip, tar.gz, or skill-md.");
                    source = new(type, uri, digest, format);
                    break;
                case "git":
                    uri = HttpsUrl(Required(sourceMap, "Url"));
                    if (!uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase) &&
                        uri.AbsolutePath.Trim('/') is not { Length: > 0 })
                        throw new InvalidOperationException("Git Source Url must identify an HTTPS repository.");
                    var commit = Required(sourceMap, "Commit");
                    if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
                        throw new InvalidOperationException("Git Source Commit must be an exact 40-character hexadecimal SHA.");
                    source = new(type, uri, Commit: commit.ToLowerInvariant());
                    break;
                case "notion-skill":
                case "notion-plugin":
                    if (!Guid.TryParseExact(Required(sourceMap, "Id"), "D", out var notionId))
                        throw new InvalidOperationException("Notion Source Id must be a UUID.");
                    var versionId = Required(sourceMap, "VersionId");
                    if (versionId.Length != 64)
                        throw new InvalidOperationException("Notion Source VersionId must be a 64-character opaque value.");
                    source = new(type, Id: notionId, VersionId: versionId);
                    break;
                default:
                    uri = HttpsUrl(Required(sourceMap, "IndexUrl"));
                    digest = Sha256(Required(sourceMap, "Sha256"));
                    source = new(type, uri, digest);
                    break;
            }
        }
        else
        {
            uri = HttpsUrl(Required(skill, "ArtifactUrl"));
            digest = Sha256(Required(skill, "ArtifactSha256"));
        }

        string? path = null;
        if (skill.TryGetValue("SkillPath", out var pathValue))
        {
            if (pathValue is not string text || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("AgentSkill SkillPath must be a nonempty relative directory.");
            path = string.Join('/', SplitSafePath(text));
        }
        string? pluginPath = null;
        if (skill.TryGetValue("PluginPath", out var pluginValue))
        {
            if (pluginValue is not string pluginText || string.IsNullOrWhiteSpace(pluginText))
                throw new InvalidOperationException("AgentSkill PluginPath must be a relative directory or '.'.");
            pluginPath = pluginText == "." ? "" : string.Join('/', SplitSafePath(pluginText));
        }
        if (source?.Type == "notion-plugin" && pluginPath is null)
            throw new InvalidOperationException("notion-plugin requires PluginPath and SkillPath.");
        if (pluginPath is not null && path != $"skills/{name}")
            throw new InvalidOperationException("Plugin SkillPath must be skills/<PackageName> relative to PluginPath.");
        if (source?.Format == "skill-md" && (path is not null || pluginPath is not null))
            throw new InvalidOperationException("skill-md archives cannot specify SkillPath or PluginPath.");
        return new AgentSkillManifest(id, version, name, publisher,
            uri ?? new Uri($"https://api.notion.com/v1/ai/{(source!.Type == "notion-plugin" ? "plugins" : "skills")}/{source.Id:D}"),
            digest ?? "", path, source, pluginPath);
    }

    private static Uri HttpsUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidOperationException("AgentSkill URL must be HTTPS without credentials or fragment.");
        return url;
    }

    private static string Sha256(string value)
    {
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidOperationException("AgentSkill Sha256 must be 64 hexadecimal characters.");
        return value.ToUpperInvariant();
    }

    internal static InstallResult Install(AgentSkillManifest manifest, InstallRequest request,
        HttpClient? httpClient = null, string? projectRoot = null, string? home = null,
        string? catalogSource = null, string? catalogManifestSha256 = null)
    {
        ValidateRequest(request);
        projectRoot ??= Environment.CurrentDirectory;
        home ??= GetHomeDirectory();
        var (destination, statePath, user) = InstallPaths(manifest, request, projectRoot, home);
        if (CheckExistingState(manifest, request, destination, statePath, user, catalogSource).NoOp is { } noOp)
            return noOp;
        if (manifest.Source?.Type == "git")
            return InstallGit(manifest, request, projectRoot, home, catalogSource, catalogManifestSha256);
        using var ownedClient = httpClient is null ? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        { Timeout = TimeSpan.FromMinutes(2) } : null;
        httpClient ??= ownedClient!;
        var source = manifest.Source;
        var url = source?.Url ?? manifest.ArtifactUrl;
        var format = source?.Format ?? "zip";
        var digest = source?.Sha256 ?? manifest.ArtifactSha256;
        if (source?.Type is "notion-skill" or "notion-plugin")
        {
            var credential = Environment.GetEnvironmentVariable("NOTION_API_KEY");
            if (string.IsNullOrWhiteSpace(credential))
                throw new InvalidOperationException("NOTION_API_KEY is required for Notion sources.");
            var kind = source.Type == "notion-plugin" ? "plugins" : "skills";
            using var api = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.notion.com/v1/ai/{kind}/{source.Id:D}");
            api.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            api.Headers.Add("Notion-Version", "2026-03-11");
            using var response = httpClient.SendAsync(api).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(ReadBounded(response, 1024 * 1024));
            var root = json.RootElement;
            if (root.GetProperty("id").GetString() != source.Id?.ToString("D") ||
                root.GetProperty("version_id").GetString() != source.VersionId)
                throw new InvalidOperationException("Notion skill/plugin id or version does not match the manifest pin.");
            url = HttpsUrl(root.GetProperty("url").GetString() ?? "");
            format = "tar.gz";
            digest = null;
        }
        else if (source?.Type == "well-known")
        {
            using var indexRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = httpClient.SendAsync(indexRequest).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(ReadBounded(response, 1024 * 1024));
            var root = json.RootElement;
            if (root.GetProperty("$schema").GetString() != DiscoverySchema)
                throw new InvalidOperationException("Unsupported well-known index schema.");
            var entries = root.GetProperty("skills").EnumerateArray()
                .Where(e => e.GetProperty("name").GetString() == manifest.Name).ToArray();
            if (entries.Length != 1)
                throw new InvalidOperationException("Well-known index must contain exactly one matching package name.");
            var entry = entries[0];
            var parts = (entry.GetProperty("digest").GetString() ?? "").Split(':');
            if (parts.Length != 2 || parts[0] != "sha256" ||
                Sha256(parts[1]) != digest)
                throw new InvalidOperationException("Well-known index digest does not match the manifest pin.");
            format = entry.GetProperty("type").GetString() switch
            {
                "skill-md" => "skill-md",
                "archive" => "auto",
                _ => throw new InvalidOperationException("Unsupported well-known entry type."),
            };
            var entryUrl = entry.GetProperty("url").GetString() ?? "";
            if (!Uri.TryCreate(url, entryUrl, out var resolved))
                throw new InvalidOperationException("Well-known skill URL is invalid.");
            url = HttpsUrl(resolved.AbsoluteUri);
        }
        var bytes = Download(url, httpClient);
        return InstallContent(manifest, request, bytes, format, digest, projectRoot, home,
            catalogSource: catalogSource, catalogManifestSha256: catalogManifestSha256);
    }

    private static void ValidateRequest(InstallRequest request)
    {
        if (request.Agent is not ("github-copilot" or "claude-code" or "codex"))
            throw new InvalidOperationException("AgentSkill installation requires --agent github-copilot, claude-code, or codex.");
        if (request.Query.InstallScope is not null and not "user")
            throw new InvalidOperationException("AgentSkill --scope supports only user (the default is project).");
        if (request.IgnoreSecurityHash || request.DependenciesOnly || request.SkipDependencies ||
            request.InstallLocation is not null || request.Rename is not null ||
            request.UninstallPrevious || request.Override is not null || request.Custom is not null ||
            request.LogPath is not null || request.AcceptPackageAgreements || request.Force ||
            request.NoUpgrade || request.DependencySource is not null ||
            request.Mode == InstallerMode.Interactive ||
            request.Query.Query is not null || request.Query.Id is not null ||
            request.Query.Name is not null || request.Query.Moniker is not null ||
            request.Query.Tag is not null || request.Query.Command is not null ||
            request.Query.Count is not null || request.Query.Exact ||
            request.Query.Version is not null || request.Query.Source is not null ||
            request.Query.InstallerType is not null || request.Query.InstallerArchitecture is not null ||
            request.Query.Locale is not null || request.Query.Platform is not null ||
            request.Query.OsVersion is not null || request.Query.Channel is not null)
            throw new InvalidOperationException("Installer-specific options are not supported for AgentSkill manifests.");
    }

    private static string GetHomeDirectory()
    {
        var variable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } home
            ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static byte[] Download(Uri url, HttpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .GetAwaiter().GetResult();
            if (response.StatusCode is System.Net.HttpStatusCode.Moved or
                System.Net.HttpStatusCode.Redirect or
                System.Net.HttpStatusCode.RedirectMethod or
                System.Net.HttpStatusCode.TemporaryRedirect or
                System.Net.HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ??
                    throw new InvalidOperationException("Artifact redirect has no Location header.");
                url = new Uri(url, location);
                if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) ||
                    !string.IsNullOrEmpty(url.Fragment))
                    throw new InvalidOperationException("Artifact redirects must stay on HTTPS.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new InvalidOperationException("AgentSkill artifact exceeds the download limit.");
            using var input = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) != 0)
            {
                if (output.Length + read > MaxArchiveBytes)
                    throw new InvalidOperationException("AgentSkill artifact exceeds the download limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("AgentSkill artifact redirected too many times.");
    }

    private static byte[] ReadBounded(HttpResponseMessage response, int limit)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidOperationException("AgentSkill response exceeds the size limit.");
        using var input = response.Content.ReadAsStream();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > limit)
                throw new InvalidOperationException("AgentSkill response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    // Exposed to the test assembly so ZIP validation can be exercised without a network endpoint.
    internal static InstallResult InstallArchive(
        AgentSkillManifest manifest, InstallRequest request, byte[] archiveBytes,
        string projectRoot, string home, Action<string>? warning = null,
        Action? beforeProvenanceCommit = null)
    {
        return InstallContent(manifest, request, archiveBytes,
            manifest.Source?.Format ?? "zip",
            manifest.Source?.Sha256 ?? manifest.ArtifactSha256, projectRoot, home, warning,
            beforeProvenanceCommit);
    }

    private static InstallResult InstallContent(AgentSkillManifest manifest, InstallRequest request,
        byte[] content, string format, string? digest, string projectRoot, string home,
        Action<string>? warning = null, Action? beforeProvenanceCommit = null,
        string? catalogSource = null, string? catalogManifestSha256 = null)
    {
        ValidateRequest(request);
        if (content.Length > MaxArchiveBytes)
            throw new InvalidOperationException("AgentSkill artifact SHA-256 mismatch or download limit exceeded.");
        var actualDigest = SHA256.HashData(content);
        if (digest is not null && !CryptographicOperations.FixedTimeEquals(
                actualDigest, Convert.FromHexString(digest)))
            throw new InvalidOperationException("AgentSkill artifact SHA-256 mismatch or download limit exceeded.");

        if (format == "auto")
            format = content.AsSpan().StartsWith("PK"u8) ? "zip" :
                content.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b }) ? "tar.gz" :
                throw new InvalidOperationException("Unsupported well-known archive format.");
        var entries = format switch
        {
            "zip" => ReadZip(content),
            "tar.gz" => ReadTarGz(content),
            "skill-md" => new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { ["SKILL.md"] = content },
            _ => throw new InvalidOperationException("Unsupported AgentSkill archive format."),
        };
        return InstallEntries(manifest, request, entries, projectRoot, home,
            artifactSha256: Convert.ToHexString(actualDigest), warning: warning,
            beforeProvenanceCommit: beforeProvenanceCommit, catalogSource: catalogSource,
            catalogManifestSha256: catalogManifestSha256);
    }

    private static InstallResult InstallEntries(AgentSkillManifest manifest, InstallRequest request,
        Dictionary<string, byte[]> entries, string projectRoot, string home, string? sourceRevision = null,
        string? artifactSha256 = null, Action<string>? warning = null,
        Action? beforeProvenanceCommit = null, string? catalogSource = null,
        string? catalogManifestSha256 = null)
    {
        // Notion bundles commonly wrap their payload in one directory.
        var prefix = manifest.PluginPath is null ? manifest.SkillPath :
            JoinPath(manifest.PluginPath, manifest.SkillPath);
        if (manifest.Source?.Type is "notion-skill" or "notion-plugin" &&
            !entries.ContainsKey(JoinPath(prefix, "SKILL.md")))
        {
            var roots = entries.Keys.Select(k => k.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (roots.Length == 1 && entries.Keys.All(k => k.StartsWith(roots[0] + "/", StringComparison.OrdinalIgnoreCase)))
                prefix = JoinPath(roots[0], prefix);
        }
        if (manifest.PluginPath is not null)
        {
            var pluginRoot = prefix![..^manifest.SkillPath!.Length].TrimEnd('/');
            if (!entries.TryGetValue(JoinPath(pluginRoot, "plugin.json"), out var pluginBytes) ||
                pluginBytes.Length > 1024 * 1024)
                throw new InvalidOperationException("Agent Plugins root requires plugin.json (at most 1 MiB).");
            using var json = JsonDocument.Parse(pluginBytes);
            var plugin = json.RootElement;
            if (plugin.ValueKind != JsonValueKind.Object ||
                !plugin.TryGetProperty("$schema", out var schema) ||
                schema.GetString() != PluginSchema ||
                !plugin.TryGetProperty("name", out var pluginName) ||
                pluginName.ValueKind != JsonValueKind.String ||
                pluginName.GetString() is not { } pluginNameValue ||
                pluginNameValue.Length is < 1 or > 64 ||
                !PluginNamePattern.IsMatch(pluginNameValue))
                throw new InvalidOperationException("Invalid Agent Plugins plugin.json schema or name.");
            var unknown = plugin.EnumerateObject().Select(property => property.Name)
                .Except(PluginFields, StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal).ToArray();
            if (unknown.Length != 0)
                (warning ?? Console.Error.WriteLine)(
                    $"Agent Plugins plugin.json contains unknown fields: {string.Join(", ", unknown)}.");
        }
        var skillPrefix = string.IsNullOrEmpty(prefix) ? "" : prefix + "/";
        if (!entries.TryGetValue(skillPrefix + "SKILL.md", out var skillMarkdown) ||
            skillMarkdown.Length > 1024 * 1024)
            throw new InvalidOperationException("AgentSkill resolved directory must contain a SKILL.md (at most 1 MiB).");
        var skillName = ValidateSkillMarkdown(StrictUtf8.GetString(skillMarkdown), manifest.Name);

        var (destination, statePath, user) = InstallPaths(manifest, request, projectRoot, home);
        var skillsRoot = Path.GetDirectoryName(destination)!;
        var stateRoot = Path.GetDirectoryName(statePath)!;
        if (Path.GetFileName(destination) != skillName)
            throw new InvalidOperationException("AgentSkill destination name differs from the validated SKILL.md name.");
        RejectLinkedParents(skillsRoot);
        RejectLinkedParents(stateRoot);
        RejectLinkedParents(destination);
        RejectLinkedParents(statePath);
        var (hasDestination, installedHash, recordedStateHash, noOp) =
            CheckExistingState(manifest, request, destination, statePath, user, catalogSource);
        if (noOp is not null) return noOp;
        Directory.CreateDirectory(skillsRoot);
        Directory.CreateDirectory(stateRoot);

        var staging = Path.Combine(skillsRoot, ".pinget-stage-" + Guid.NewGuid().ToString("N"));
        var stateStaging = Path.Combine(stateRoot, ".pinget-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var (path, bytes) in entries)
            {
                if (!path.StartsWith(skillPrefix, StringComparison.Ordinal) ||
                    path.Length <= skillPrefix.Length)
                    continue;
                var relative = path[skillPrefix.Length..];
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew);
                output.Write(bytes);
            }
            var installedTreeHash = HashInstalledTree(staging);

            using (var stream = new FileStream(stateStaging, FileMode.CreateNew))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("manifestType", "pinget.agentSkill");
                writer.WriteString("packageIdentifier", manifest.Id);
                writer.WriteString("packageVersion", manifest.Version);
                writer.WriteString("packageName", manifest.Name);
                writer.WriteString("publisher", manifest.Publisher);
                writer.WriteString("agent", request.Agent);
                writer.WriteString("scope", user ? "user" : "project");
                writer.WriteString("artifactSha256", artifactSha256 ?? manifest.ArtifactSha256);
                writer.WriteString("installedTreeSha256", installedTreeHash);
                if (catalogSource is not null)
                {
                    writer.WriteString("catalogSource", catalogSource);
                    writer.WriteString("catalogManifestSha256", catalogManifestSha256);
                }
                if (manifest.Source is null)
                {
                    writer.WriteString("artifactUrl", manifest.ArtifactUrl.GetLeftPart(UriPartial.Path));
                }
                else
                {
                    writer.WriteString("sourceType", manifest.Source.Type);
                    if (manifest.Source.Type is "notion-skill" or "notion-plugin")
                        writer.WriteString("sourceId", manifest.Source.Id?.ToString("D"));
                    else
                        writer.WriteString("sourceUrl", manifest.Source.Url?.GetLeftPart(UriPartial.Path));
                    writer.WriteString("sourceRevision", sourceRevision ?? manifest.Source.Commit ??
                        manifest.Source.VersionId ?? manifest.Source.Sha256);
                }
                writer.WriteString("skillPath", manifest.SkillPath);
                writer.WriteString("pluginPath", manifest.PluginPath);
                writer.WriteString("installedPath", destination);
                writer.WriteString("installedAt", DateTimeOffset.UtcNow);
                writer.WriteEndObject();
            }

            if (!hasDestination)
            {
                Directory.Move(staging, destination);
                try
                {
                    File.Move(stateStaging, statePath);
                }
                catch
                {
                    Directory.Delete(destination, recursive: true);
                    throw;
                }
            }
            else
            {
                if (!string.Equals(installedHash, HashInstalledTree(destination), StringComparison.OrdinalIgnoreCase) ||
                    !CryptographicOperations.FixedTimeEquals(recordedStateHash!, SHA256.HashData(File.ReadAllBytes(statePath))))
                    throw new InvalidOperationException("Managed AgentSkill files or provenance changed while staging.");
                var backup = Path.Combine(skillsRoot, ".pinget-backup-" + Guid.NewGuid().ToString("N"));
                var stateBackup = Path.Combine(stateRoot, ".pinget-backup-" + Guid.NewGuid().ToString("N"));
                var movedDirectory = false;
                var movedState = false;
                try
                {
                    Directory.Move(destination, backup);
                    movedDirectory = true;
                    File.Move(statePath, stateBackup);
                    movedState = true;
                    Directory.Move(staging, destination);
                    beforeProvenanceCommit?.Invoke();
                    File.Move(stateStaging, statePath);
                }
                catch
                {
                    if (movedDirectory)
                    {
                        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                        Directory.Move(backup, destination);
                    }
                    if (movedState)
                    {
                        if (File.Exists(statePath)) File.Delete(statePath);
                        File.Move(stateBackup, statePath);
                    }
                    throw;
                }
                try
                {
                    Directory.Delete(backup, recursive: true);
                    File.Delete(stateBackup);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A stale backup is safer than rolling back an already committed update.
                }
            }
            return new InstallResult
            {
                PackageId = manifest.Id,
                Version = manifest.Version,
                InstallerType = "agentSkill",
                InstallerPath = destination,
                ExitCode = 0,
                Success = true,
            };
        }

        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (File.Exists(stateStaging)) File.Delete(stateStaging);
        }
    }

    private static (string Destination, string StatePath, bool User) InstallPaths(
        AgentSkillManifest manifest, InstallRequest request, string projectRoot, string home)
    {
        var user = request.Query.InstallScope == "user";
        var root = Path.GetFullPath(user ? home : projectRoot);
        var skillsRoot = user
            ? Path.Combine(root, request.Agent switch
            {
                "github-copilot" => ".copilot",
                "claude-code" => ".claude",
                _ => ".codex",
            }, "skills")
            : Path.Combine(root, request.Agent == "claude-code" ? ".claude" : ".agents", "skills");
        var stateRoot = Path.Combine(root, ".pinget", "agent-skills", request.Agent!);
        return (Path.Combine(skillsRoot, manifest.Name),
            Path.Combine(stateRoot, manifest.Name + ".json"), user);
    }

    private static (bool Exists, string? InstalledHash, byte[]? RecordedStateHash, InstallResult? NoOp)
        CheckExistingState(AgentSkillManifest manifest, InstallRequest request,
            string destination, string statePath, bool user, string? catalogSource)
    {
        RejectLinkedParents(destination);
        RejectLinkedParents(statePath);
        var exists = File.Exists(destination) || Directory.Exists(destination);
        var hasState = File.Exists(statePath) || Directory.Exists(statePath);
        if (exists != hasState || File.Exists(destination) || Directory.Exists(statePath))
            throw new InvalidOperationException("AgentSkill destination is unmanaged or its provenance is missing.");
        if (request.RequireExistingAgentSkill && !exists)
            throw new InvalidOperationException("No managed AgentSkill is installed to upgrade.");
        if (!exists) return (false, null, null, null);
        var recordedState = File.ReadAllBytes(statePath);
        var recordedStateHash = SHA256.HashData(recordedState);
        using var state = JsonDocument.Parse(recordedState);
        var record = state.RootElement;
        if (record.GetProperty("manifestType").GetString() != "pinget.agentSkill" ||
            !string.Equals(record.GetProperty("packageIdentifier").GetString(), manifest.Id, StringComparison.OrdinalIgnoreCase) ||
            record.GetProperty("packageName").GetString() != manifest.Name ||
            record.GetProperty("agent").GetString() != request.Agent ||
            record.GetProperty("scope").GetString() != (user ? "user" : "project") ||
            record.GetProperty("installedPath").GetString() != destination ||
            (record.TryGetProperty("catalogSource", out var installedSource) &&
                installedSource.GetString() != catalogSource) ||
            !record.TryGetProperty("installedTreeSha256", out var recordedHash) ||
            !string.Equals(recordedHash.GetString(), HashInstalledTree(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Managed AgentSkill identity, provenance, or installed files have changed; update refused.");
        var installedVersion = record.GetProperty("packageVersion").GetString()!;
        var comparison = RestSource.CompareVersionStrings(manifest.Version, installedVersion);
        if (comparison < 0)
            throw new InvalidOperationException("AgentSkill downgrade is not supported.");
        var noOp = comparison != 0 ? null : new InstallResult
        {
            PackageId = manifest.Id,
            Version = installedVersion,
            InstallerType = "agentSkill",
            InstallerPath = destination,
            ExitCode = 0,
            Success = true,
            NoOp = true,
        };
        return (true, recordedHash.GetString(), recordedStateHash, noOp);
    }

    private static string HashInstalledTree(string root)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<(string Path, string Relative, byte[] RelativeBytes)>();
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entries > MaxEntries)
                    throw new InvalidOperationException("Managed AgentSkill has too many entries.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Managed AgentSkill contains a symbolic link.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
                else
                {
                    if ((attributes & FileAttributes.Device) != 0)
                        throw new InvalidOperationException("Managed AgentSkill contains a non-file entry.");
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    files.Add((path, relative, StrictUtf8.GetBytes(relative)));
                }
            }
        }
        files.Sort((left, right) => left.RelativeBytes.AsSpan().SequenceCompareTo(right.RelativeBytes));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        foreach (var (file, name, pathBytes) in files)
        {
            if (!seen.Add(name))
                throw new InvalidOperationException("Managed AgentSkill contains conflicting paths.");
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)pathBytes.Length);
            digest.AppendData(length);
            digest.AppendData(pathBytes);
            using var input = File.OpenRead(file);
            if (input.Length > MaxEntryBytes || total > MaxExtractedBytes - input.Length)
                throw new InvalidOperationException("Managed AgentSkill exceeds extraction limits.");
            total += input.Length;
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)input.Length);
            digest.AppendData(length);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) != 0) digest.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static Dictionary<string, byte[]> ReadZip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidOperationException("AgentSkill ZIP has too many entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var path = string.Join('/', SplitSafePath(directory ? entry.FullName[..^1] : entry.FullName));
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode != 0 && mode != (directory ? 0x4000 : 0x8000) ||
                (entry.ExternalAttributes & 0x10) != 0 && !directory)
                throw new InvalidOperationException("AgentSkill ZIP contains a link or unsupported entry type.");
            if (!seen.Add(path))
                throw new InvalidOperationException("AgentSkill ZIP contains duplicate paths.");
            if (entry.Length > MaxEntryBytes || total > MaxExtractedBytes - entry.Length)
                throw new InvalidOperationException("AgentSkill ZIP exceeds extraction limits.");
            total += entry.Length;
            if (!directory)
            {
                using var input = entry.Open();
                files.Add(path, ReadEntry(input, entry.Length));
            }
        }
        ValidateFileParents(files.Keys, seen);
        return files;
    }

    private static Dictionary<string, byte[]> ReadTarGz(byte[] bytes)
    {
        using var compressed = new MemoryStream(bytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var count = 0;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++count > MaxEntries)
                throw new InvalidOperationException("AgentSkill tar.gz has too many entries.");
            var directory = entry.EntryType == TarEntryType.Directory;
            if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidOperationException("AgentSkill tar.gz contains a link or unsupported entry type.");
            var path = string.Join('/', SplitSafePath(entry.Name.TrimEnd('/')));
            if (!seen.Add(path))
                throw new InvalidOperationException("AgentSkill tar.gz contains duplicate paths.");
            if (entry.Length > MaxEntryBytes || total > MaxExtractedBytes - entry.Length)
                throw new InvalidOperationException("AgentSkill tar.gz exceeds extraction limits.");
            total += entry.Length;
            if (!directory)
                files.Add(path, ReadEntry(entry.DataStream!, entry.Length));
        }
        ValidateFileParents(files.Keys, seen);
        return files;
    }

    private static byte[] ReadEntry(Stream input, long expected)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > expected || output.Length + read > MaxEntryBytes)
                throw new InvalidOperationException("AgentSkill archive entry exceeds its declared size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != expected)
            throw new InvalidOperationException("AgentSkill archive entry length does not match its header.");
        return output.ToArray();
    }

    private static void ValidateFileParents(IEnumerable<string> filePaths, HashSet<string> seen)
    {
        var files = new HashSet<string>(filePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in seen)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
                if (files.Contains(string.Join('/', parts[..i])))
                    throw new InvalidOperationException("AgentSkill archive contains a file used as a directory.");
        }
    }

    private static string JoinPath(string? left, string? right) =>
        string.IsNullOrEmpty(left) ? right ?? "" :
        string.IsNullOrEmpty(right) ? left : left + "/" + right;

    private static string[] SplitSafePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(char.IsControl))
            throw new InvalidOperationException("AgentSkill path is unsafe.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny("<>:\"|?*".ToCharArray()) >= 0 ||
                IsDeviceName(part))
                throw new InvalidOperationException("AgentSkill path is unsafe.");
        }
        return parts;
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static string ValidateSkillMarkdown(string markdown, string packageName)
    {
        using var reader = new StringReader(markdown.TrimStart('\uFEFF'));
        if (reader.ReadLine() != "---")
            throw new InvalidOperationException("SKILL.md requires YAML frontmatter.");
        var yaml = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null && line != "---")
            yaml.AppendLine(line);
        if (line is null || Repository.ParseYamlManifestDocuments(Encoding.UTF8.GetBytes(yaml.ToString()))
            is not Dictionary<string, object?> frontmatter ||
            !frontmatter.TryGetValue("name", out var nameValue) || nameValue is not string name ||
            !frontmatter.TryGetValue("description", out var description) || description is not string desc ||
            string.IsNullOrWhiteSpace(desc) || desc.Length > 1024 || name.Length is < 1 or > 64 ||
            !SkillNamePattern.IsMatch(name))
            throw new InvalidOperationException("SKILL.md requires a valid name and description in YAML frontmatter.");
        if (!string.Equals(packageName, name, StringComparison.Ordinal))
            throw new InvalidOperationException("SKILL.md name must match manifest PackageName.");
        if (string.IsNullOrWhiteSpace(reader.ReadToEnd()))
            throw new InvalidOperationException("SKILL.md must contain instructions after its frontmatter.");
        return name;
    }

    private static void RejectLinkedParents(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (new DirectoryInfo(current).LinkTarget is not null ||
                new FileInfo(current).LinkTarget is not null)
                throw new InvalidOperationException("AgentSkill destination contains a symbolic link.");
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
    }
}
