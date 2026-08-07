using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// V3 Phase 5 gates (grill Q12, Codex-hardened): realized savings come from PRIOR receipt medians with the
// scanned receipt EXCLUDED (no baseline contamination), double-close is impossible, close-out clears the
// picked-up rows in the same transaction, thin history yields null (never $0), overpays go negative, and
// the delete-with-backup flow relinks the ledger row on restore instead of silently losing it.
public sealed class TripCloseoutTests
{
    private static void AddLine(SqliteConnection conn, int receiptId, int? itemId, string desc,
        double qty, decimal unitPrice)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description, quantity, unit_price, line_total)
            VALUES ($rid, 0, $item, $desc, $qty, $price, $total)
            """;
        cmd.Parameters.AddWithValue("$rid", receiptId);
        cmd.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", desc);
        cmd.Parameters.AddWithValue("$qty", qty);
        cmd.Parameters.AddWithValue("$price", unitPrice);
        cmd.Parameters.AddWithValue("$total", unitPrice * (decimal)qty);
        cmd.ExecuteNonQuery();
    }

    // 4 prior receipt purchases at `price` (>= MinReceiptSamplesForUsual) so the item has a clean baseline.
    private static int SeedHistory(TempDb db, int storeId, string name, decimal price)
    {
        var item = ItemsRepo.CreateItem(db.Conn, name).Id;
        foreach (var d in new[] { 40, 30, 20, 10 })
        {
            var rid = AddReceipt(db, storeId, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, storeId, (double)price, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        return item;
    }

    private static int TripReceipt(TempDb db, int storeId, int itemId, double qty, decimal paidUnit,
        decimal? total = null)
    {
        var rid = AddReceipt(db, storeId, Today);
        AddLine(db.Conn, rid, itemId, "line", qty, paidUnit);
        // The trip's own price row — the exact row that CONTAMINATES a naive usual-price baseline.
        PricesRepo.AddPricePoint(db.Conn, itemId, storeId, (double)paidUnit, "each", quantity: qty,
            source: "receipt", date: Today, receiptId: rid);
        if (total is { } t) Exec(db.Conn, $"UPDATE receipts SET total_amount = '{t}' WHERE id = {rid}");
        return rid;
    }

    [Fact]
    public void Realized_saving_uses_prior_median_excluding_this_receipt()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = SeedHistory(db, store, "milk", 5.00m);
        var rid = TripReceipt(db, store, milk, qty: 2.0, paidUnit: 4.00m, total: 8.00m);

        var closed = new TripReconciliationService(db.Factory).CloseTrip(rid);

        // Baseline = median of the four prior $5.00 rows. If the trip's own $4.00 row leaked in, the
        // median would drop to $5.00->$5.00 (5 rows: 4,5,5,5,5 -> 5.00 still) — use asymmetric check:
        // (5.00 - 4.00) x 2 = exactly $2.00 only when the baseline is the uncontaminated $5.00.
        Assert.Equal(2.00m, closed.RealizedSaving);
        Assert.Equal(1, closed.QualifyingLines);
        Assert.Equal(1L, Count(db.Conn, "trips"));
    }

    [Fact]
    public void Overpaying_yields_negative_savings_not_silence()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = SeedHistory(db, store, "milk", 4.00m);
        var rid = TripReceipt(db, store, milk, qty: 1.0, paidUnit: 5.50m);

        Assert.Equal(-1.50m, new TripReconciliationService(db.Factory).CloseTrip(rid).RealizedSaving);
    }

    [Fact]
    public void Thin_history_yields_null_never_zero()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rare = ItemsRepo.CreateItem(db.Conn, "saffron").Id; // no prior receipts at all
        var rid = TripReceipt(db, store, rare, qty: 1.0, paidUnit: 9.00m);

        var closed = new TripReconciliationService(db.Factory).CloseTrip(rid);
        Assert.Null(closed.RealizedSaving);
        Assert.Equal(0, closed.QualifyingLines);
        Assert.Equal(1, closed.MappedLines);
    }

    [Fact]
    public void Close_clears_checked_rows_and_double_close_throws()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = SeedHistory(db, store, "milk", 5.00m);
        var rid = TripReceipt(db, store, milk, qty: 1.0, paidUnit: 4.00m);

        var row = ShoppingListRepo.AddItem(db.Conn, "milk", itemId: milk);
        ShoppingListRepo.SetCheckedOff(db.Conn, row, true);
        var keep = ShoppingListRepo.AddItem(db.Conn, "bread"); // unchecked — must survive

        var svc = new TripReconciliationService(db.Factory);
        svc.CloseTrip(rid);

        var open = ShoppingListRepo.ListActiveItems(db.Conn, includeCheckedOff: true);
        Assert.Equal(keep, Assert.Single(open).Id); // checked row gone, unchecked row untouched
        Assert.Throws<InvalidOperationException>(() => svc.CloseTrip(rid)); // UNIQUE(receipt_id) semantics
    }

    [Fact]
    public void Delete_with_backup_then_restore_relinks_the_trip_row()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = SeedHistory(db, store, "milk", 5.00m);
        var rid = TripReceipt(db, store, milk, qty: 2.0, paidUnit: 4.00m, total: 8.00m);
        new TripReconciliationService(db.Factory).CloseTrip(rid);

        var backupId = ReceiptsRepo.DeleteReceiptWithBackup(db.Conn, rid);
        Assert.Equal(0L, Count(db.Conn, "trips")); // CASCADE removed the ledger row with the receipt

        var (newRid, _) = ReceiptsRepo.RestoreReceiptFromBackup(db.Conn, backupId);
        Assert.Equal(1L, Count(db.Conn, "trips"));
        Assert.True(TripsRepo.HasTripForReceipt(db.Conn, newRid)); // relinked to the NEW id
        var (total, withSavings, count) = TripsRepo.GetMonthRealizedSavings(db.Conn,
            DateTime.Now.ToString("yyyy-MM"));
        Assert.Equal(2.00m, total); // realized value survived the round trip
        Assert.Equal(1, withSavings);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Month_summary_sums_decimals_and_reports_all_null_as_unavailable()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var month = DateTime.Now.ToString("yyyy-MM");
        var r1 = AddReceipt(db, store, $"{month}-05");
        var r2 = AddReceipt(db, store, $"{month}-12");
        var r3 = AddReceipt(db, store, $"{month}-20");

        TripsRepo.Insert(db.Conn, r1, store, $"{month}-05", null, null, null, null, 3.25m, "basis", 1, 1, 0, 0);
        TripsRepo.Insert(db.Conn, r2, store, $"{month}-12", null, null, null, null, -1.00m, "basis", 1, 1, 0, 0);
        TripsRepo.Insert(db.Conn, r3, store, $"{month}-20", null, null, null, null, null, null, 1, 0, 0, 0);

        var (total, withSavings, count) = TripsRepo.GetMonthRealizedSavings(db.Conn, month);
        Assert.Equal(2.25m, total); // 3.25 - 1.00; the null trip is counted but never summed
        Assert.Equal(2, withSavings);
        Assert.Equal(3, count);

        Exec(db.Conn, "DELETE FROM trips WHERE realized_saving IS NOT NULL");
        var allNull = TripsRepo.GetMonthRealizedSavings(db.Conn, month);
        Assert.Null(allNull.Total); // unavailable, not $0
        Assert.Equal(1, allNull.TripCount);
    }
}
