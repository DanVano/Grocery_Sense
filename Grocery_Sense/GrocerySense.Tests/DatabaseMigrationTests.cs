using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GrocerySense.Data;

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

    // The staple scan is the one prices query with no item_id bound (ListStapleItemIds). Migration 9's
    // idx_prices_coalesced_date must let its date-range predicate SEARCH the index instead of SCANning the
    // whole table — otherwise the cost tracks total price history, not the ~90-day window.
    [Fact]
    public void Coalesced_date_index_serves_staple_scan()
    {
        var factory = NewFactory();
        Database.Initialize(factory);
        using var conn = factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT item_id, COUNT(*) AS line_count, COUNT(DISTINCT receipt_id) AS receipt_count
            FROM prices INDEXED BY idx_prices_coalesced_date
            WHERE item_id IS NOT NULL AND unit_price IS NOT NULL
              AND (source = 'receipt' OR receipt_id IS NOT NULL)
              AND date(COALESCE(date, created_at)) >= date('now', $since)
            GROUP BY item_id
            HAVING line_count >= $minLines OR receipt_count >= $minReceipts
            ORDER BY receipt_count DESC, line_count DESC
            """;
        cmd.Parameters.AddWithValue("$since", "-90 day");
        cmd.Parameters.AddWithValue("$minLines", 4);
        cmd.Parameters.AddWithValue("$minReceipts", 3);
        using var reader = cmd.ExecuteReader();
        var details = new List<string>();
        while (reader.Read()) details.Add(reader.GetString(3));

        // Must SEARCH the coalesced-date index, and must NOT fall back to scanning the prices table.
        Assert.Contains(details, d => d.Contains("idx_prices_coalesced_date", StringComparison.Ordinal));
        Assert.DoesNotContain(details, d => d.Contains("SCAN prices", StringComparison.Ordinal));
    }

    // Append-only content guard: the other tests check schema SHAPE, so an edited shipped migration that
    // still yields a working fresh install slips through — exactly what the "never edit a shipped
    // migration" rule forbids (it forks fresh installs from already-upgraded devices). Pin each entry's
    // text by hash. Appending migration N+1 = append one hash here; any other diff = a shipped edit.
    // Reflection (not InternalsVisibleTo) so the guard needs zero changes in Data; tests never run AOT.
    [Fact]
    public void Shipped_migration_text_is_pinned_append_only()
    {
        string[] expected =
        {
            "8DFBBB605F42", "99E952033B8D", "1438C084820C", "09F9DFAC0688", "0A6501CC0611",
            "7429205583F9", "D1C5E8CFA323", "5705D9AB3C21", "DA19DBE7B07B",
        };

        var field = typeof(Database).GetField("_migrations", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field); // renamed ledger field = update this guard deliberately
        var migrations = (string[])field!.GetValue(null)!;
        // Normalize newlines: the raw string literals inherit the checkout's line endings.
        var actual = migrations
            .Select(m => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Replace("\r\n", "\n"))))[..12])
            .ToArray();

        for (var i = 0; i < Math.Min(expected.Length, actual.Length); i++)
            Assert.True(expected[i] == actual[i],
                $"Shipped migration {i + 1} changed (hash {actual[i]}, pinned {expected[i]}). " +
                "Never edit a shipped migration — append a new one instead.");
        Assert.True(actual.Length >= expected.Length,
            $"Shipped migration(s) deleted: ledger has {actual.Length}, {expected.Length} pinned.");
        Assert.True(actual.Length == expected.Length,
            "New migration(s) appended — pin them by extending expected to:\n" +
            "{ " + string.Join(", ", actual.Select(h => $"\"{h}\"")) + " }");
    }

    // "Money = TEXT, never REAL" lives only in comments; nothing stops a future migration shipping a REAL
    // money column (floats drop cents). Sweep every money-pattern column; the allowlist names the
    // pre-existing REAL engine-math columns (normalized/derived comparison values, not money-of-record).
    [Fact]
    public void Money_pattern_columns_are_text_never_real()
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prices.norm_unit_price",            // normalized per-kg/each comparison value
            "price_drop_alerts.current_price",   // alert engine unit-price doubles (ported shape)
            "price_drop_alerts.usual_price",
            "watchlist.target_price",            // user-set threshold compared against engine doubles
        };
        string[] moneyPatterns = { "price", "amount", "total", "discount" };

        var factory = NewFactory();
        Database.Initialize(factory);
        using var conn = factory.Open();

        var offenders = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT m.name, p.name, p.type FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
            "WHERE m.type = 'table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);
            var declared = reader.GetString(2);
            if (!moneyPatterns.Any(p => column.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!declared.Contains("REAL", StringComparison.OrdinalIgnoreCase)) continue;
            if (!allowlist.Contains($"{table}.{column}")) offenders.Add($"{table}.{column} ({declared})");
        }

        Assert.True(offenders.Count == 0,
            "REAL money column(s) found — money must be TEXT round-tripping decimal (floats drop cents): " +
            string.Join(", ", offenders) +
            ". If a column is genuinely engine-math (not money-of-record), allowlist it here deliberately.");
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
