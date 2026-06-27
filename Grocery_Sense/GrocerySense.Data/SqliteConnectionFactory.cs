using Microsoft.Data.Sqlite;

namespace GrocerySense.Data;

/// <summary>
/// Single place that opens SQLite connections and applies pragmas — the C# port of
/// reference-python/src/Grocery_Sense/data/connection.py (get_connection / connection_scope).
/// Caller disposes the returned connection (use `using`).
/// </summary>
public sealed class SqliteConnectionFactory
{
    // Per-DB-path integrity-check guard (mirrors Python's _integrity_checked set). Keyed by
    // resolved path so per-test temp DBs each get checked once.
    private static readonly HashSet<string> _integrityChecked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    private readonly string _dbPath;

    public SqliteConnectionFactory(string dbPath) => _dbPath = dbPath;

    public string DbPath => _dbPath;

    /// <summary>Opens a configured connection: FK on, WAL, synchronous=NORMAL, busy_timeout, UTF-8,
    /// and a one-time integrity check per DB path.</summary>
    public SqliteConnection Open()
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var conn = new SqliteConnection(connString);
        conn.Open();

        Exec(conn, "PRAGMA foreign_keys = ON;");
        Exec(conn, "PRAGMA journal_mode = WAL;");
        Exec(conn, "PRAGMA synchronous = NORMAL;");
        Exec(conn, "PRAGMA busy_timeout = 5000;");

        EnsureIntegrityChecked(conn);
        return conn;
    }

    /// <summary>Runs the migration ledger once at app startup (creates tables, applies pending steps).</summary>
    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Database.Initialize(this);
        return Task.CompletedTask;
    }

    private void EnsureIntegrityChecked(SqliteConnection conn)
    {
        var key = Path.GetFullPath(_dbPath);
        lock (_gate)
        {
            if (_integrityChecked.Contains(key)) return;
        }

        // PRAGMA encoding only takes effect on a brand-new (empty) DB; harmless on an existing one.
        Exec(conn, "PRAGMA encoding = 'UTF-8';");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar() as string;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SQLite integrity_check failed for '{_dbPath}': {result ?? "<null>"}");

        lock (_gate) { _integrityChecked.Add(key); }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Test hook (mirrors Python's reset_integrity_cache) — clears the per-path guard.</summary>
    public static void ResetIntegrityCache()
    {
        lock (_gate) { _integrityChecked.Clear(); }
    }
}
