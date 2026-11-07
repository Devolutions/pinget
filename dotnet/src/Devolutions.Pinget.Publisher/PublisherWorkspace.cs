using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using YamlDotNet.RepresentationModel;

namespace Pinget.Publisher;

public sealed partial class PublisherWorkspace
{
    private const int StateSchemaVersion = 1;
    internal const int ArtifactFormatVersion = 2;
    private readonly string _stateDirectory;

    public PublisherWorkspace(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public PublisherBuildResult Build(PublisherBuildOptions options)
    {
        var input = Path.GetFullPath(options.InputDirectory);
        var state = Path.GetFullPath(options.StateDirectory);
        EnsureDisjoint(input, state, "manifest", "state");
        EnsureSafeExistingPath(input);
        EnsureSafeExistingPath(state);
        if (File.Exists(Path.Combine(state, "publisher.db")))
        {
            using var existingState = OpenState(state, create: false);
        }
        var scan = ManifestScanner.Scan(input);
        Directory.CreateDirectory(state);
        EnsureSafeExistingPath(state);
        using var writerLock = AcquireLock(state);
        using var stateCheck = OpenState(state, create: true);
        Directory.CreateDirectory(Path.Combine(state, "builds"));
        EnsureSafeExistingPath(Path.Combine(state, "builds"));
        var buildId = ComputeBuildId(scan.Inventory);
        var snapshot = Path.Combine(state, "builds", buildId);
        var changed = 0;
        var removed = 0;
        var previousInventory = TryGetLatestInventory(state);
        if (previousInventory is not null)
        {
            changed = scan.Inventory.Count(pair => previousInventory.TryGetValue(pair.Key, out var hash) && !hash.Equals(pair.Value, StringComparison.Ordinal));
            changed += scan.Inventory.Keys.Count(path => !previousInventory.ContainsKey(path));
            removed = previousInventory.Keys.Count(path => !scan.Inventory.ContainsKey(path));
        }

        if (!Directory.Exists(snapshot))
        {
            var staging = Path.Combine(state, "builds", $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                WriteSnapshot(staging, buildId, scan);
                Directory.Move(staging, snapshot);
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }
        else
        {
            VerifySnapshotDirectory(snapshot, buildId);
        }

        using var connection = OpenState(state, create: true);
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO builds(id,inventory,summary) VALUES($id,$inventory,$summary)";
            insert.Parameters.AddWithValue("$id", buildId);
            insert.Parameters.AddWithValue("$inventory", WriteInventory(scan.Inventory));
            var snapshotMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(snapshot, "snapshot.json")))!.AsObject();
            var summary = new JsonObject
            {
                ["manifestCount"] = scan.Manifests.Count,
                ["packageCount"] = scan.Packages.Count,
                ["createdUtc"] = snapshotMetadata["createdUtc"]!.GetValue<string>(),
                ["snapshot"] = Path.GetRelativePath(state, snapshot).Replace('\\', '/'),
            };
            insert.Parameters.AddWithValue("$summary", summary.ToJsonString());
            insert.ExecuteNonQuery();
        }
        SetMetadata(connection, transaction, "schemaVersion", StateSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMetadata(connection, transaction, "lastBuildId", buildId);
        transaction.Commit();
        return new PublisherBuildResult(buildId, snapshot, scan.Manifests.Count, scan.Packages.Count, changed, removed);
    }

