using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class TripReconciliationServiceTests
{


    private static void AddLine(TempDb db, int receiptId, int lineIndex, int? itemId, string desc,
        string unitPrice, double qty = 1, string? lineTotal = null)
    {
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description, quantity, unit_price, line_total)
            VALUES ($r, $i, $item, $d, $q, $p, $t)
            """;
        cmd.Parameters.AddWithValue("$r", receiptId);
        cmd.Parameters.AddWithValue("$i", lineIndex);
        cmd.Parameters.AddWithValue("$item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", desc);
        cmd.Parameters.AddWithValue("$q", qty);
        cmd.Parameters.AddWithValue("$p", unitPrice);
        cmd.Parameters.AddWithValue("$t", (object?)lineTotal ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }


    [Fact]
    public void Flags_paid_above_current_flyer_price()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var chicken = ItemsRepo.CreateItem(db.Conn, "chicken").Id;
        SeedActiveFlyerDeal(db, store, "chicken", "2.00", itemId: chicken);
        ShoppingListRepo.AddItem(db.Conn, "chicken", itemId: chicken, plannedStoreId: store);

        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, chicken, "CHICKEN", "3.00");

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        var flag = Assert.Single(result.Flags);
        Assert.Equal("flyer_below_paid", flag.Kind);
        Assert.Equal(3.00m, flag.Paid);
        Assert.Equal(2.00, flag.Expected, 2);
    }

    [Fact]
    public void Weight_priced_flyer_quote_is_skipped_and_disclosed()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var beef = ItemsRepo.CreateItem(db.Conn, "beef").Id;
        SeedActiveFlyerDeal(db, store, "beef", "8.80", itemId: beef, unit: "kg"); // per-kg — line unit unknown
        ShoppingListRepo.AddItem(db.Conn, "beef", itemId: beef, plannedStoreId: store);

        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, beef, "BEEF", "12.00"); // would "flag" if units were naively compared

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        Assert.DoesNotContain(result.Flags, f => f.Kind == "flyer_below_paid");
        Assert.Contains("unit", result.DataNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unplanned_lines_are_counted_and_totalled_in_decimal()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var chips = ItemsRepo.CreateItem(db.Conn, "chips").Id;
        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, chips, "CHIPS", "4.49", qty: 2, lineTotal: "8.98");

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        Assert.Equal(1, result.UnplannedCount);
        Assert.Equal(8.98m, result.UnplannedTotal);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public void Unplanned_total_derives_from_unit_price_and_quantity_when_line_total_missing()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var chips = ItemsRepo.CreateItem(db.Conn, "chips").Id;
        var soda = ItemsRepo.CreateItem(db.Conn, "soda").Id;
        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, chips, "CHIPS", "4.49", qty: 2); // line_total NULL → 4.49m * 2
        AddLine(db, rid, 1, soda, "SODA", "3.25", qty: 0);   // qty <= 0 falls back to 1 → 3.25m

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        Assert.Equal(2, result.UnplannedCount);
        Assert.Equal(12.23m, result.UnplannedTotal); // 8.98 + 3.25, derived in decimal — nothing read from line_total
    }

    [Fact]
    public void Planned_at_this_store_but_missing_from_receipt_is_reported()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        ShoppingListRepo.AddItem(db.Conn, "milk", itemId: milk, plannedStoreId: store);

        var rid = AddReceipt(db, store, Today); // empty trip
        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        Assert.Contains("milk", result.PlannedNotBought);
    }

    [Fact]
    public void Unmapped_lines_are_disclosed_not_judged()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, null, "MYSTERY ITEM", "5.00");

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);

        Assert.Empty(result.Flags);
        Assert.Equal(0, result.UnplannedCount); // unmapped ≠ unplanned — we don't know what it is
        Assert.NotNull(result.DataNote);
        Assert.Contains("1", result.DataNote);
    }

    [Fact]
    public void Duplicate_lines_of_one_planned_item_count_once()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        ShoppingListRepo.AddItem(db.Conn, "milk", itemId: milk, plannedStoreId: store);

        var rid = AddReceipt(db, store, Today);
        AddLine(db, rid, 0, milk, "MILK", "3.00");
        AddLine(db, rid, 1, milk, "MILK", "3.00");

        var result = new TripReconciliationService(db.Factory).Reconcile(rid);
        Assert.Equal(1, result.MatchedPlanned); // distinct items, not receipt lines
    }

    [Fact]
    public void Old_or_future_receipts_are_refused()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var old = AddReceipt(db, store, DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd"));
        var future = AddReceipt(db, store, DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd"));

        var svc = new TripReconciliationService(db.Factory);
        Assert.Throws<InvalidOperationException>(() => svc.Reconcile(old));    // stale — current-state diff is dishonest
        Assert.Throws<InvalidOperationException>(() => svc.Reconcile(future)); // future-dated — bad data, refuse
    }
}
