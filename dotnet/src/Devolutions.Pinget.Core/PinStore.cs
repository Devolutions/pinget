using Microsoft.Data.Sqlite;

namespace Devolutions.Pinget.Core;

internal static class PinStore
{
    private const string CurrentPinTypeColumn = "type";
    private const string LegacyPinTypeColumn = "pin_type";

    public static List<PinRecord> List(string? appRoot = null, string? sourceId = null)
    {
        var dbPath = SourceStoreManager.PinsDbPath(appRoot);
        if (!File.Exists(dbPath)) return [];

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var pinTypeColumn = ResolvePinTypeColumn(conn);
        if (pinTypeColumn is null) return [];

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            cmd.CommandText = $"SELECT package_id, version, source_id, {pinTypeColumn} FROM pin";
        }
        else
        {
            cmd.CommandText = $"SELECT package_id, version, source_id, {pinTypeColumn} FROM pin WHERE source_id = @src COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@src", sourceId);
        }

        var pins = new List<PinRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            pins.Add(new PinRecord
            {
                PackageId = reader.GetString(0),
                Version = reader.GetString(1),
                SourceId = reader.GetString(2),
                PinType = ToPinType(reader.GetInt64(3))
            });
        }
        return pins;
    }

    public static void Add(string packageId, string version, string sourceId, PinType pinType, string? appRoot = null)
    {
        SourceStoreManager.EnsureAppDirs(appRoot);
        var dbPath = SourceStoreManager.PinsDbPath(appRoot);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        EnsureSchema(conn);

        var pinTypeColumn = ResolvePinTypeColumn(conn) ?? CurrentPinTypeColumn;

        int typeInt = pinType switch
        {
            PinType.Blocking => 4,
            PinType.Gating => 3,
            _ => 2
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO pin (package_id, version, source_id, {pinTypeColumn}) VALUES (@id, @ver, @src, @type)";
        cmd.Parameters.AddWithValue("@id", packageId);
        cmd.Parameters.AddWithValue("@ver", version);
        cmd.Parameters.AddWithValue("@src", sourceId);
        cmd.Parameters.AddWithValue("@type", typeInt);
        cmd.ExecuteNonQuery();
    }

    public static bool Remove(string packageId, string? appRoot = null, string? sourceId = null)
    {
        var dbPath = SourceStoreManager.PinsDbPath(appRoot);
        if (!File.Exists(dbPath)) return false;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            cmd.CommandText = "DELETE FROM pin WHERE package_id = @id COLLATE NOCASE";
        }
        else
        {
            cmd.CommandText = "DELETE FROM pin WHERE package_id = @id COLLATE NOCASE AND source_id = @src COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@src", sourceId);
        }
        cmd.Parameters.AddWithValue("@id", packageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static void Reset(string? appRoot = null, string? sourceId = null)
    {
        var dbPath = SourceStoreManager.PinsDbPath(appRoot);
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
        {
            if (string.IsNullOrWhiteSpace(sourceId))
            {
                File.Delete(dbPath);
            }
            else
            {
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM pin WHERE source_id = @src COLLATE NOCASE";
                cmd.Parameters.AddWithValue("@src", sourceId);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Creates WinGet's pinning database schema (PinningIndex 1.0) when it is missing. WinGet opens
    /// pinning.db through SQLiteStorageBase, which reads metadata.majorVersion/minorVersion; a file
    /// without them is treated as corrupt and deleted, losing every pin. The pin table and its unique
    /// index match Pinning_1_0/PinTable.cpp.
    /// </summary>
    internal static void EnsureSchema(SqliteConnection conn)
    {
        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS [metadata]([name] TEXT PRIMARY KEY NOT NULL,[value] TEXT NOT NULL) WITHOUT ROWID;
            INSERT OR IGNORE INTO [metadata] ([name], [value]) VALUES ('majorVersion', '1');
            INSERT OR IGNORE INTO [metadata] ([name], [value]) VALUES ('minorVersion', '0');
            CREATE TABLE IF NOT EXISTS pin (package_id TEXT NOT NULL, source_id TEXT NOT NULL, type INT64 NOT NULL, version TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS pin_index ON pin (package_id, source_id);
            """;
        createCmd.ExecuteNonQuery();
    }

    private static string? ResolvePinTypeColumn(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(pin)";

        bool hasCurrentColumn = false;
        bool hasLegacyColumn = false;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var columnName = reader.GetString(1);
            if (string.Equals(columnName, CurrentPinTypeColumn, StringComparison.OrdinalIgnoreCase))
            {
                hasCurrentColumn = true;
            }
            else if (string.Equals(columnName, LegacyPinTypeColumn, StringComparison.OrdinalIgnoreCase))
            {
                hasLegacyColumn = true;
            }
        }

        if (hasCurrentColumn) return CurrentPinTypeColumn;
        if (hasLegacyColumn) return LegacyPinTypeColumn;

        return null;
    }

    private static PinType ToPinType(long pinTypeValue)
    {
        return pinTypeValue switch
        {
            4 => PinType.Blocking,
            3 => PinType.Gating,
            _ => PinType.Pinning
        };
    }
}
