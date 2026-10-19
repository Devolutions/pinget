using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Devolutions.Pinget.Core;

internal sealed record AgentSkillCatalogEntry(string Id, string Name, string Version, Uri ManifestUrl, string ManifestSha256);

internal static class AgentSkillCatalog
{
    private const int MaxCatalogBytes = 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxPackages = 1000;
    private static readonly Regex ParentPath = new(
        @"(^|/)\.\.(/|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SkillName = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static SourceStore LoadSources(string appRoot)
    {
        var path = Path.Combine(appRoot, "agent-skill-sources.json");
        if (!File.Exists(path)) return new SourceStore();
        var sources = JsonSerializer.Deserialize(File.ReadAllText(path), SourceStoreContext.Default.SourceStore) ??
            throw new InvalidOperationException("Invalid AgentSkills source store.");
        if (sources.Sources is null || sources.Sources.Any(source => source.Kind != SourceKind.AgentSkills ||
            RequireHttps(source.Arg).Query.Length != 0) ||
            sources.Sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            sources.Sources.Count)
            throw new InvalidOperationException("AgentSkills source store contains a non-AgentSkills source or URL with a query.");
        return sources;
    }

    internal static void SaveSources(string appRoot, SourceStore sources)
    {
        var path = Path.Combine(appRoot, "agent-skill-sources.json");
        var staging = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, JsonSerializer.Serialize(sources, SourceStoreContext.Default.SourceStore));
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    internal static string CachePath(string appRoot, SourceRecord source)
    {
        var identity = Encoding.UTF8.GetBytes(source.Name);
        return Path.Combine(appRoot, "agent-skill-catalogs",
            Convert.ToHexString(SHA256.HashData(identity)) + ".json");
    }

    internal static void DeleteCache(string appRoot, SourceRecord source)
    {
        var path = CachePath(appRoot, source);
        if (File.Exists(path)) File.Delete(path);
    }

    internal static Uri RequireHttps(string value)
    {
        var path = value.Split('?', '#')[0];
        if (path.Contains('\\') || ParentPath.IsMatch(Uri.UnescapeDataString(path)) ||
            Regex.IsMatch(path, "%(?:2e|2f|5c)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("AgentSkills catalog URLs must be HTTPS without credentials, fragments or traversal.");
        return uri;
    }

    private static Uri CatalogUrl(SourceRecord source)
    {
        var url = RequireHttps(source.Arg);
        if (url.Query.Length != 0)
            throw new InvalidOperationException("AgentSkills catalog source URL cannot contain a query or token.");
        return url;
    }

    private static Uri ManifestUrl(string value, Uri? catalogUrl)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
            return RequireHttps(value);
        if (catalogUrl is null || value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\') || value.Contains(':') || value.Contains('?') || value.Contains('#') ||
            ParentPath.IsMatch(Uri.UnescapeDataString(value)) ||
            Regex.IsMatch(value, "%(?:2e|2f|5c)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("AgentSkills catalog manifestUrl must be a safe relative path or absolute HTTPS URL.");
        return RequireHttps(new Uri(catalogUrl, value).AbsoluteUri);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"AgentSkills catalog requires {property}.");
        return value.GetString()!;
    }

    private static string CatalogIdentity(JsonElement element, string property)
    {
        var value = RequiredString(element, property);
        if (value.Length > 128 || value.StartsWith('.') || value.EndsWith('.') ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) ||
                character is '/' or '\\' or ':' or '%' or '?' or '#'))
            throw new InvalidOperationException($"AgentSkills catalog {property} contains an unsafe identifier or version.");
        return value;
    }

    internal static List<AgentSkillCatalogEntry> Parse(byte[] bytes, Uri? catalogUrl = null)
    {
        if (bytes.Length > MaxCatalogBytes)
            throw new InvalidOperationException("AgentSkills catalog exceeds its size limit.");
        if (catalogUrl is not null)
        {
            catalogUrl = RequireHttps(catalogUrl.OriginalString);
            if (catalogUrl.Query.Length != 0)
                throw new InvalidOperationException("AgentSkills catalog source URL cannot contain a query or token.");
        }
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
            !root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Unsupported AgentSkills catalog schema; expected schemaVersion 1 and packages array.");
        RequireFields(root, ["schemaVersion", "packages"]);
        var entries = new List<AgentSkillCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("AgentSkills catalog package must be an object.");
            RequireFields(package, ["packageIdentifier", "packageName", "packageVersion", "manifestUrl", "manifestSha256"]);
            var id = CatalogIdentity(package, "packageIdentifier");
            var name = RequiredString(package, "packageName");
            if (name.Length is < 1 or > 64 || !SkillName.IsMatch(name))
                throw new InvalidOperationException("AgentSkills catalog packageName must be a lowercase skill slug.");
            var packageVersion = CatalogIdentity(package, "packageVersion");
            var url = ManifestUrl(RequiredString(package, "manifestUrl"), catalogUrl);
            if (url.Query.Length != 0)
                throw new InvalidOperationException("AgentSkills catalog manifestUrl cannot contain a query or token.");
            var digest = RequiredString(package, "manifestSha256");
            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
                throw new InvalidOperationException("AgentSkills manifestSha256 must be 64 hexadecimal characters.");
            if (!seen.Add(id + "\0" + packageVersion))
                throw new InvalidOperationException("AgentSkills catalog contains duplicate package identifier and version.");
            if (entries.Count >= MaxPackages)
                throw new InvalidOperationException("AgentSkills catalog has too many packages.");
            entries.Add(new(id, name, packageVersion, url, digest.ToUpperInvariant()));
        }
        return entries;
    }

