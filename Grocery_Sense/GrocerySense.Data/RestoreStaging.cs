using Microsoft.Data.Sqlite;

namespace GrocerySense.Data;

// P1-5: staged cold-start restore. A live in-place restore is unsafe (SQLite pooling, WAL sidecars,
// per-path integrity caches), so restore is two halves:
//
//   1. StageRestore — at runtime: copy the selected backup into app-private storage, validate the COPY
//      (integrity_check, foreign_key_check, schema_version ≤ this build's ledger, expected-tables
//      sanity), then arm a marker file. The live DB is untouched.
//   2. CompletePendingRestore — at COLD START, before any DB consumer exists (AppStartup calls it ahead
//      of migrations): clear pools, drop stale -wal/-shm, move live → .pre-restore, move staged → live,
//      clear the marker. Migrations then run on the restored file.
//
// Crash-safety: the marker plus the .pre-restore copy make every interruption deterministic — each state
// the sequence can crash in is re-entered idempotently on the next launch (see CompletePendingRestore).
public static class RestoreStaging
{
    private const string StagedName = "restore_staged.db";
    private const string MarkerName = "restore_pending";
    private const string PreRestoreSuffix = ".pre-restore";

    public static string StagedPath(string dbPath) => Path.Combine(DirOf(dbPath), StagedName);
    public static string MarkerPath(string dbPath) => Path.Combine(DirOf(dbPath), MarkerName);
    public static string PreRestorePath(string dbPath) => dbPath + PreRestoreSuffix;

    public static bool HasPendingRestore(string dbPath) => File.Exists(MarkerPath(dbPath));

    // Validate on a private copy, then arm the marker. Throws (and cleans the copy) on any validation
    // failure — the live DB and any previously staged restore are left exactly as they were.
    public static void StageRestore(string dbPath, string backupPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup file not found", backupPath);
        var staged = StagedPath(dbPath);
        var tmp = staged + ".tmp";
        File.Copy(backupPath, tmp, overwrite: true);
        try
        {
            ValidateBackupCopy(tmp);
            if (File.Exists(staged)) File.Delete(staged);
            File.Move(tmp, staged);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
        File.WriteAllText(MarkerPath(dbPath), StagedName);
    }

    // The four checks a candidate must pass before it may ever replace the live DB.
    public static void ValidateBackupCopy(string candidatePath)
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = candidatePath,
            Mode = SqliteOpenMode.ReadOnly,
            // No pooling: the file handle must close with the connection so the staged copy can be moved.
            Pooling = false,
        }.ToString();
        using var conn = new SqliteConnection(connString);
        conn.Open();

        var integrity = Scalar(conn, "PRAGMA integrity_check;") as string;
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup failed integrity_check: {integrity ?? "<null>"}");

        using (var fk = conn.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_check;";
            using var r = fk.ExecuteReader();
            if (r.Read())
                throw new InvalidDataException("Backup failed foreign_key_check — it contains orphaned rows.");
        }

        foreach (var table in new[] { "schema_version", "receipts", "prices", "items", "stores" })
            if (Scalar(conn, $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{table}'") is null)
                throw new InvalidDataException($"Backup is not a Grocery Sense database — missing table '{table}'.");

        // The schema_version TABLE (not PRAGMA user_version). Never migrate down, never open blind.
        var version = Convert.ToInt32(Scalar(conn, "SELECT version FROM schema_version LIMIT 1;") ?? 0);
        if (version > Database.LatestVersion)
            throw new InvalidDataException(
                $"Backup schema version {version} is newer than this app supports ({Database.LatestVersion}) — " +
                "update the app before restoring this backup.");
    }

    // Cold-start half. Idempotent across every crash point:
    //   staged present, live present  → normal swap (live → .pre-restore, staged → live)
    //   staged present, live missing  → an earlier run crashed after moving live out; finish the swap
    //   staged missing, live present  → an earlier run finished the swap, crashed before clearing the marker
    //   staged missing, live missing  → crashed between the two moves; roll back from .pre-restore
    public static void CompletePendingRestore(string dbPath)
    {
        var marker = MarkerPath(dbPath);
        if (!File.Exists(marker)) return;

        var staged = StagedPath(dbPath);
        var pre = PreRestorePath(dbPath);

        SqliteConnection.ClearAllPools();
        // Sidecars belong to the OUTGOING db; they must never attach to the restored file.
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");

        if (File.Exists(staged))
        {
            if (File.Exists(dbPath))
            {
                TryDelete(pre); // a stale .pre-restore from an older restore loses to the current one
                File.Move(dbPath, pre);
            }
            File.Move(staged, dbPath);
        }
        else if (!File.Exists(dbPath) && File.Exists(pre))
        {
            File.Move(pre, dbPath);
        }

        File.Delete(marker);
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static string DirOf(string dbPath) =>
        Path.GetDirectoryName(dbPath) is { Length: > 0 } d ? d : ".";
}
