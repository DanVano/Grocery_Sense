using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ShoppingInsightsServiceTests
{
    private static string DaysAgo(int n) => DateTime.UtcNow.AddDays(-n).ToString("yyyy-MM-dd");

    private static int AddReceipt(SqliteConnection conn, int storeId, string date)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, source) VALUES ($s, $d, 'receipt'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", date);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    // Usual = receipt median $10 (4 samples, satisfies MinReceiptSamplesForUsual), quantity=1 per receipt
    // so purchase cadence is computable (interval 10 days).
    private static void SeedUsualTen(SqliteConnection conn, int item, int store)
    {
        foreach (var d in new[] { 40, 30, 20, 10 })
        {
            var rid = AddReceipt(conn, store, DaysAgo(d));
            PricesRepo.AddPricePoint(conn, item, store, 10.0, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
    }

    private static ShoppingInsightsService Svc(TempDb db) => new(db.Factory);

    [Fact]
    public void Buy_badge_when_current_is_15pct_below_usual_but_not_near_low()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        SeedUsualTen(db.Conn, item, store);
        // Old $5 low keeps today's $7 out of the near-low band (7 > 5 * 1.05) -> pure "buy".
        PricesRepo.AddPricePoint(db.Conn, item, store, 5.0, "each", source: "manual", date: DaysAgo(100));
        PricesRepo.AddPricePoint(db.Conn, item, store, 7.0, "each", source: "manual", date: DaysAgo(0));
        ShoppingListRepo.AddItem(db.Conn, "Milk", plannedStoreId: store, itemId: item);

        var group = Assert.Single(Svc(db).BuildShopModeView());
        var insight = Assert.Single(group.Items);
        Assert.Equal("buy", insight.Badge);
        Assert.Equal(7.0, insight.CurrentPrice!.Value, 4);
        Assert.Equal(10.0, insight.UsualPrice!.Value, 4);
        Assert.Equal(30.0, insight.PctBelowUsual!.Value, 1);
    }

    [Fact]
    public void StockUp_badge_with_suggested_qty_when_current_matches_six_month_low()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Pasta").Id;
        SeedUsualTen(db.Conn, item, store);
        PricesRepo.AddPricePoint(db.Conn, item, store, 7.0, "each", source: "manual", date: DaysAgo(0));
        ShoppingListRepo.AddItem(db.Conn, "Pasta", plannedStoreId: store, itemId: item);

        var insight = Assert.Single(Assert.Single(Svc(db).BuildShopModeView()).Items);
        Assert.Equal("stock_up", insight.Badge);
        // Cadence: 4 receipts over 30 days -> ~10-day interval; 28-day horizon at 1/receipt -> buy 3.
        Assert.Equal(3.0, insight.SuggestedQty!.Value, 4);
        Assert.NotNull(insight.SuggestedQtyNote);
    }

    [Fact]
    public void Wait_badge_when_current_is_above_usual()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Cheese").Id;
        SeedUsualTen(db.Conn, item, store);
        PricesRepo.AddPricePoint(db.Conn, item, store, 12.0, "each", source: "manual", date: DaysAgo(0));
        ShoppingListRepo.AddItem(db.Conn, "Cheese", plannedStoreId: store, itemId: item);

        var insight = Assert.Single(Assert.Single(Svc(db).BuildShopModeView()).Items);
        Assert.Equal("wait", insight.Badge);
    }

    [Fact]
    public void Unmapped_or_unpriced_rows_get_no_badge_and_count_as_unpriced()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Dragonfruit").Id; // mapped but has zero price history
        ShoppingListRepo.AddItem(db.Conn, "Dragonfruit", plannedStoreId: store, itemId: item);
        ShoppingListRepo.AddItem(db.Conn, "birthday candles", plannedStoreId: store); // free text, no item

        var group = Assert.Single(Svc(db).BuildShopModeView());
        Assert.Equal(2, group.UnpricedCount);
        Assert.Equal(0.0, group.SubtotalEstimated, 4);
        Assert.All(group.Items, i => Assert.Equal("none", i.Badge));
        Assert.All(group.Items, i => Assert.Null(i.CurrentPrice));
    }

    [Fact]
    public void Groups_by_planned_store_with_subtotals_and_unassigned_last()
    {
        using var db = new TempDb();
        var storeA = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var storeB = StoresRepo.CreateStore(db.Conn, "NoFrills").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        SeedUsualTen(db.Conn, item, storeA);
        PricesRepo.AddPricePoint(db.Conn, item, storeB, 8.0, "each", source: "manual", date: DaysAgo(0));

        ShoppingListRepo.AddItem(db.Conn, "Milk", quantity: 2.0, plannedStoreId: storeA, itemId: item);
        ShoppingListRepo.AddItem(db.Conn, "Milk", plannedStoreId: storeB, itemId: item);
        ShoppingListRepo.AddItem(db.Conn, "mystery snack"); // no planned store

        var groups = Svc(db).BuildShopModeView();
        Assert.Equal(3, groups.Count);
        Assert.Equal("Unassigned", groups[^1].StoreName);
        Assert.Null(groups[^1].StoreId);

        var a = groups.Single(g => g.StoreId == storeA);
        Assert.Equal(20.0, a.SubtotalEstimated, 4); // qty 2 x $10 most-recent at store A
        var b = groups.Single(g => g.StoreId == storeB);
        Assert.Equal(8.0, b.SubtotalEstimated, 4);
    }

    [Fact]
    public void Planned_store_without_a_price_stays_unpriced_not_swapped_to_another_store()
    {
        using var db = new TempDb();
        var storeA = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var storeB = StoresRepo.CreateStore(db.Conn, "NoFrills").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        PricesRepo.AddPricePoint(db.Conn, item, storeA, 9.0, "each", source: "manual", date: DaysAgo(0));
        ShoppingListRepo.AddItem(db.Conn, "Milk", plannedStoreId: storeB, itemId: item); // planned where no price

        var group = Assert.Single(Svc(db).BuildShopModeView());
        var insight = Assert.Single(group.Items);
        Assert.Null(insight.CurrentPrice);
        Assert.Equal(1, group.UnpricedCount);
    }

    [Fact]
    public void Empty_list_yields_no_groups()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        Assert.Empty(Svc(db).BuildShopModeView());
    }
}
