using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ReceiptsRepoTests
{
    [Fact]
    public void Reads_round_trip_summary_detail_and_line_items()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "T&T");
        var item = ItemsRepo.CreateItem(db.Conn, "Milk");
        var rid = InsertReceipt(db.Conn, store.Id, "2026-06-10", 12.34m, 11.00m, 1.34m);
        InsertLineItem(db.Conn, rid, 0, item.Id, "MILK 2L", 1, 5.00m, 5.00m);
        InsertLineItem(db.Conn, rid, 1, null, "BAGS", 1, 0.10m, 0.10m);
        InsertRawJson(db.Conn, rid, "op-1", "{\"x\":1}");

        var summary = Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
        Assert.Equal(rid, summary.Id);
        Assert.Equal(12.34m, summary.TotalAmount);
        Assert.Equal(2, summary.ItemCount);
        Assert.Equal("T&T", summary.StoreName);

        var detail = ReceiptsRepo.GetReceipt(db.Conn, rid)!;
        Assert.Equal(11.00m, detail.SubtotalAmount);
        Assert.Equal(1.34m, detail.TaxAmount);

        var lines = ReceiptsRepo.ListReceiptLineItems(db.Conn, rid);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Milk", lines[0].CanonicalName); // joined from items
        Assert.Equal(5.00m, lines[0].UnitPrice);

        var (raw, _) = ReceiptsRepo.GetReceiptRawJson(db.Conn, rid);
        Assert.Equal("{\"x\":1}", raw);
    }

    [Fact]
    public void Spend_sums_in_decimal_without_float_drift()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Store");
        // 0.1 + 0.2 — the canonical float-drift case; decimal summing must give exactly 0.30.
        InsertReceipt(db.Conn, store.Id, "2026-05-01", 0.10m, null, null);
        InsertReceipt(db.Conn, store.Id, "2026-05-15", 0.20m, null, null);
        InsertReceipt(db.Conn, store.Id, "2026-04-01", 5.00m, null, null);

        var may = ReceiptsRepo.GetMonthSpend(db.Conn, "2026-05");
        Assert.Equal(0.30m, may.Total);
        Assert.Equal(2, may.ReceiptCount);

        var trend = ReceiptsRepo.GetSpendTrend(db.Conn, months: 600); // wide window to include the fixtures
        Assert.Contains(trend, p => p.Month == "2026-05" && p.Total == 0.30m && p.ReceiptCount == 2);
        Assert.Contains(trend, p => p.Month == "2026-04" && p.Total == 5.00m);
        // oldest-first ordering
        Assert.Equal(trend.OrderBy(p => p.Month, StringComparer.Ordinal), trend);
    }

    [Fact]
    public void CascadeDelete_removes_receipt_and_children()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Store");
        var item = ItemsRepo.CreateItem(db.Conn, "Eggs");
        var rid = InsertReceipt(db.Conn, store.Id, "2026-06-01", 4.00m, null, null);
        InsertLineItem(db.Conn, rid, 0, item.Id, "EGGS", 1, 4.00m, 4.00m);
        InsertPrice(db.Conn, rid, item.Id, store.Id, "2026-06-01", 4.00m, "dozen");

        ReceiptsRepo.DeleteReceiptCascade(db.Conn, rid);

        Assert.Null(ReceiptsRepo.GetReceipt(db.Conn, rid));
        Assert.Equal(0, Count(db.Conn, "receipt_line_items"));
        Assert.Equal(0, Count(db.Conn, "prices"));
    }

    [Fact]
    public void DeleteWithBackup_then_restore_recreates_the_graph()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Store");
        var item = ItemsRepo.CreateItem(db.Conn, "Rice");
        var rid = InsertReceipt(db.Conn, store.Id, "2026-06-02", 9.99m, null, null);
        InsertLineItem(db.Conn, rid, 0, item.Id, "RICE 2KG", 1, 9.99m, 9.99m);
        InsertPrice(db.Conn, rid, item.Id, store.Id, "2026-06-02", 5.00m, "kg");
        InsertSignature(db.Conn, rid, "sig-abc");

        var backupId = ReceiptsRepo.DeleteReceiptWithBackup(db.Conn, rid);
        Assert.Null(ReceiptsRepo.GetReceipt(db.Conn, rid));
        Assert.Contains(ReceiptsRepo.ListDeletedBackups(db.Conn), b => b.BackupId == backupId && b.OriginalReceiptId == rid);

        var (newId, conflicts) = ReceiptsRepo.RestoreReceiptFromBackup(db.Conn, backupId);
        Assert.NotEqual(rid, newId);
        Assert.Empty(conflicts);
        var restored = ReceiptsRepo.GetReceipt(db.Conn, newId)!;
        Assert.Equal(9.99m, restored.TotalAmount);
        Assert.Single(ReceiptsRepo.ListReceiptLineItems(db.Conn, newId));
        Assert.Equal(1, Count(db.Conn, "prices"));
    }

    [Fact]
    public void Restore_reports_signature_conflict_without_stealing_the_key()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Store");
        var a = InsertReceipt(db.Conn, store.Id, "2026-06-03", 1.00m, null, null);
        InsertSignature(db.Conn, a, "dup-sig");

        var backupId = ReceiptsRepo.DeleteReceiptWithBackup(db.Conn, a);
        // A new receipt grabs the same signature before we restore.
        var b = InsertReceipt(db.Conn, store.Id, "2026-06-04", 2.00m, null, null);
        InsertSignature(db.Conn, b, "dup-sig");

        var (_, conflicts) = ReceiptsRepo.RestoreReceiptFromBackup(db.Conn, backupId);
        Assert.Contains(("signature", "dup-sig"), conflicts);
        // The signature still points at B, not the restored receipt.
        Assert.Equal(b, SignatureReceiptId(db.Conn, "dup-sig"));
    }

    // ---- SQL insert helpers (receipts_repo has no create methods — ingestion writes these) ----

    private static int InsertReceipt(SqliteConnection conn, int storeId, string date, decimal? total, decimal? sub, decimal? tax)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, subtotal_amount, tax_amount, total_amount, source) " +
            "VALUES ($s, $d, $sub, $tax, $tot, 'receipt'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$sub", (object?)sub ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tax", (object?)tax ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tot", (object?)total ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void InsertLineItem(SqliteConnection conn, int receiptId, int idx, int? itemId, string desc, double qty, decimal price, decimal total)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description, quantity, unit_price, line_total) " +
            "VALUES ($r, $i, $item, $desc, $q, $p, $t)";
        cmd.Parameters.AddWithValue("$r", receiptId);
        cmd.Parameters.AddWithValue("$i", idx);
        cmd.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", desc);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.Parameters.AddWithValue("$p", price);
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
    }

    private static void InsertPrice(SqliteConnection conn, int receiptId, int itemId, int storeId, string date, decimal unitPrice, string unit)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO prices (item_id, store_id, receipt_id, source, date, unit_price, unit) " +
            "VALUES ($item, $store, $r, 'receipt', $d, $p, $u)";
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$store", storeId);
        cmd.Parameters.AddWithValue("$r", receiptId);
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$p", unitPrice);
        cmd.Parameters.AddWithValue("$u", unit);
        cmd.ExecuteNonQuery();
    }

    private static void InsertRawJson(SqliteConnection conn, int receiptId, string op, string raw)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO receipt_raw_json (receipt_id, operation_id, raw_json) VALUES ($r, $o, $j)";
        cmd.Parameters.AddWithValue("$r", receiptId);
        cmd.Parameters.AddWithValue("$o", op);
        cmd.Parameters.AddWithValue("$j", raw);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSignature(SqliteConnection conn, int receiptId, string sig)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO receipt_signatures (signature, receipt_id) VALUES ($s, $r)";
        cmd.Parameters.AddWithValue("$s", sig);
        cmd.Parameters.AddWithValue("$r", receiptId);
        cmd.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static int SignatureReceiptId(SqliteConnection conn, string sig)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT receipt_id FROM receipt_signatures WHERE signature = $s";
        cmd.Parameters.AddWithValue("$s", sig);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
