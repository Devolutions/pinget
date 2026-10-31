using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pinget.Publisher;

internal static class WinGetV2IndexWriter
{
    public static void Write(string path, IReadOnlyList<IndexedPackage> packages, string databaseIdentifier)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = EmbeddedSql("index-schema.sql");
            schema.ExecuteNonQuery();
        }

        using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "INSERT INTO metadata(name,value) VALUES($name,$value)";
            var name = metadata.Parameters.Add("$name", SqliteType.Text);
            var value = metadata.Parameters.Add("$value", SqliteType.Text);
            void Set(string key, string data)
            {
                name.Value = key;
                value.Value = data;
                metadata.ExecuteNonQuery();
            }
            Set("majorVersion", "2");
            Set("minorVersion", "0");
            Set("databaseIdentifier", $"{{{databaseIdentifier.ToUpperInvariant()}}}");
            Set("lastwritetime", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var packageId = InsertPackage(connection, transaction, package);
            InsertMap(connection, transaction, "tags2", "tags2_map", "tag", package.Tags, packageId);
            InsertMap(connection, transaction, "commands2", "commands2_map", "command", package.Commands, packageId);
            InsertValues(connection, transaction, "pfns2", "pfn", package.Pfns, packageId);
            InsertValues(connection, transaction, "productcodes2", "productcode", package.ProductCodes, packageId);
            InsertValues(connection, transaction, "upgradecodes2", "upgradecode", package.UpgradeCodes, packageId);
            InsertValues(connection, transaction, "norm_names2", "norm_name", package.NormalizedNames, packageId);
            InsertValues(connection, transaction, "norm_publishers2", "norm_publisher", package.NormalizedPublishers, packageId);
        }
        transaction.Commit();
        connection.Close();
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        check.Open();
        using var integrity = check.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new PublisherException("Generated source index failed SQLite integrity_check.");
    }

    private static long InsertPackage(SqliteConnection connection, SqliteTransaction transaction, IndexedPackage package)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO packages(id,name,moniker,latest_version,arp_min_version,arp_max_version,hash)
            VALUES($id,$name,$moniker,$version,$arpMin,$arpMax,$hash);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$id", package.Id);
        command.Parameters.AddWithValue("$name", package.Name);
        command.Parameters.AddWithValue("$moniker", (object?)package.Moniker ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", package.LatestVersion);
        command.Parameters.AddWithValue("$arpMin", (object?)package.ArpMinimumVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$arpMax", (object?)package.ArpMaximumVersion ?? DBNull.Value);
        command.Parameters.Add("$hash", SqliteType.Blob).Value = package.PackageHash;
        return (long)command.ExecuteScalar()!;
    }

    private static void InsertMap(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string valueTable,
        string mapTable,
        string column,
        IReadOnlyList<string> values,
        long packageId)
    {
        foreach (var value in values)
        {
            long valueId;
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = $"SELECT rowid FROM {valueTable} WHERE {column}=$value COLLATE NOCASE LIMIT 1";
                find.Parameters.AddWithValue("$value", value);
                var existing = find.ExecuteScalar();
                if (existing is not null)
                {
                    valueId = (long)existing;
                    using var canonical = connection.CreateCommand();
                    canonical.Transaction = transaction;
                    canonical.CommandText = $"SELECT {column} FROM {valueTable} WHERE rowid=$id";
                    canonical.Parameters.AddWithValue("$id", valueId);
                    if (StringComparer.Ordinal.Compare(value, canonical.ExecuteScalar() as string) < 0)
                    {
                        canonical.CommandText = $"UPDATE {valueTable} SET {column}=$value WHERE rowid=$id";
                        canonical.Parameters.AddWithValue("$value", value);
                        canonical.ExecuteNonQuery();
                    }
                }
                else
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = $"INSERT INTO {valueTable}({column}) VALUES($value); SELECT last_insert_rowid();";
                    insert.Parameters.AddWithValue("$value", value);
                    valueId = (long)insert.ExecuteScalar()!;
                }
            }
            using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = $"INSERT INTO {mapTable}({column},package) VALUES($value,$package)";
            map.Parameters.AddWithValue("$value", valueId);
            map.Parameters.AddWithValue("$package", packageId);
            map.ExecuteNonQuery();
        }
    }

    private static void InsertValues(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        IReadOnlyList<string> values,
        long packageId)
    {
        foreach (var value in values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT OR IGNORE INTO {table}({column},package) VALUES($value,$package)";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$package", packageId);
            command.ExecuteNonQuery();
        }
    }

    internal static string EmbeddedSql(string name)
    {
        var assembly = typeof(WinGetV2IndexWriter).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith($".{name}", StringComparison.Ordinal))
            ?? throw new PublisherException($"Embedded publisher SQL resource is missing: {name}");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
