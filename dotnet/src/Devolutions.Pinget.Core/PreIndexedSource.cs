using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Devolutions.Pinget.Core;

internal static class PreIndexedSource
{
    private static readonly string[] MsixCandidates = ["source2.msix", "source.msix"];

    public static string IndexPath(SourceRecord source, string? appRoot = null)
    {
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        return Path.Combine(stateDir, "index.db");
    }

    public static string Update(HttpClient client, SourceRecord source, string? appRoot = null)
    {
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        Directory.CreateDirectory(stateDir);

        Exception? lastError = null;
        foreach (var candidate in MsixCandidates)
        {
            var url = $"{source.Arg.TrimEnd('/')}/{candidate}";
            try
            {
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) continue;

                var headerVersion = response.Headers.TryGetValues("x-ms-meta-sourceversion", out var values)
                    ? values.FirstOrDefault() : null;

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var msixPath = Path.Combine(stateDir, candidate);
                File.WriteAllBytes(msixPath, bytes);

                // Extract index.db from MSIX
                using var archive = ZipFile.OpenRead(msixPath);
                var indexEntry = archive.GetEntry("Public/index.db")
                    ?? throw new InvalidOperationException($"No Public/index.db in {candidate}");

                var indexPath = IndexPath(source, appRoot);
                indexEntry.ExtractToFile(indexPath, overwrite: true);

                source.SourceVersion = headerVersion;
                return $"Updated from {candidate}" + (headerVersion != null ? $" (v{headerVersion})" : "");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to update preindexed source '{source.Name}': {lastError?.Message}");
    }

    // V2 search: flat `packages` table with id, name, moniker, latest_version, hash
    public static (List<V2MatchRow> Rows, bool Truncated) SearchV2(
        SqliteConnection conn, PackageQuery query, SearchSemantics semantics)
    {
        int limit = MaxResults(query, semantics);
        var (whereClause, parameters) = BuildV2Where(query, semantics);

        var sql = $"SELECT rowid, id, name, moniker, latest_version, hash FROM packages WHERE {whereClause} LIMIT {limit + 1}";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < parameters.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i + 1}", parameters[i]);

        var rows = new List<V2MatchRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            var idVal = reader.GetString(1);
            var nameVal = reader.GetString(2);
            var monikerVal = reader.IsDBNull(3) ? null : reader.GetString(3);
            rows.Add(new V2MatchRow
            {
                PackageRowId = rowId,
                Id = idVal,
                Name = nameVal,
                Moniker = monikerVal,
                Version = reader.IsDBNull(4) ? "" : reader.GetString(4),
                PackageHash = reader.IsDBNull(5) ? "" : HexString(reader, 5),
                MatchCriteria = DetermineMatchCriteriaV2(conn, query, idVal, nameVal, monikerVal, rowId, semantics)
            });
        }

