using GrocerySense.Data;
using Xunit;

namespace GrocerySense.Tests;

// Migration-ledger guarantees: a fresh DB reaches the latest version with every table, and re-running
// Initialize is a no-op that never drops or rewrites existing rows. (Legacy Python-shape migration is
// N/A — v1 is a clean start, so "old shape" here means an empty DB at version 0.)
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"gs_migrate_{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory NewFactory() => new(_dbPath);

    [Fact]
    public void Fresh_database_reaches_latest_version_with_all_tables()
    {
        Database.Initialize(NewFactory());

        using var conn = NewFactory().Open();
        Assert.Equal(Database.LatestVersion, ReadVersion(conn));

        var tables = ReadTableNames(conn);
        string[] expected =
        {
            "schema_version", "stores", "items", "receipts", "flyer_sources", "prices",
            "receipt_line_items", "item_aliases", "shopping_list", "deleted_receipt_backups",
            "receipt_raw_json", "receipt_file_hashes", "receipt_signatures",
            "flyer_batches", "flyer_assets", "flyer_raw_json", "flyer_deals",
        };
        foreach (var t in expected)
            Assert.Contains(t, tables);

        // v1 scope: these are deferred and must NOT be created.
        Assert.DoesNotContain("member_requests", tables);
        Assert.DoesNotContain("user_profile", tables);
    }

    [Fact]
    public void Initialize_is_idempotent_and_preserves_rows()
    {
        var factory = NewFactory();
        Database.Initialize(factory);

        using (var conn = factory.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO stores (name) VALUES ('Test Store');";
            cmd.ExecuteNonQuery();
        }

        // Second run must apply nothing: same version, row untouched, no exception.
        Database.Initialize(factory);

        using var verify = factory.Open();
        Assert.Equal(Database.LatestVersion, ReadVersion(verify));
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT name FROM stores;";
        Assert.Equal("Test Store", check.ExecuteScalar() as string);
    }

    private static int ReadVersion(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    private static HashSet<string> ReadTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    public void Dispose()
    {
        // WAL leaves -wal/-shm siblings; best-effort cleanup.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* temp file, ignore */ }
        }
    }
}
