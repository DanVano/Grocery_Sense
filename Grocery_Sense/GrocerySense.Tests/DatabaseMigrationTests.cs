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
            "price_drop_alerts", "watchlist", "member_requests", "user_recipes",
        };
        foreach (var t in expected)
            Assert.Contains(t, tables);

        // Still deferred (no such table): the old per-member user_profile table.
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

    [Fact]
    public void Migration_adds_priority_column_defaulting_normal()
    {
        var factory = NewFactory();
        Database.Initialize(factory);

        using var conn = factory.Open();
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO shopping_list (display_name) VALUES ('Milk');";
        insert.ExecuteNonQuery();

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT priority FROM shopping_list WHERE display_name = 'Milk';";
        Assert.Equal("normal", read.ExecuteScalar() as string);
    }

    [Fact]
    public void Item_name_nocase_index_serves_exact_lookup()
    {
        var factory = NewFactory();
        Database.Initialize(factory);
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "EXPLAIN QUERY PLAN SELECT id FROM items WHERE canonical_name = $name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$name", "milk");
        using var reader = cmd.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));
        Assert.Contains(details, d => d.Contains("idx_items_name_nocase", StringComparison.Ordinal));
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