    private static void RequireFields(JsonElement value, HashSet<string> allowed)
    {
        var fields = value.EnumerateObject().Select(field => field.Name).ToArray();
        if (fields.Length != allowed.Count || fields.Distinct(StringComparer.Ordinal).Count() != allowed.Count ||
            fields.Any(field => !allowed.Contains(field)))
            throw new InvalidOperationException("AgentSkills catalog has missing, duplicate, or unknown fields.");
    }

    private static void EnsureImmutableVersions(IReadOnlyList<AgentSkillCatalogEntry> previous,
        IReadOnlyList<AgentSkillCatalogEntry> updated)
    {
        foreach (var entry in updated)
        {
            var old = previous.FirstOrDefault(candidate =>
                candidate.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) &&
                candidate.Version.Equals(entry.Version, StringComparison.OrdinalIgnoreCase));
            if (old is not null && (old.Name != entry.Name || old.ManifestUrl != entry.ManifestUrl ||
                !old.ManifestSha256.Equals(entry.ManifestSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "AgentSkills catalog changed an existing package version; publish a new version instead.");
        }
    }

    internal static byte[] Fetch(HttpClient client, Uri url, int limit = MaxCatalogBytes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .GetAwaiter().GetResult();
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ??
                    throw new InvalidOperationException("AgentSkills catalog redirect lacks Location.");
                url = RequireHttps(new Uri(url, location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidOperationException("AgentSkills response exceeds its size limit.");
            using var stream = response.Content.ReadAsStreamAsync(timeout.Token).GetAwaiter().GetResult();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult()) != 0)
            {
                if (output.Length + read > limit)
                    throw new InvalidOperationException("AgentSkills response exceeds its size limit.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("AgentSkills response redirected too many times.");
    }

    internal static AgentSkillManifest Resolve(HttpClient client, SourceRecord source, string id, string? requestedVersion)
        => Resolve(client, source, id, requestedVersion, out _);

    internal static AgentSkillManifest Resolve(HttpClient client, SourceRecord source, string id,
        string? requestedVersion, out string manifestSha256)
        => Resolve(client, Read(client, source), id, requestedVersion, out manifestSha256);

    internal static List<AgentSkillCatalogEntry> Read(HttpClient client, SourceRecord source, string? appRoot = null)
    {
        var catalogUrl = CatalogUrl(source);
        if (appRoot is null) return Parse(Fetch(client, catalogUrl), catalogUrl);
        var cache = CachePath(appRoot, source);
        return File.Exists(cache) ? Parse(File.ReadAllBytes(cache), catalogUrl) : Refresh(client, source, appRoot);
    }

    internal static List<AgentSkillCatalogEntry> Refresh(HttpClient client, SourceRecord source, string appRoot)
    {
        var catalogUrl = CatalogUrl(source);
        var entries = Parse(Fetch(client, catalogUrl), catalogUrl);
        var cache = CachePath(appRoot, source);
        if (File.Exists(cache))
            EnsureImmutableVersions(Parse(File.ReadAllBytes(cache), catalogUrl), entries);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        var staging = cache + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew))
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("packages");
                foreach (var entry in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("packageIdentifier", entry.Id);
                    writer.WriteString("packageName", entry.Name);
                    writer.WriteString("packageVersion", entry.Version);
                    writer.WriteString("manifestUrl", entry.ManifestUrl.AbsoluteUri);
                    writer.WriteString("manifestSha256", entry.ManifestSha256);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(staging, cache, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
        return entries;
    }

    internal static AgentSkillManifest Resolve(HttpClient client, IReadOnlyList<AgentSkillCatalogEntry> catalog,
        string id, string? requestedVersion, out string manifestSha256)
    {
        var entries = catalog
            .Where(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = requestedVersion is null
            ? entries.OrderByDescending(entry => entry.Version, Comparer<string>.Create(RestSource.CompareVersionStrings)).FirstOrDefault()
            : entries.FirstOrDefault(entry => string.Equals(entry.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
            throw new InvalidOperationException($"AgentSkills catalog has no matching package '{id}'" +
                (requestedVersion is null ? "." : $" version '{requestedVersion}'."));
        manifestSha256 = selected.ManifestSha256;
        var manifest = Fetch(client, selected.ManifestUrl, MaxManifestBytes);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifest), Convert.FromHexString(selected.ManifestSha256)))
            throw new InvalidOperationException("AgentSkills manifest SHA-256 does not match the catalog pin.");
        var parsed = AgentSkillInstaller.TryParse(manifest) ??
            throw new InvalidOperationException("AgentSkills catalog references a non-AgentSkill manifest.");
        if (!string.Equals(parsed.Id, selected.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Name, selected.Name, StringComparison.Ordinal) ||
            !string.Equals(parsed.Version, selected.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("AgentSkills catalog manifest identity, packageName, or version does not match.");
        return parsed;
    }
}
