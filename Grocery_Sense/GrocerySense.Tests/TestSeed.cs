using Microsoft.Data.Sqlite;

namespace GrocerySense.Tests;

// Shared raw-SQL seeding helpers. Consumers `using static GrocerySense.Tests.TestSeed;` so the
// call sites read the same as the per-file privates they replaced.
internal static class TestSeed
{
    public static string DaysAgo(int n) => DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd");

    public static int AddReceipt(SqliteConnection conn, int storeId, string date)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, source) VALUES ($s, $d, 'receipt'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", date);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    public static int AddReceipt(TempDb db, int storeId, string date) => AddReceipt(db.Conn, storeId, date);

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static object ExecScalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }
}
