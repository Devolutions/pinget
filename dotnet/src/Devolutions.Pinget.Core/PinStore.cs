using Microsoft.Data.Sqlite;

namespace Devolutions.Pinget.Core;

internal static class PinStore
{
    public static List<PinRecord> List(string? appRoot = null, string? sourceId = null)
    {
        var dbPath = SourceStoreManager.PinsDbPath(appRoot);
        if (!File.Exists(dbPath)) return [];

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            cmd.CommandText = "SELECT package_id, version, source_id, pin_type FROM pin";
        }
        else
        {
            cmd.CommandText = "SELECT package_id, version, source_id, pin_type FROM pin WHERE source_id = @src";
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
                PinType = reader.GetInt64(3) switch
                {
                    1 => PinType.Blocking,
                    2 => PinType.Gating,
                    _ => PinType.Pinning
                }
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

        using var createCmd = conn.CreateCommand();
        createCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS pin (
                package_id TEXT NOT NULL,
                version TEXT NOT NULL DEFAULT '*',
                source_id TEXT NOT NULL DEFAULT '',
                pin_type INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (package_id, source_id)
            )";
        createCmd.ExecuteNonQuery();

        int typeInt = pinType switch
        {
            PinType.Blocking => 1,
            PinType.Gating => 2,
            _ => 0
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO pin (package_id, version, source_id, pin_type) VALUES (@id, @ver, @src, @type)";
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
            cmd.CommandText = "DELETE FROM pin WHERE package_id = @id";
        }
        else
        {
            cmd.CommandText = "DELETE FROM pin WHERE package_id = @id AND source_id = @src";
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
                cmd.CommandText = "DELETE FROM pin WHERE source_id = @src";
                cmd.Parameters.AddWithValue("@src", sourceId);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
