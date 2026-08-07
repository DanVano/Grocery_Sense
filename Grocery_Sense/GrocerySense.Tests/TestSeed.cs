using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Tests;

// Shared raw-SQL seeding helpers. Consumers `using static GrocerySense.Tests.TestSeed;` so the
// call sites read the same as the per-file privates they replaced.
internal static class TestSeed
{
    // Negative n is a future date (DaysAgo(-1) == tomorrow) — already the convention at the call sites.
    // LOCAL now: production windows are local-date based (V3 convention); a UTC stamp here would drift one
    // day off the service's "today" during evening rollover hours and flake date-window assertions.
    public static string DaysAgo(int n) => DateTime.Now.AddDays(-n).ToString("yyyy-MM-dd");
    public static string Today => DaysAgo(0);

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

    // A flyer_deals insert row. The record has 19 positional fields and test sites care about four or
    // five of them; anything else goes through a `with` clause at the call site.
    public static FlyerDeal Deal(int flyerId, int storeId, string title = "t", decimal? unitPrice = null,
        int? itemId = null, string? priceText = null, string? unit = "each",
        decimal? normUnitPrice = null, string? normUnit = null) =>
        new(Id: 0, FlyerId: flyerId, AssetId: null, StoreId: storeId, PageIndex: null,
            Title: title, Description: null, PriceText: priceText, DealQty: null, DealTotal: null,
            UnitPrice: unitPrice, Unit: unit, NormUnitPrice: normUnitPrice, NormUnit: normUnit,
            NormNote: null, ItemId: itemId, MappingConfidence: null, Confidence: null, CreatedAt: null);

    public static long Count(SqliteConnection conn, string table) =>
        (long)ExecScalar(conn, $"SELECT COUNT(*) FROM {table}");

    // One active flyer batch holding one deal. Validity stays NULL — open-ended, which every flyer
    // surface treats as currently active. unitPrice is a string because money columns are TEXT.
    public static void SeedActiveFlyerDeal(TempDb db, int storeId, string title, string unitPrice,
        int? itemId = null, string? unit = null)
    {
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flyer_batches (store_id, status, imported_at) VALUES ($s, 'active', datetime('now'));
            INSERT INTO flyer_deals (flyer_id, store_id, item_id, title, unit_price, unit, created_at)
            VALUES (last_insert_rowid(), $s, $item, $t, $p, $u, datetime('now'));
            """;
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$p", unitPrice);
        cmd.Parameters.AddWithValue("$u", (object?)unit ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
