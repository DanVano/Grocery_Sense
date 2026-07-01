using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

// Port of reference-python/tests/planning/test_planning_service.py — guard rails, greedy store
// selection, cost estimation, item resolution, and the archived-store guard.
public sealed class PlanningServiceTests
{
    private static string Recent(int daysAgo = 5) => DateTime.Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private static PlanningService Svc(TempDb db) => new(db.Factory);

    private static int SeedItemWithPrices(TempDb db, string name, params (int StoreId, double Price)[] prices)
    {
        var item = ItemsRepo.CreateItem(db.Conn, name);
        foreach (var (storeId, price) in prices)
            PricesRepo.AddPricePoint(db.Conn, item.Id, storeId, price, "each", source: "receipt", date: Recent());
        return item.Id;
    }

    private static void Listed(TempDb db, string name, int? itemId = null, double quantity = 1.0) =>
        ShoppingListRepo.AddItem(db.Conn, name, quantity, itemId: itemId);

    // ---------- Guard rails ----------

    [Fact]
    public void No_items_and_no_stores()
    {
        using var db = new TempDb();
        var plan = Svc(db).BuildPlanForActiveList();

        Assert.Empty(plan.Stores);
        Assert.Empty(plan.Unassigned);
        Assert.Contains("No plan possible", plan.Summary);
        Assert.Null(plan.Costs.BasketTotalEstimate);
    }

    [Fact]
    public void Items_but_no_stores()
    {
        using var db = new TempDb();
        Listed(db, "eggs");

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.Empty(plan.Stores);
        Assert.Single(plan.Unassigned);
    }