    public PublisherDiffResult Diff(string inputDirectory)
    {
        EnsureSafeExistingPath(_stateDirectory);
        EnsureSafeExistingPath(Path.GetFullPath(inputDirectory));
        EnsureDisjoint(Path.GetFullPath(inputDirectory), _stateDirectory, "manifest", "state");
        using var connection = OpenState(_stateDirectory, create: false);
        var current = ManifestScanner.Scan(Path.GetFullPath(inputDirectory), generateArtifacts: false).Inventory;
        var latestBuildId = GetMetadata(connection, "lastBuildId");
        var publishedBuildId = GetMetadata(connection, "lastPublishedBuildId");
        if (latestBuildId is null)
            return new PublisherDiffResult(null, false, current.Keys.Order(StringComparer.Ordinal).ToArray(), [], []);
        var previous = ReadInventory(connection, latestBuildId)
            ?? throw new PublisherException($"State metadata references missing build '{latestBuildId}'.");
        var added = current.Keys.Where(path => !previous.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var changed = current.Keys.Where(path => previous.TryGetValue(path, out var hash) && !hash.Equals(current[path], StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var removed = previous.Keys.Where(path => !current.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        return new PublisherDiffResult(latestBuildId, string.Equals(publishedBuildId, latestBuildId, StringComparison.Ordinal), added, changed, removed);
    }

    public PublisherStatusResult Status()
    {
        EnsureSafeExistingPath(_stateDirectory);
        using var connection = OpenState(_stateDirectory, create: false);
        var latest = GetMetadata(connection, "lastBuildId");
        var published = GetMetadata(connection, "lastPublishedBuildId");
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM builds";
        var buildCount = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        string? timestamp = null;
        if (latest is not null)
        {
            var summary = ReadBuildSummary(connection, latest);
            timestamp = summary?["createdUtc"]?.GetValue<string>();
        }
        return new PublisherStatusResult(latest, published, buildCount, timestamp);
    }

    public PublisherVerifyResult Verify(string? buildId = null)
    {
        EnsureSafeExistingPath(_stateDirectory);
        using var connection = OpenState(_stateDirectory, create: false);
        buildId ??= GetMetadata(connection, "lastBuildId")
            ?? throw new PublisherException("Publisher state has no successful build.");
        if (!BuildIdPattern().IsMatch(buildId))
            throw new PublisherException($"Invalid build ID: '{buildId}'.");
        var stateInventory = ReadInventory(connection, buildId)
            ?? throw new PublisherException($"Publisher state does not contain build '{buildId}'.");
        var snapshot = Path.Combine(_stateDirectory, "builds", buildId);
        VerifySnapshotDirectory(snapshot, buildId);
        var snapshotMetadata = JsonNode.Parse(File.ReadAllText(Path.Combine(snapshot, "snapshot.json"))) as JsonObject;
        var snapshotInventory = snapshotMetadata?["inventory"] as JsonObject
            ?? throw new PublisherException("Snapshot input inventory is missing.");
        if (stateInventory.Count != snapshotInventory.Count
            || stateInventory.Any(item => !string.Equals(snapshotInventory[item.Key]?.GetValue<string>(), item.Value, StringComparison.Ordinal)))
            throw new PublisherException("Snapshot input inventory does not match publisher state.");
        var summary = ReadBuildSummary(connection, buildId)
            ?? throw new PublisherException("Publisher build summary is missing.");
        foreach (var key in new[] { "manifestCount", "packageCount", "createdUtc" })
            if (!JsonNode.DeepEquals(summary[key], snapshotMetadata![key]))
                throw new PublisherException($"Snapshot '{key}' does not match publisher build summary.");
        return new PublisherVerifyResult(true, "unsigned build snapshot", CountSnapshotArtifacts(snapshot), buildId);
    }

    internal static void RecordPublished(string stateDirectory, string buildId)
    {
        var state = Path.GetFullPath(stateDirectory);
        using var connection = OpenState(state, create: true);
        using var transaction = connection.BeginTransaction();
        if (ReadInventory(connection, buildId) is null)
            throw new PublisherException($"Cannot record publication; build '{buildId}' is not in this state.");
        SetMetadata(connection, transaction, "lastPublishedBuildId", buildId);
        transaction.Commit();
    }

    internal static FileStream AcquireLock(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, ".publisher.lock");
        EnsureSafeExistingPath(path);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new PublisherException($"Another publisher writer holds the state lock at '{path}'.", ex);
        }
    }

    internal static SqliteConnection OpenState(string stateDirectory, bool create)
    {
        var path = Path.Combine(stateDirectory, "publisher.db");
        EnsureSafeExistingPath(stateDirectory);
        var exists = File.Exists(path);
        if (exists && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PublisherException("Publisher state database cannot be a reparse point.");
        if (!exists && !create)
            throw new PublisherException($"Publisher state does not exist: {path}");
        Directory.CreateDirectory(stateDirectory);
        if (!exists)
        {
            try
            {
                using var reserved = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new PublisherException($"Cannot exclusively create publisher state '{path}'; retry without replacing the existing database.", ex);
            }
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            if (create && !exists)
            {
                using var command = connection.CreateCommand();
                command.CommandText = WinGetV2IndexWriter.EmbeddedSql("state-schema.sql");
                command.ExecuteNonQuery();
                using var transaction = connection.BeginTransaction();
                SetMetadata(connection, transaction, "schemaVersion", StateSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SetMetadata(connection, transaction, "artifactFormatVersion", ArtifactFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                transaction.Commit();
            }
            if (!HasTable(connection, "metadata") || !HasTable(connection, "builds"))
                throw new PublisherException($"Existing database is not a publisher state database: '{path}'.");
            using (var tables = connection.CreateCommand())
            {
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using var reader = tables.ExecuteReader();
                var names = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                    names.Add(reader.GetString(0));
                if (!names.SetEquals(new[] { "metadata", "builds" }))
                    throw new PublisherException("Existing publisher state has unexpected tables.");
            }
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT id,inventory,summary FROM builds LIMIT 0";
                using var reader = columns.ExecuteReader();
            }
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != StateSchemaVersion)
                    throw new PublisherException($"Unsupported publisher state schema in '{path}'.");
            }
            if (!string.Equals(GetMetadata(connection, "schemaVersion"), StateSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new PublisherException($"Unsupported publisher state metadata version in '{path}'.");
            ValidateArtifactFormatVersion(GetMetadata(connection, "artifactFormatVersion"), "publisher state");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void ValidateArtifactFormatVersion(string? value, string artifact)
    {
        if (!string.Equals(value, ArtifactFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new PublisherException($"Unsupported {artifact} artifactFormatVersion '{value ?? "missing (legacy format 1)"}'; format {ArtifactFormatVersion} is required. Use a fresh --state directory. Existing files and snapshots are preserved; automatic state migration is not supported. Existing publication destinations can receive a new, explicitly higher package version.");
    }

    internal static string? GetMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE name=$name";
        command.Parameters.AddWithValue("$name", key);
        return command.ExecuteScalar() as string;
    }

    internal static void SetMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO metadata(name,value) VALUES($name,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$name", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal static Dictionary<string, string>? ReadInventory(SqliteConnection connection, string buildId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT inventory FROM builds WHERE id=$id";
        command.Parameters.AddWithValue("$id", buildId);
        if (command.ExecuteScalar() is not string json)
            return null;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateObject())
            result.Add(item.Name, item.Value.GetString()!);
        return result.ToDictionary(StringComparer.Ordinal);
    }

    internal static JsonObject? ReadBuildSummary(SqliteConnection connection, string buildId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT summary FROM builds WHERE id=$id";
        command.Parameters.AddWithValue("$id", buildId);
        return command.ExecuteScalar() is string json ? JsonNode.Parse(json) as JsonObject : null;
    }

    private static Dictionary<string, string>? TryGetLatestInventory(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, "publisher.db");
        if (!File.Exists(path))
            return null;
        using var connection = OpenState(stateDirectory, create: false);
        var buildId = GetMetadata(connection, "lastBuildId");
        return buildId is null ? null : ReadInventory(connection, buildId);
    }

    private static void WriteSnapshot(string directory, string buildId, ScanResult scan)
    {
        var feed = Path.Combine(directory, "feed");
        Directory.CreateDirectory(feed);
        var artifactHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var manifest in scan.Manifests)
        {
            var path = ResolveContained(feed, manifest.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, manifest.Content);
            artifactHashes.Add(manifest.RelativePath, manifest.Hash);
        }
        foreach (var package in scan.Packages)
        {
            var sidecarHash = Convert.ToHexString(package.PackageHash).ToLowerInvariant();
            var sidecarRelative = $"packages/{package.Id}/{sidecarHash[..8]}/versionData.mszyml";
            var sidecarPath = ResolveContained(feed, sidecarRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllBytes(sidecarPath, package.CompressedVersionData);
            artifactHashes.Add(sidecarRelative, sidecarHash);
        }
        var indexPath = Path.Combine(directory, "Public", "index.db");
        WinGetV2IndexWriter.Write(indexPath, scan.Packages, Guid.NewGuid().ToString("D"));
        var snapshot = new JsonObject
        {
            ["schemaVersion"] = StateSchemaVersion,
            ["artifactFormatVersion"] = ArtifactFormatVersion,
            ["buildId"] = buildId,
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["manifestCount"] = scan.Manifests.Count,
            ["packageCount"] = scan.Packages.Count,
            ["indexHash"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))).ToLowerInvariant(),
            ["inventory"] = JsonNode.Parse(WriteInventory(scan.Inventory)),
            ["artifacts"] = JsonNode.Parse(WriteInventory(artifactHashes)),
        };
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void VerifySnapshotDirectory(string snapshot, string expectedBuildId)
    {
        if (!Directory.Exists(snapshot))
            throw new PublisherException($"Build snapshot does not exist: {snapshot}");
        EnsureSafeExistingPath(snapshot);
        var metadataPath = Path.Combine(snapshot, "snapshot.json");
        EnsureSafeExistingPath(metadataPath);
        if (!File.Exists(metadataPath))
            throw new PublisherException($"Build snapshot metadata is missing: {metadataPath}");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject
            ?? throw new PublisherException($"Invalid build snapshot metadata: {metadataPath}");
        if (!string.Equals(metadata["buildId"]?.GetValue<string>(), expectedBuildId, StringComparison.Ordinal))
            throw new PublisherException($"Snapshot identity does not match expected build '{expectedBuildId}'.");
        if (metadata["schemaVersion"]?.GetValue<int>() != StateSchemaVersion)
            throw new PublisherException("Unsupported snapshot schema version.");
        ValidateArtifactFormatVersion(metadata["artifactFormatVersion"]?.ToJsonString(), "snapshot");
        if (metadata["inventory"] is not JsonObject inputInventory)
            throw new PublisherException("Snapshot input inventory is missing.");
        var inventory = inputInventory.ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        if (!string.Equals(ComputeBuildId(inventory), expectedBuildId, StringComparison.Ordinal))
            throw new PublisherException("Snapshot build ID does not match its input inventory.");
        var feed = Path.Combine(snapshot, "feed");
        if (metadata["artifacts"] is not JsonObject artifacts)
            throw new PublisherException("Snapshot artifact inventory is missing.");
        var actualPaths = EnumerateSafeFiles(feed).Select(p => Path.GetRelativePath(feed, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(artifacts.Select(p => p.Key)))
            throw new PublisherException("Snapshot feed does not match its exact artifact inventory.");
        foreach (var entry in artifacts)
        {
            var path = ResolveContained(feed, entry.Key);
            if (!File.Exists(path))
                throw new PublisherException($"Snapshot artifact is missing: {entry.Key}");
            var expected = entry.Value?.GetValue<string>();
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new PublisherException($"Snapshot artifact hash mismatch: {entry.Key}");
        }
        var indexPath = Path.Combine(snapshot, "Public", "index.db");
        EnsureSafeExistingPath(indexPath);
        if (!File.Exists(indexPath))
            throw new PublisherException("Snapshot v2 index is missing.");
        var indexHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))).ToLowerInvariant();
        if (!string.Equals(indexHash, metadata["indexHash"]?.GetValue<string>(), StringComparison.Ordinal))
            throw new PublisherException("Snapshot v2 index hash does not match snapshot metadata.");
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        using (var schema = database.CreateCommand())
        {
            schema.CommandText = "SELECT name,value FROM metadata WHERE name IN ('majorVersion','minorVersion')";
            using var schemaReader = schema.ExecuteReader();
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            while (schemaReader.Read())
                versions[schemaReader.GetString(0)] = schemaReader.GetString(1);
            if (versions.GetValueOrDefault("majorVersion") != "2" || versions.GetValueOrDefault("minorVersion") != "0")
                throw new PublisherException("Unsupported snapshot index schema; expected 2.0.");
        }
        using var integrity = database.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new PublisherException("Snapshot v2 index failed SQLite integrity_check.");

        using var packages = database.CreateCommand();
        packages.CommandText = "SELECT id,latest_version,hash FROM packages";
        using var reader = packages.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var latestVersion = reader.GetString(1);
            var hash = (byte[])reader[2];
            if (hash.Length != 32)
                throw new PublisherException($"Package '{id}' has an invalid sidecar hash length.");
            var hashHex = Convert.ToHexString(hash).ToLowerInvariant();
            var relative = $"packages/{id}/{hashHex[..8]}/versionData.mszyml";
            var sidecar = ResolveContained(feed, relative);
            if (!File.Exists(sidecar))
                throw new PublisherException($"Package '{id}' has a missing version-data sidecar.");
            var sidecarBytes = File.ReadAllBytes(sidecar);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sidecarBytes), hash))
                throw new PublisherException($"Package '{id}' has a missing or invalid version-data sidecar.");
            VerifyVersionData(feed, id, latestVersion, MsZipCompressor.Decompress(sidecarBytes));
        }

    }

    internal static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        EnsureSafeExistingPath(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new PublisherException($"Reparse point is not allowed: {entry}");
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else
                    yield return entry;
            }
    }
    private static void VerifyVersionData(string feed, string packageId, string latestVersion, byte[] bytes)
    {
        try
        {
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                throw new PublisherException($"Package '{packageId}' has malformed version data.");
            var schema = root.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode scalar && scalar.Value == "sV").Value as YamlScalarNode;
            if (schema?.Value != "1.0")
                throw new PublisherException($"Package '{packageId}' uses an unsupported version-data schema.");
            var versionsNode = root.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode scalar && scalar.Value == "vD").Value;
            if (versionsNode is not YamlSequenceNode versions)
                throw new PublisherException($"Package '{packageId}' version data is missing vD.");
            var latestFound = false;
            var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in versions.Children.OfType<YamlMappingNode>())
            {
                var version = Scalar(node, "v");
                var relative = Scalar(node, "rP");
                var expectedHash = Scalar(node, "s256H");
                if (version is null || relative is null || expectedHash is null)
                    throw new PublisherException($"Package '{packageId}' has an incomplete version-data entry.");
                if (!seenVersions.Add(version))
                    throw new PublisherException($"Package '{packageId}' contains duplicate version-data records.");
                latestFound |= string.Equals(version, latestVersion, StringComparison.OrdinalIgnoreCase);
                var manifest = ResolveContained(feed, relative);
                if (!File.Exists(manifest))
                    throw new PublisherException($"Version data references missing manifest: {relative}");
                var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))).ToLowerInvariant();
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    throw new PublisherException($"Version data manifest hash mismatch: {relative}");
                var model = JsonNode.Parse(File.ReadAllBytes(manifest)) as JsonObject;
                if (model?["PackageIdentifier"]?.GetValue<string>() != packageId || model["PackageVersion"]?.GetValue<string>() != version)
                    throw new PublisherException($"Version data manifest identity mismatch: {relative}");
            }
            if (!latestFound)
                throw new PublisherException($"Package '{packageId}' version data does not include latest version '{latestVersion}'.");
        }
        catch (PublisherException) { throw; }
        catch (Exception ex) { throw new PublisherException($"Could not verify version data for package '{packageId}': {ex.Message}", ex); }
    }

    private static string? Scalar(YamlMappingNode mapping, string name) =>
        mapping.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode key && key.Value == name).Value is YamlScalarNode value
            ? value.Value
            : null;

    private static int CountSnapshotArtifacts(string snapshot) =>
        Directory.EnumerateFiles(Path.Combine(snapshot, "feed"), "*", SearchOption.AllDirectories).Count() + 1;

    private static string ComputeBuildId(IReadOnlyDictionary<string, string> inventory)
    {
        var canonical = "pinget-publisher-state-v2\n" + WriteInventory(inventory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    // Same bytes as the Rust publisher's serde_json inventory, so equivalent trees share a build ID.
    private static string WriteInventory(IReadOnlyDictionary<string, string> inventory) =>
        "{" + string.Join(",", inventory.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(entry => ManifestScanner.JsonQuote(entry.Key) + ":" + ManifestScanner.JsonQuote(entry.Value))) + "}";

    private static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    internal static string ResolveContained(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or ".."
            || part.Any(c => c < ' ' || "\\:*?\"<>|".Contains(c)) || !ManifestScanner.IsWindowsSafeName(part)))
            throw new PublisherException($"Unsafe publisher relative path: '{relative}'.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PublisherException($"Path escapes publisher root: '{relative}'.");
        return fullPath;
    }

    internal static void EnsureDisjoint(string first, string second, string firstLabel, string secondLabel)
    {
        var a = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsWithin(a, b) || IsWithin(b, a))
            throw new PublisherException($"The {firstLabel} and {secondLabel} directories must not overlap.");
    }

    internal static bool IsWithin(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(Path.GetFullPath(root), fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static void EnsureSafeExistingPath(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new PublisherException($"Reparse points are not allowed in publisher paths: {path}");
        var full = Path.GetFullPath(path);
        var current = new DirectoryInfo(full);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PublisherException($"Reparse points are not allowed in publisher paths: {current.FullName}");
            current = current.Parent;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    [GeneratedRegex(@"^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdPattern();
}
