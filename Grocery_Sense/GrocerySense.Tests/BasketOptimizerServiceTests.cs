using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

// The 8 spec-driven golden cases for the BasketOptimizer redesign (PORTING.md Phase 4 verify), plus the
// plan write-back + no-partial-rows tests.
public sealed class BasketOptimizerServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_opt_{Guid.NewGuid():N}");
    public BasketOptimizerServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    private (BasketOptimizerService Svc, ConfigStore Config) Build(TempDb db)
    {
        var config = new ConfigStore(_dir);
        return (new BasketOptimizerService(db.Factory, config, new PreferencesService(config)), config);
    }

    private static int Store(TempDb db, string name) => StoresRepo.CreateStore(db.Conn, name).Id;

    // Create an item, add it to the active list, return its id.
    private static int Listed(TempDb db, string name)
    {
        var id = ItemsRepo.CreateItem(db.Conn, name).Id;
        ShoppingListRepo.AddItem(db.Conn, name, itemId: id);
        return id;
    }

    private static void Price(TempDb db, int itemId, int storeId, double price) =>
        PricesRepo.AddPricePoint(db.Conn, itemId, storeId, price, "each", source: "manual", date: Today);

    private static int StoreOf(BasketOptimizationResult r, int itemId) =>
        r.Stores.Single(sp => sp.Items.Any(i => i.ItemId == itemId && !i.HardExcluded && !i.PriceUnknown)).StoreId;

    // (1) all stores within <10% -> 1 store.
    [Fact]
    public void All_stores_close_yields_one_store()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B"), c = Store(db, "C");
        foreach (var name in new[] { "x", "y", "z" })
        {
            var id = Listed(db, name);
            Price(db, id, a, 10.0); Price(db, id, b, 10.5); Price(db, id, c, 10.2);
        }

        var r = svc.Optimize("best_savings");
        Assert.Single(r.Stores);
        Assert.Equal(a, r.Stores[0].StoreId);
    }

    // (2) 2 items >=10% cheaper at B saving >=$5 -> 2 stores.
    [Fact]
    public void Two_items_cheaper_enough_adds_a_second_store()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int x = Listed(db, "x"), y = Listed(db, "y"), z = Listed(db, "z"), w = Listed(db, "w");
        Price(db, x, a, 10); Price(db, y, a, 10); Price(db, z, a, 5); Price(db, w, a, 5);
        Price(db, x, b, 7); Price(db, y, b, 7); Price(db, z, b, 10); Price(db, w, b, 10);

        var r = svc.Optimize("best_savings");
        Assert.Equal(2, r.Stores.Count);
        Assert.Equal(b, StoreOf(r, x));
        Assert.Equal(b, StoreOf(r, y));
        Assert.Equal(a, StoreOf(r, z));
    }

    // (3) lone item >=10% cheaper but saving <$5 -> stays at primary (1 store).
    [Fact]
    public void Lone_cheap_item_below_dollar_floor_stays_at_primary()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int x = Listed(db, "x"), y = Listed(db, "y");
        Price(db, x, a, 10); Price(db, y, a, 5);
        Price(db, x, b, 9); Price(db, y, b, 10); // x is 10% cheaper at B but only $1 saving

        Assert.Single(svc.Optimize("best_savings").Stores);
    }

    // (4) 4 stores qualify, maxStores=3 -> top-3 (primary + 2 highest-saving).
    [Fact]
    public void Caps_at_max_stores_taking_highest_savings()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db); // default maxStores = 3
        int a = Store(db, "A"), b = Store(db, "B"), c = Store(db, "C"), d = Store(db, "D"), e = Store(db, "E");
        int i1 = Listed(db, "i1"), i2 = Listed(db, "i2"), i3 = Listed(db, "i3"), i4 = Listed(db, "i4");
        foreach (var id in new[] { i1, i2, i3, i4 }) Price(db, id, a, 20);
        // each rival is cheap on exactly one item, expensive elsewhere (so A stays primary)
        Price(db, i1, b, 14); Price(db, i2, b, 30); Price(db, i3, b, 30); Price(db, i4, b, 30); // save 6
        Price(db, i1, c, 30); Price(db, i2, c, 13); Price(db, i3, c, 30); Price(db, i4, c, 30); // save 7
        Price(db, i1, d, 30); Price(db, i2, d, 30); Price(db, i3, d, 12); Price(db, i4, d, 30); // save 8
        Price(db, i1, e, 30); Price(db, i2, e, 30); Price(db, i3, e, 30); Price(db, i4, e, 11); // save 9

        var r = svc.Optimize("best_savings");
        Assert.Equal(3, r.Stores.Count);
        var ids = r.Stores.Select(s => s.StoreId).ToHashSet();
        Assert.Equal(new[] { a, d, e }.ToHashSet(), ids); // primary + the two highest savers
    }

    // (5) Fewest-stops forces one store even when a split would save.
    [Fact]
    public void Fewest_stops_forces_single_store()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int x = Listed(db, "x"), y = Listed(db, "y"), z = Listed(db, "z"), w = Listed(db, "w");
        Price(db, x, a, 10); Price(db, y, a, 10); Price(db, z, a, 5); Price(db, w, a, 5);
        Price(db, x, b, 7); Price(db, y, b, 7); Price(db, z, b, 10); Price(db, w, b, 10);

        var r = svc.Optimize("fewest_stops");
        Assert.Single(r.Stores);
        Assert.Equal("fewest_stops", r.Mode);
    }

    // (6) unknown price -> assigned to primary, flagged, excluded from total, pulls no store.
    [Fact]
    public void Unknown_price_item_is_flagged_and_excluded()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A");
        int x = Listed(db, "x");
        int u = Listed(db, "mystery"); // no price anywhere
        Price(db, x, a, 10);

        var r = svc.Optimize("best_savings");
        Assert.Single(r.Stores);
        var uPlan = r.Stores[0].Items.Single(i => i.ItemId == u);
        Assert.True(uPlan.PriceUnknown);
        Assert.Equal(a, uPlan.ChosenStoreId);
        Assert.Null(uPlan.UnitPrice);
        Assert.Equal(10, r.BasketTotalEstimated); // u excluded
        Assert.Contains(r.Warnings, w => w.Contains("no recent price data"));
    }

    // (7) allergen / hard-exclude -> pulled OUT (unassigned), surfaced as a warning.
    [Fact]
    public void Hard_excluded_item_is_pulled_out()
    {
        using var db = new TempDb();
        var (svc, config) = Build(db);
        config.Save(config.Load() with { }); // ensure file exists
        var cfg = config.Load();
        cfg.Household.Members[0].Profile["hard_excludes"] = new List<string> { "pork" };
        config.Save(cfg);

        int a = Store(db, "A");
        int pork = Listed(db, "pork chops");
        int milk = Listed(db, "milk");
        Price(db, pork, a, 8); Price(db, milk, a, 4);

        var r = svc.Optimize("best_savings");
        var porkPlan = r.Stores.SelectMany(s => s.Items).Single(i => i.ItemId == pork);
        Assert.True(porkPlan.HardExcluded);
        Assert.Null(porkPlan.ChosenStoreId);
        Assert.Contains(r.Warnings, w => w.Contains("hard-excluded"));
        Assert.Equal(4, r.BasketTotalEstimated); // only milk counts
    }

    // (8) maxStores=1 behaves like fewest_stops.
    [Fact]
    public void Max_stores_one_equals_fewest_stops()
    {
        using var db = new TempDb();
        var (svc, config) = Build(db);
        config.Save(config.Load() with { MaxStores = 1 });

        int a = Store(db, "A"), b = Store(db, "B");
        int x = Listed(db, "x"), y = Listed(db, "y");
        Price(db, x, a, 10); Price(db, y, a, 10);
        Price(db, x, b, 5); Price(db, y, b, 5); // big savings, but capped to one store

        var r = svc.Optimize("best_savings");
        Assert.Single(r.Stores);
        Assert.Equal("fewest_stops", r.Mode);
    }

    // Plan write-back: planned_store_id assigned per the plan, in one transaction.
    [Fact]
    public void ApplyOptimizerPlan_writes_planned_store_ids()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var list = new ShoppingListService(db.Factory);
        int a = Store(db, "A"), b = Store(db, "B");
        int x = Listed(db, "x"), y = Listed(db, "y"), z = Listed(db, "z"), w = Listed(db, "w");
        Price(db, x, a, 10); Price(db, y, a, 10); Price(db, z, a, 5); Price(db, w, a, 5);
        Price(db, x, b, 7); Price(db, y, b, 7); Price(db, z, b, 10); Price(db, w, b, 10);

        var result = svc.Optimize("best_savings");
        var applied = list.ApplyOptimizerPlanToActiveList(result);

        Assert.True(applied.Ok);
        var active = ShoppingListRepo.ListActiveItems(db.Conn).ToDictionary(r => r.ItemId!.Value, r => r.PlannedStoreId);
        Assert.Equal(b, active[x]);
        Assert.Equal(b, active[y]);
        Assert.Equal(a, active[z]);
        Assert.Equal(a, active[w]);
    }

    // No partial rows: a write-back failure rolls back the clear too.
    [Fact]
    public void ApplyOptimizerPlan_rolls_back_on_failure()
    {
        using var db = new TempDb();
        var list = new ShoppingListService(db.Factory);
        var store = Store(db, "A");
        var item = Listed(db, "milk");
        ShoppingListRepo.BulkSetPlannedStoreIdsByItemId(db.Conn, new[] { (item, (int?)store) }); // pre-set

        // A plan pointing at a non-existent store id -> FK violation mid-write.
        var bad = new BasketOptimizationResult("best_savings",
            new[] { new StorePlan(999999, "ghost",
                new[] { new BasketItemPlan(item, "milk", 999999, 5.0, "each", "manual", false, false, null, null) },
                5.0, 0) },
            5.0, null, null, Array.Empty<string>());

        Assert.ThrowsAny<SqliteException>(() => list.ApplyOptimizerPlanToActiveList(bad));

        // The pre-set assignment survived (clear + write rolled back together).
        var row = ShoppingListRepo.ListActiveItems(db.Conn).Single(r => r.ItemId == item);
        Assert.Equal(store, row.PlannedStoreId);
    }
}