    [Fact]
    public void Stores_but_no_items()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Mart");

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.Empty(plan.Stores);
        Assert.Empty(plan.Unassigned);
    }

    // ---------- Greedy store selection ----------

    [Fact]
    public void Items_go_to_cheapest_store()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "Store A", isFavorite: true, priority: 10);
        var b = StoresRepo.CreateStore(db.Conn, "Store B", priority: 5);

        var eggs = SeedItemWithPrices(db, "eggs", (a.Id, 5.00), (b.Id, 3.00));
        var milk = SeedItemWithPrices(db, "milk", (a.Id, 4.00), (b.Id, 6.00));
        Listed(db, "eggs", eggs);
        Listed(db, "milk", milk);

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);

        Assert.Contains(plan.Stores[a.Id].Items, i => i.DisplayName == "milk");
        Assert.Contains(plan.Stores[b.Id].Items, i => i.DisplayName == "eggs");
    }

    [Fact]
    public void Max_stores_caps_plan()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true, priority: 10);
        var b = StoresRepo.CreateStore(db.Conn, "B", priority: 5);
        var c = StoresRepo.CreateStore(db.Conn, "C");

        var i1 = SeedItemWithPrices(db, "i1", (a.Id, 1.00), (b.Id, 2.00), (c.Id, 3.00));
        var i2 = SeedItemWithPrices(db, "i2", (a.Id, 3.00), (b.Id, 1.00), (c.Id, 2.00));
        var i3 = SeedItemWithPrices(db, "i3", (a.Id, 3.00), (b.Id, 2.00), (c.Id, 1.00));
        Listed(db, "i1", i1);
        Listed(db, "i2", i2);
        Listed(db, "i3", i3);

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);
        Assert.True(plan.Stores.Count <= 2);
    }

    [Fact]
    public void Fallback_when_no_price_history()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true, priority: 10);
        StoresRepo.CreateStore(db.Conn, "B");

        Listed(db, "eggs"); // unlinked, no price history

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);
        Assert.True(plan.Stores.ContainsKey(a.Id));
        Assert.Contains(plan.Stores[a.Id].Items, i => i.DisplayName == "eggs");
    }

    // ---------- Cost estimation ----------

    [Fact]
    public void Per_store_and_basket_totals()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true, priority: 10);
        var b = StoresRepo.CreateStore(db.Conn, "B", priority: 5);

        var eggs = SeedItemWithPrices(db, "eggs", (a.Id, 5.00), (b.Id, 3.00));
        var milk = SeedItemWithPrices(db, "milk", (a.Id, 4.00), (b.Id, 6.00));
        Listed(db, "eggs", eggs, quantity: 2);
        Listed(db, "milk", milk, quantity: 1);

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);

        // eggs at B ($3 x 2 = $6); milk at A ($4 x 1 = $4). Basket = $10.
        Assert.Equal(10.0, plan.Costs.BasketTotalEstimate!.Value, 2);
        Assert.Equal(6.0, plan.Stores[b.Id].EstimatedSubtotal!.Value, 2);
        Assert.Equal(4.0, plan.Stores[a.Id].EstimatedSubtotal!.Value, 2);
    }

    [Fact]
    public void Baseline_compares_against_favorite_store()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "Fav", isFavorite: true, priority: 10);
        var b = StoresRepo.CreateStore(db.Conn, "Other");

        var eggs = SeedItemWithPrices(db, "eggs", (a.Id, 5.00), (b.Id, 3.00));
        Listed(db, "eggs", eggs);

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);

        Assert.Equal(a.Id, plan.Costs.BaselineStore!.Id);
        Assert.Equal(5.0, plan.Costs.BaselineTotalEstimate!.Value, 2);
        Assert.Equal(3.0, plan.Costs.BasketTotalEstimate!.Value, 2);
        Assert.Equal(2.0, plan.Costs.EstimatedSavings!.Value, 2);
    }

    [Fact]
    public void Coverage_counts_missing_items()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true);
        StoresRepo.CreateStore(db.Conn, "B");

        var eggs = SeedItemWithPrices(db, "eggs", (a.Id, 5.00));
        Listed(db, "eggs", eggs);
        Listed(db, "unknown widget"); // unlinked + no history -> missing

        var cov = Svc(db).BuildPlanForActiveList().Costs.Coverage;
        Assert.Equal(2, cov.TotalItems);
        Assert.Equal(1, cov.EstimatedItems);
        Assert.Equal(1, cov.MissingItems);
    }

    // ---------- Item resolution ----------

    [Fact]
    public void Item_id_preferred_over_name()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true);
        var thighs = SeedItemWithPrices(db, "chicken thighs", (a.Id, 5.0));
        SeedItemWithPrices(db, "chicken breasts", (a.Id, 9.99));

        // Display says 'chicken breasts' but item_id points at thighs — the id must win.
        Listed(db, "chicken breasts", thighs);

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.Equal(5.0, plan.Costs.BasketTotalEstimate!.Value, 2);
    }

    [Fact]
    public void Name_fallback_when_no_item_id()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true);
        SeedItemWithPrices(db, "eggs", (a.Id, 4.00));

        Listed(db, "eggs"); // no item_id — resolves by name

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.Equal(4.0, plan.Costs.BasketTotalEstimate!.Value, 2);
    }

    // ---------- Averaging / summary ----------

    [Fact]
    public void Multiple_price_points_average()
    {
        using var db = new TempDb();
        var a = StoresRepo.CreateStore(db.Conn, "A", isFavorite: true);
        var item = ItemsRepo.CreateItem(db.Conn, "eggs");
        PricesRepo.AddPricePoint(db.Conn, item.Id, a.Id, 4.0, "each", source: "receipt", date: Recent());
        PricesRepo.AddPricePoint(db.Conn, item.Id, a.Id, 6.0, "each", source: "receipt", date: Recent());
        Listed(db, "eggs", item.Id);

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.Equal(5.0, plan.Costs.BasketTotalEstimate!.Value, 2); // avg(4, 6)
    }

    [Fact]
    public void Summary_is_always_a_nonempty_string()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "A", isFavorite: true);
        Listed(db, "eggs");

        var plan = Svc(db).BuildPlanForActiveList();
        Assert.False(string.IsNullOrEmpty(plan.Summary));
    }

    // ---------- Archived store guard ----------

    [Fact]
    public void Archived_store_excluded_from_plan()
    {
        using var db = new TempDb();
        var active = StoresRepo.CreateStore(db.Conn, "Active Mart", isFavorite: true, priority: 10);
        var archived = StoresRepo.CreateStore(db.Conn, "Archived Mart", isFavorite: true, priority: 10);

        var eggs = SeedItemWithPrices(db, "eggs", (active.Id, 5.00), (archived.Id, 2.00));
        Listed(db, "eggs", eggs);

        StoresRepo.SetStoreActive(db.Conn, archived.Id, false);

        var plan = Svc(db).BuildPlanForActiveList(maxStores: 2);
        Assert.False(plan.Stores.ContainsKey(archived.Id));
        Assert.True(plan.Stores.ContainsKey(active.Id));
    }
}
