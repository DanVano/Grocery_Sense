using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/receipts_repo.py
// Read shapes are row dicts in Python -> Dictionary<string, object?> here until the UI needs typed rows.
public static class ReceiptsRepo
{
    public static IReadOnlyList<Dictionary<string, object?>> ListRecentReceipts(SqliteConnection conn, int limit = 50,
        int offset = 0, int? storeId = null, string? since = null, string? until = null) => throw new NotImplementedException();

    public static Dictionary<string, object?>? GetReceipt(SqliteConnection conn, int receiptId) => throw new NotImplementedException();

    public static IReadOnlyList<Dictionary<string, object?>> ListReceiptLineItems(SqliteConnection conn, int receiptId)
        => throw new NotImplementedException();

    public static (string? RawJson, string? JsonPath) GetReceiptRawJson(SqliteConnection conn, int receiptId) => throw new NotImplementedException();

    public static Dictionary<string, object?> GetMonthSpend(SqliteConnection conn, string yearMonth) => throw new NotImplementedException();

    public static IReadOnlyList<Dictionary<string, object?>> GetSpendTrend(SqliteConnection conn, int months = 12) => throw new NotImplementedException();

    public static void DeleteReceiptCascade(SqliteConnection conn, int receiptId) => throw new NotImplementedException();

    public static int DeleteReceiptWithBackup(SqliteConnection conn, int receiptId) => throw new NotImplementedException();

    // returns (newReceiptId, conflicts[(kind, key)])
    public static (int NewReceiptId, IReadOnlyList<(string Kind, string Key)> Conflicts) RestoreReceiptFromBackup(
        SqliteConnection conn, int backupId) => throw new NotImplementedException();
}
