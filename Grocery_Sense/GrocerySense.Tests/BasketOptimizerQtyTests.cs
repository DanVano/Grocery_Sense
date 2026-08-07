using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// V3 finding 8 regression: PickPrimary ranked stores by UNIT-price sums while the store-join gate and
// result totals are qty-weighted — a basket dominated by one multi-quantity row could get a primary store
// that is not the cheapest for the actual basket. Primary selection must weight by quantity.
public sealed class BasketOptimizerQtyTests : TempDirTestBase
{
    [Fact]
    public void Primary_store_selection_is_quantity_weighted()
    {
        using var db = new TempDb();
        var config = new ConfigStore(_dir);
        var svc = new BasketOptimizerService(db.Factory, config, new PreferencesService(config));

        int a = StoresRepo.CreateStore(db.Conn, "A").Id, b = StoresRepo.CreateStore(db.Conn, "B").Id;

        var x = ItemsRepo.CreateItem(db.Conn, "x").Id; // qty 6: unit prices A $2 / B $1
        ShoppingListRepo.AddItem(db.Conn, "x", 6.0, itemId: x);
        PricesRepo.AddPricePoint(db.Conn, x, a, 2.0, "each", source: "manual", date: Today);
        PricesRepo.AddPricePoint(db.Conn, x, b, 1.0, "each", source: "manual", date: Today);

        var y = ItemsRepo.CreateItem(db.Conn, "y").Id; // qty 1: unit prices A $1 / B $5
        ShoppingListRepo.AddItem(db.Conn, "y", 1.0, itemId: y);
        PricesRepo.AddPricePoint(db.Conn, y, a, 1.0, "each", source: "manual", date: Today);
        PricesRepo.AddPricePoint(db.Conn, y, b, 5.0, "each", source: "manual", date: Today);

        // Unit-price sums say A ($3 vs $6); the real basket says B ($13 vs $11). fewest_stops = the
        // primary store alone, so the assertion isolates PickPrimary.
        var r = svc.Optimize("fewest_stops");
        Assert.Equal(b, r.Stores.Single().StoreId);
    }
}
