using GrocerySense.Core;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

// P1-5: backup → stage → cold-start swap. Validation happens on a private copy, the live DB is only
// replaced during the cold-start half, and every crash point recovers deterministically.
//
// Serialized against the whole suite: CompletePendingRestore (and this class's Dispose) call
// SqliteConnection.ClearAllPools(), which is PROCESS-WIDE — run in parallel it can dispose a pooled
// handle another test is concurrently fetching (ObjectDisposedException on sqlite3). Production is
// unaffected: the swap runs at cold start before any DB consumer exists.
[CollectionDefinition("restore-staging", DisableParallelization = true)]
public sealed class RestoreStagingCollection { }

[Collection("restore-staging")]
public sealed class RestoreStagingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_restore_{Guid.NewGuid():N}");
    public RestoreStagingTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string NewDbPath(string name) => Path.Combine(_dir, name);

    // A migrated DB with one receipt whose TEXT money cell is exactly `total`.
    private SqliteConnectionFactory SeededDb(string name, string total)
    {
        var factory = new SqliteConnectionFactory(NewDbPath(name));
        Database.Initialize(factory);
        using var conn = factory.Open();
        var store = StoresRepo.CreateStore(conn, "Loblaws").Id;
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, total_amount, source) VALUES ($s, '2026-06-01', $t, 'receipt')";
        cmd.Parameters.AddWithValue("$s", store);
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
        return factory;
    }

    private static object? Scalar(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Backup_stage_and_cold_swap_round_trip_preserves_decimal_text_exactly()
    {
        var sourceFactory = SeededDb("source.db", "12.34");
        var backup = new DbMaintenanceService(sourceFactory).BackupDatabase(NewDbPath("backup.db"));

        // The "device being restored onto": a different live DB with different data.
        var liveFactory = SeededDb("live.db", "99.99");
        var livePath = liveFactory.DbPath;
        SqliteConnection.ClearAllPools();

        RestoreStaging.StageRestore(livePath, backup);
        Assert.True(RestoreStaging.HasPendingRestore(livePath));
        Assert.Equal("99.99", Scalar(livePath, "SELECT total_amount FROM receipts")); // live untouched until cold start

        RestoreStaging.CompletePendingRestore(livePath);
        Database.Initialize(new SqliteConnectionFactory(livePath)); // migrations replay cleanly on the restored file

        Assert.False(RestoreStaging.HasPendingRestore(livePath));
        // Landmine §4.3: raw cell read — the TEXT money cell survives byte-for-byte, no decimal round-trip.
        Assert.Equal("12.34", Scalar(livePath, "SELECT total_amount FROM receipts"));
        Assert.True(File.Exists(RestoreStaging.PreRestorePath(livePath))); // the outgoing DB is kept for recovery
        Assert.Equal("99.99", Scalar(RestoreStaging.PreRestorePath(livePath), "SELECT total_amount FROM receipts"));
    }

    [Fact]
    public void Corrupt_backup_is_rejected_at_validation_and_the_live_db_is_untouched()
    {
        var liveFactory = SeededDb("live.db", "50.00");
        SqliteConnection.ClearAllPools();
        var garbage = NewDbPath("garbage.db");
        File.WriteAllBytes(garbage, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

        Assert.ThrowsAny<Exception>(() => RestoreStaging.StageRestore(liveFactory.DbPath, garbage));

        Assert.False(RestoreStaging.HasPendingRestore(liveFactory.DbPath)); // no marker armed
        Assert.Equal("50.00", Scalar(liveFactory.DbPath, "SELECT total_amount FROM receipts"));
    }

    [Fact]
    public void Backup_with_a_newer_schema_version_is_rejected_at_restore()
    {
        var sourceFactory = SeededDb("source.db", "12.34");
        var backup = new DbMaintenanceService(sourceFactory).BackupDatabase(NewDbPath("backup.db"));
        using (var conn = new SqliteConnection($"Data Source={backup}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_version SET version = $v";
            cmd.Parameters.AddWithValue("$v", Database.LatestVersion + 1);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var livePath = SeededDb("live.db", "50.00").DbPath;
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidDataException>(() => RestoreStaging.StageRestore(livePath, backup));

        Assert.Contains("newer", ex.Message);
        Assert.False(RestoreStaging.HasPendingRestore(livePath));
    }

    [Fact]
    public void Newer_schema_db_is_rejected_at_normal_startup_with_both_versions()
    {
        var factory = SeededDb("live.db", "10.00");
        using (var conn = factory.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE schema_version SET version = $v";
            cmd.Parameters.AddWithValue("$v", Database.LatestVersion + 3);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => Database.Initialize(factory));

        Assert.Contains((Database.LatestVersion + 3).ToString(), ex.Message);
        Assert.Contains(Database.LatestVersion.ToString(), ex.Message);
    }

    [Fact]
    public void Crash_between_the_two_moves_rolls_back_from_pre_restore()
    {
        var liveFactory = SeededDb("live.db", "42.00");
        var livePath = liveFactory.DbPath;
        SqliteConnection.ClearAllPools();

        // Simulate the exact crash window: marker armed, live already moved out, staged already consumed.
        File.WriteAllText(RestoreStaging.MarkerPath(livePath), "restore_staged.db");
        File.Move(livePath, RestoreStaging.PreRestorePath(livePath));

        RestoreStaging.CompletePendingRestore(livePath);

        Assert.True(File.Exists(livePath)); // rolled back deterministically
        Assert.False(RestoreStaging.HasPendingRestore(livePath));
        Assert.Equal("42.00", Scalar(livePath, "SELECT total_amount FROM receipts"));
    }

    [Fact]
    public async Task AppStartup_completes_a_staged_restore_before_migrations()
    {
        var sourceFactory = SeededDb("source.db", "12.34");
        var backup = new DbMaintenanceService(sourceFactory).BackupDatabase(NewDbPath("backup.db"));
        var liveFactory = SeededDb("live.db", "99.99");
        SqliteConnection.ClearAllPools();
        RestoreStaging.StageRestore(liveFactory.DbPath, backup);

        var startup = new AppStartup(new SqliteConnectionFactory(liveFactory.DbPath));
        await startup.EnsureStartedAsync();

        Assert.Equal(StartupStatus.Ready, startup.Status);
        Assert.Equal("12.34", Scalar(liveFactory.DbPath, "SELECT total_amount FROM receipts"));
    }

    [Fact]
    public async Task AppStartup_retry_recovers_after_the_failure_is_fixed()
    {
        // A directory where the DB file should be → open fails → Error.
        var dbPath = NewDbPath("blocked.db");
        Directory.CreateDirectory(dbPath);
        var startup = new AppStartup(new SqliteConnectionFactory(dbPath));

        await startup.EnsureStartedAsync();
        Assert.Equal(StartupStatus.Error, startup.Status);
        Assert.False(string.IsNullOrWhiteSpace(startup.Error));

        Directory.Delete(dbPath); // the operator fixed the environment
        await startup.RetryAsync();

        Assert.Equal(StartupStatus.Ready, startup.Status);
        Assert.Null(startup.Error);
    }
}
