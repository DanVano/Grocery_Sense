using Microsoft.Data.Sqlite;

namespace GrocerySense.Data;

// Shared repo plumbing. Centralizes the two load-bearing conventions so every repo applies them
// identically: (1) commands carry the caller's transaction (Microsoft.Data.Sqlite throws on a
// pending transaction unless cmd.Transaction matches it, and SqliteConnection doesn't expose it);
// (2) money is decimal stored as TEXT (Microsoft.Data.Sqlite round-trips decimal losslessly as TEXT).
internal static class Db
{
    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    // ISO-8601 round-trippable timestamp for created_at/updated_at columns (DateTimeOffset convention).
    public static string NowIso() => DateTimeOffset.UtcNow.ToString("o");

    // Null-safe parameter value: pass through DBNull for null so AddWithValue binds SQL NULL.
    public static object OrNull(object? value) => value ?? DBNull.Value;

    public static long LastRowId(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = Command(conn, tx, "SELECT last_insert_rowid();");
        return (long)cmd.ExecuteScalar()!;
    }

    // --- column readers ---
    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int? GetIntOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static double? GetDoubleOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public static decimal? GetMoneyOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
}