        bool truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return (rows, truncated);
    }

    // V1 fallback search: manifest JOIN ids/names/monikers/versions/channels
    public static (List<V1MatchRow> Rows, bool Truncated) SearchV1(
        SqliteConnection conn, PackageQuery query, SearchSemantics semantics)
    {
        int limit = MaxResults(query, semantics);
        var (whereClause, parameters) = BuildV1Where(query, semantics);

        var sql = $@"SELECT manifest.rowid, manifest.id, versions.version, channels.channel,
                            ids.id, names.name, monikers.moniker
                     FROM manifest
                     JOIN ids ON manifest.id = ids.rowid
                     JOIN names ON manifest.name = names.rowid
                     LEFT JOIN monikers ON manifest.moniker = monikers.rowid
                     JOIN versions ON manifest.version = versions.rowid
                     JOIN channels ON manifest.channel = channels.rowid
                     WHERE {whereClause} LIMIT {limit + 1}";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < parameters.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i + 1}", parameters[i]);

        var rawRows = new List<V1MatchRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var manifestRowId = reader.GetInt64(0);
            var idVal = reader.GetString(4);
            var nameVal = reader.GetString(5);
            var monikerVal = reader.IsDBNull(6) ? null : reader.GetString(6);
            rawRows.Add(new V1MatchRow
            {
                ManifestRowId = manifestRowId,
                PackageRowId = reader.GetInt64(1),
                Version = reader.GetString(2),
                Channel = reader.GetString(3),
                Id = idVal,
                Name = nameVal,
                Moniker = monikerVal,
                MatchCriteria = DetermineMatchCriteriaV1(conn, query, idVal, nameVal, monikerVal, manifestRowId, semantics)
            });
        }

        // Group by package — keep the row with the highest version for each package_rowid
        var grouped = new Dictionary<long, V1MatchRow>();
        foreach (var row in rawRows)
        {
            if (!grouped.TryGetValue(row.PackageRowId, out var existing) ||
                string.Compare(row.Version, existing.Version, StringComparison.OrdinalIgnoreCase) > 0)
            {
                grouped[row.PackageRowId] = row;
            }
        }
        var rows = grouped.Values.ToList();

        bool truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return (rows, truncated);
    }

    public static List<V1VersionRow> QueryV1Versions(SqliteConnection conn, long packageRowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.rowid, v.version, COALESCE(ch.channel, '') AS channel,
                   pp.pathpart, COALESCE(m.hash, '') AS hash
            FROM manifest m
            JOIN versions v ON v.rowid = m.version
            LEFT JOIN channels ch ON ch.rowid = m.channel
            LEFT JOIN pathparts pp ON pp.rowid = m.pathpart
            WHERE m.id = @id
            ORDER BY v.version DESC";
        cmd.Parameters.AddWithValue("@id", packageRowid);

        var rows = new List<V1VersionRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new V1VersionRow
            {
                ManifestRowId = reader.GetInt64(0),
                Version = reader.GetString(1),
                Channel = reader.GetString(2),
                PathPart = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                ManifestHash = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        return rows;
    }

    public static string ResolveV1RelativePath(SqliteConnection conn, long? pathPartId)
    {
        if (pathPartId is null) return "";
        var parts = new List<string>();
        long? current = pathPartId;
        while (current is not null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT parent, pathpart FROM pathparts WHERE rowid = @id";
            cmd.Parameters.AddWithValue("@id", current.Value);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) break;
            current = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            parts.Add(reader.GetString(1));
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // V2 version data: fetch versionData.mszyml from CDN, decompress CK+deflate → YAML
    public static (List<V2VersionDataEntry> Entries, string VersionDataFile) LoadV2VersionData(
        HttpClient client, SqliteConnection conn, SourceRecord source, long packageRowid, string packageHash, string? appRoot = null)
    {
        // Resolve package id from packages table
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT id FROM packages WHERE rowid = @rowid";
        idCmd.Parameters.AddWithValue("@rowid", packageRowid);
        var packageId = idCmd.ExecuteScalar()?.ToString()
            ?? throw new InvalidOperationException($"Package rowid {packageRowid} not found");

        var hashPrefix = packageHash.Length >= 8 ? packageHash[..8].ToLowerInvariant() : packageHash.ToLowerInvariant();
        var relativePath = $"packages/{packageId}/{hashPrefix}/versionData.mszyml";

        var bytes = GetCachedSourceFile(client, "V2_PVD", source, relativePath, packageHash);
        var yaml = DecompressMszyml(bytes);

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
        var doc = deserializer.Deserialize<V2VersionDataDocument>(yaml)
            ?? throw new InvalidOperationException("Failed to parse versionData.mszyml");

        var cacheDir = TempCachePath("V2_PVD", source.Identifier);
        return (doc.Versions, Path.Combine(cacheDir, relativePath.Replace('/', '\\')));
    }

    public static byte[] GetCachedSourceFile(
        HttpClient client, string bucket, SourceRecord source, string relativePath, string? expectedHash)
    {
        var normalizedRelative = relativePath.Replace('\\', '/');
        var cacheDir = TempCachePath(bucket, source.Identifier);
        var cachePath = Path.Combine(cacheDir, normalizedRelative.Replace('/', '\\'));

        // Check cache first
        if (File.Exists(cachePath))
        {
            var cached = File.ReadAllBytes(cachePath);
            if (expectedHash is null || HashMatches(expectedHash, cached))
                return cached;
        }

        // Download from CDN
        var url = $"{source.Arg.TrimEnd('/')}/{normalizedRelative.TrimStart('/')}";
        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        // Persist to cache
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllBytes(cachePath, bytes);

        return bytes;
    }

    public static byte[] GetCachedSourceFileFromMsix(SourceRecord source, string relativePath, string? appRoot = null)
    {
        var stateDir = SourceStoreManager.SourceStateDir(source, appRoot);
        foreach (var candidate in MsixCandidates)
        {
            var msixPath = Path.Combine(stateDir, candidate);
            if (!File.Exists(msixPath)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(msixPath);
                var entry = archive.GetEntry($"Public/{relativePath}") ?? archive.GetEntry(relativePath);
                if (entry is null) continue;

                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch { /* try next candidate */ }
        }

        throw new InvalidOperationException($"Source file '{relativePath}' not found in MSIX for {source.Name}");
    }

    private static string TempCachePath(string bucket, string sourceIdentifier)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                SourceStoreManager.GetPackagedFileCacheRoot(null),
                bucket,
                sourceIdentifier);
        }

        var tempDir = Path.GetTempPath();
        return Path.Combine(tempDir, "pinget", bucket, sourceIdentifier);
    }

    private static bool HashMatches(string? expected, byte[] data)
    {
        if (expected is null) return true;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static string DecompressMszyml(byte[] bytes)
    {
        // .mszyml files start with "CK" header then deflate-compressed data
        byte[] payload;
        if (bytes.Length >= 2 && bytes[0] == (byte)'C' && bytes[1] == (byte)'K')
            payload = bytes[2..];
        else
        {
            // Try to find CK marker
            int ckPos = -1;
            for (int i = 0; i < bytes.Length - 1; i++)
            {
                if (bytes[i] == (byte)'C' && bytes[i + 1] == (byte)'K')
                {
                    ckPos = i;
                    break;
                }
            }
            payload = ckPos >= 0 ? bytes[(ckPos + 2)..] : bytes;
        }

        using var deflateStream = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
        using var result = new MemoryStream();
        deflateStream.CopyTo(result);
        return System.Text.Encoding.UTF8.GetString(result.ToArray());
    }

    // V2 WHERE clause builder — operates on the flat `packages` table
    private static (string WhereClause, List<string> Parameters) BuildV2Where(
        PackageQuery query, SearchSemantics semantics)
    {
        var parameters = new List<string>();
        bool exact = query.Exact || semantics == SearchSemantics.Single;

        if (query.Id is not null)
            return (SingleFieldCondition("id", query.Id, exact, parameters), parameters);
        if (query.Name is not null)
            return (SingleFieldCondition("name", query.Name, exact, parameters), parameters);
        if (query.Moniker is not null)
            return (SingleFieldCondition("moniker", query.Moniker, exact, parameters), parameters);
        if (query.Tag is not null)
            return (MappedFieldCondition(true, "tag", query.Tag, "packages.rowid", true, parameters), parameters);
        if (query.Command is not null)
            return (MappedFieldCondition(true, "command", query.Command, "packages.rowid", true, parameters), parameters);

        if (query.Query is not null)
        {
            if (exact)
            {
                var conditions = new[]
                {
                    SingleFieldCondition("id", query.Query, true, parameters),
                    SingleFieldCondition("name", query.Query, true, parameters),
                    SingleFieldCondition("moniker", query.Query, true, parameters),
                };
                return ($"({string.Join(" OR ", conditions)})", parameters);
            }
            else
            {
                var conditions = new List<string>
                {
                    SingleFieldCondition("id", query.Query, false, parameters),
                    SingleFieldCondition("name", query.Query, false, parameters),
                    SingleFieldCondition("moniker", query.Query, false, parameters),
                };
                if (semantics == SearchSemantics.Many)
                {
                    conditions.Add(MappedFieldCondition(true, "tag", query.Query, "packages.rowid", false, parameters));
                    conditions.Add(MappedFieldCondition(true, "command", query.Query, "packages.rowid", false, parameters));
                }
                return ($"({string.Join(" OR ", conditions)})", parameters);
            }
        }

        return ("1 = 1", parameters);
    }

    // V1 WHERE clause builder — operates on the manifest/ids/names/monikers join
    private static (string WhereClause, List<string> Parameters) BuildV1Where(
        PackageQuery query, SearchSemantics semantics)
    {
        var parameters = new List<string>();
        bool exact = query.Exact || semantics == SearchSemantics.Single;

        if (query.Id is not null)
            return (SingleFieldCondition("ids.id", query.Id, exact, parameters), parameters);
        if (query.Name is not null)
            return (SingleFieldCondition("names.name", query.Name, exact, parameters), parameters);
        if (query.Moniker is not null)
            return (SingleFieldCondition("monikers.moniker", query.Moniker, exact, parameters), parameters);
        if (query.Tag is not null)
            return (MappedFieldCondition(false, "tag", query.Tag, "manifest.rowid", true, parameters), parameters);
        if (query.Command is not null)
            return (MappedFieldCondition(false, "command", query.Command, "manifest.rowid", true, parameters), parameters);

        if (query.Query is not null)
        {
            if (exact)
            {
                var conditions = new[]
                {
                    SingleFieldCondition("ids.id", query.Query, true, parameters),
                    SingleFieldCondition("names.name", query.Query, true, parameters),
                    SingleFieldCondition("monikers.moniker", query.Query, true, parameters),
                };
                return ($"({string.Join(" OR ", conditions)})", parameters);
            }
            else
            {
                var conditions = new List<string>
                {
                    SingleFieldCondition("ids.id", query.Query, false, parameters),
                    SingleFieldCondition("names.name", query.Query, false, parameters),
                    SingleFieldCondition("monikers.moniker", query.Query, false, parameters),
                };
                if (semantics == SearchSemantics.Many)
                {
                    conditions.Add(MappedFieldCondition(false, "tag", query.Query, "manifest.rowid", false, parameters));
                    conditions.Add(MappedFieldCondition(false, "command", query.Query, "manifest.rowid", false, parameters));
                }
                return ($"({string.Join(" OR ", conditions)})", parameters);
            }
        }

        return ("1 = 1", parameters);
    }

    private static string SingleFieldCondition(string column, string value, bool exact, List<string> parameters)
    {
        parameters.Add(MatchParameter(value, exact));
        return $"{column} LIKE @p{parameters.Count}";
    }

    private static string MappedFieldCondition(
        bool v2, string valueName, string value, string rowidColumn, bool exact, List<string> parameters)
    {
        string tableName, mapTableName, mapValueColumn, mapOwnerColumn;
        if (v2)
        {
            tableName = $"{valueName}s2";
            mapTableName = $"{valueName}s2_map";
            mapValueColumn = valueName;
            mapOwnerColumn = "package";
        }
        else
        {
            tableName = $"{valueName}s";
            mapTableName = $"{valueName}s_map";
            mapValueColumn = valueName;
            mapOwnerColumn = "manifest";
        }
        parameters.Add(MatchParameter(value, exact));
        int paramNum = parameters.Count;
        return $"EXISTS (SELECT 1 FROM {mapTableName} JOIN {tableName} ON " +
               $"{mapTableName}.{valueName} = {tableName}.rowid " +
               $"WHERE {mapTableName}.{mapOwnerColumn} = {rowidColumn} " +
               $"AND {tableName}.{mapValueColumn} LIKE @p{paramNum})";
    }

    private static string MatchParameter(string value, bool exact)
        => exact ? value : $"%{value}%";

    private static string HexString(SqliteDataReader reader, int ordinal)
    {
        // The hash column is stored as a blob in SQLite; convert to lowercase hex
        var value = reader.GetValue(ordinal);
        if (value is byte[] blob)
            return Convert.ToHexString(blob).ToLowerInvariant();
        return value?.ToString()?.ToLowerInvariant() ?? "";
    }

    private static string? DetermineMatchCriteria(SqliteConnection conn, PackageQuery query,
        string id, string name, string? moniker, long ownerRowId,
        string tagTable, string tagMapTable, string tagColumn, string ownerColumn,
        string cmdTable, string cmdMapTable, string cmdColumn,
        SearchSemantics semantics)
    {
        if (query.Tag is not null) return $"Tag: {query.Tag}";
        if (query.Command is not null) return $"Command: {query.Command}";
        if (query.Moniker is not null) return $"Moniker: {moniker ?? query.Moniker}";
        if (semantics == SearchSemantics.Single) return null;
        if (query.Query is null) return null;

        var q = query.Query;
        bool exact = query.Exact;
        if (MatchesText(id, q, exact) || MatchesText(name, q, exact))
            return null;

        if (moniker is not null && MatchesText(moniker, q, exact))
            return $"Moniker: {moniker}";

        var tag = FindMappedValue(conn, tagTable, tagMapTable, tagColumn, ownerColumn, ownerRowId, q, exact);
        if (tag is not null) return $"Tag: {tag}";

        var cmd = FindMappedValue(conn, cmdTable, cmdMapTable, cmdColumn, ownerColumn, ownerRowId, q, exact);
        if (cmd is not null) return $"Command: {cmd}";

        return null;
    }

    private static bool MatchesText(string text, string query, bool exact)
    {
        return exact
            ? text.Equals(query, StringComparison.OrdinalIgnoreCase)
            : text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindMappedValue(SqliteConnection conn, string table, string mapTable,
        string valueName, string ownerColumn, long ownerRowId, string query, bool exact)
    {
        var likeParam = exact ? query : $"%{query}%";
        var sql = $"SELECT {table}.{valueName} FROM {mapTable} " +
                  $"JOIN {table} ON {mapTable}.{valueName} = {table}.rowid " +
                  $"WHERE {mapTable}.{ownerColumn} = @owner AND {table}.{valueName} LIKE @q";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@owner", ownerRowId);
        cmd.Parameters.AddWithValue("@q", likeParam);
        using var reader = cmd.ExecuteReader();
        string? best = null;
        while (reader.Read())
        {
            var val = reader.GetString(0);
            if (best is null || val.Length < best.Length) best = val;
        }
        return best;
    }

    // Convenience overloads
    internal static string? DetermineMatchCriteriaV2(SqliteConnection conn, PackageQuery query,
        string id, string name, string? moniker, long packageRowId, SearchSemantics semantics) =>
        DetermineMatchCriteria(conn, query, id, name, moniker, packageRowId,
            "tags2", "tags2_map", "tag", "package",
            "commands2", "commands2_map", "command",
            semantics);

    internal static string? DetermineMatchCriteriaV1(SqliteConnection conn, PackageQuery query,
        string id, string name, string? moniker, long manifestRowId, SearchSemantics semantics) =>
        DetermineMatchCriteria(conn, query, id, name, moniker, manifestRowId,
            "tags", "tags_map", "tag", "manifest",
            "commands", "commands_map", "command",
            semantics);

    private static int MaxResults(PackageQuery query, SearchSemantics semantics)
    {
        if (semantics == SearchSemantics.Single) return 2;
        return Math.Max(query.Count ?? 50, 50);
    }

    // Internal row types
    internal record V2MatchRow
    {
        public long PackageRowId { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Moniker { get; init; }
        public required string Version { get; init; }
        public required string PackageHash { get; init; }
        public string? MatchCriteria { get; init; }
    }

    internal record V1MatchRow
    {
        public long ManifestRowId { get; init; }
        public long PackageRowId { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Moniker { get; init; }
        public required string Version { get; init; }
        public required string Channel { get; init; }
        public string? MatchCriteria { get; init; }
    }

    internal record V1VersionRow
    {
        public long ManifestRowId { get; init; }
        public required string Version { get; init; }
        public required string Channel { get; init; }
        public long? PathPart { get; init; }
        public string? ManifestHash { get; init; }
    }

    internal class V2VersionDataEntry
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "v")]
        public string Version { get; set; } = "";
        [YamlDotNet.Serialization.YamlMember(Alias = "rP")]
        public string ManifestRelativePath { get; set; } = "";
        [YamlDotNet.Serialization.YamlMember(Alias = "s256H")]
        public string ManifestHash { get; set; } = "";
    }

    internal class V2VersionDataDocument
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "vD")]
        public List<V2VersionDataEntry> Versions { get; set; } = [];
    }
}
