using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class StapleRestockServiceTests
{
    private static string DaysAgo(int n) => DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd");

    private static int AddReceipt(TempDb db, int storeId, string date)
    {
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, source) VALUES ($s, $d, 'receipt'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", date);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // 4 receipt purchases spaced 10 days apart, most recent `lastDaysAgo` ago.
    // Qualifies as a staple (>=3 distinct receipts in 90d) with a 10-day cadence.
    private static int SeedStaple(TempDb db, string name, int lastDaysAgo)
    {
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, name).Id;
        foreach (var d in new[] { lastDaysAgo + 30, lastDaysAgo + 20, lastDaysAgo + 10, lastDaysAgo })
        {
            var rid = AddReceipt(db, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, 3.5, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        return item;
    }

    [Fact]
    public void Overdue_staple_is_suggested_with_cadence()
    {
        using var db = new TempDb();
        SeedStaple(db, "milk", lastDaysAgo: 12); // 12 days since >= 10-day interval -> due

        var suggestions = new StapleRestockService(db.Factory).GetSuggestions();

        var s = Assert.Single(suggestions);
        Assert.Equal("milk", s.Name);
        Assert.Equal(12, s.DaysSinceLast);
        Assert.Equal(10, s.IntervalDays);
    }

    [Fact]
    public void Staple_bought_recently_is_not_suggested()
    {
        using var db = new TempDb();
        SeedStaple(db, "milk", lastDaysAgo: 2); // 2 < 10-day interval -> not due
        Assert.Empty(new StapleRestockService(db.Factory).GetSuggestions());
    }

    [Fact]
    public void Staple_already_on_the_list_by_item_id_is_not_suggested()
    {
        using var db = new TempDb();
        var item = SeedStaple(db, "milk", lastDaysAgo: 12);
        ShoppingListRepo.AddItem(db.Conn, "milk", itemId: item);
        Assert.Empty(new StapleRestockService(db.Factory).GetSuggestions());
    }

    [Fact]
    public void Staple_already_on_the_list_by_typed_name_is_not_suggested()
    {
        using var db = new TempDb();
        SeedStaple(db, "milk", lastDaysAgo: 12);
        // Repo-level seed with NO item_id, sloppy casing/spacing. Deliberately NOT AddSingleItem:
        // the service auto-maps "MILK" -> the item and would satisfy this test through the ID path —
        // this must prove the normalized-NAME fallback (rows whose mapping failed at add time).
        ShoppingListRepo.AddItem(db.Conn, "  MILK ");
        Assert.Empty(new StapleRestockService(db.Factory).GetSuggestions());
    }

    [Fact]
    public void No_cadence_means_no_suggestion()
    {
        using var db = new TempDb();
        // 4 line items on ONE receipt: passes the staple line-count filter but has no interval -> never guess.
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "salt").Id;
        var rid = AddReceipt(db, store, DaysAgo(40));
        for (var i = 0; i < 4; i++)
            PricesRepo.AddPricePoint(db.Conn, item, store, 1.0, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(40), receiptId: rid);

        Assert.Empty(new StapleRestockService(db.Factory).GetSuggestions());
    }
}
