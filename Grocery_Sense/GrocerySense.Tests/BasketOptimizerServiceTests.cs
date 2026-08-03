using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// The 8 spec-driven golden cases for the BasketOptimizer redesign (PORTING.md Phase 4 verify), plus the
// plan write-back + no-partial-rows tests.
public sealed class BasketOptimizerServiceTests : TempDirTestBase
{


    private (BasketOptimizerService Svc, ConfigStore Config) Build(TempDb db)
    {
        var config = new ConfigStore(_dir);
        return (new BasketOptimizerService(db.Factory, config, new PreferencesService(config)), config);
    }

    private static int Store(TempDb db, string name) => StoresRepo.CreateStore(db.Conn, name).Id;

    // Create an item, add it to the active list, return its id.
    private static int Listed(TempDb db, string name, double quantity = 1.0)
    {
        var id = ItemsRepo.CreateItem(db.Conn, name).Id;
        ShoppingListRepo.AddItem(db.Conn, name, quantity, itemId: id);
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
        cfg = cfg with { Preferences = cfg.Preferences! with { HardExcludes = ["pork"] } };
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

    // wait_for_sale item stays unplanned when its current price isn't clearly below usual.
    [Fact]
    public void Wait_for_sale_item_is_left_unplanned_when_not_on_sale()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var a = Store(db, "A");
        var milk = Listed(db, "milk");        // normal item so the basket is non-empty
        Price(db, milk, a, 4);

        var steak = ItemsRepo.CreateItem(db.Conn, "steak").Id;
        ShoppingListRepo.AddItem(db.Conn, "steak", itemId: steak, priority: "wait_for_sale");
        Price(db, steak, a, 10); Price(db, steak, a, 10); Price(db, steak, a, 10); // usual ~10, current ~10

        var r = svc.Optimize("best_savings");

        Assert.DoesNotContain(r.Stores.SelectMany(s => s.Items), i => i.ItemId == steak);
        Assert.Contains(r.Warnings, w => w.Contains("wait for sale"));
    }

    // wait_for_sale item is planned once its current price beats usual by the saving margin.
    [Fact]
    public void Wait_for_sale_item_is_planned_when_on_sale()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var a = Store(db, "A");
        var milk = Listed(db, "milk");
        Price(db, milk, a, 4);

        var steak = ItemsRepo.CreateItem(db.Conn, "steak").Id;
        ShoppingListRepo.AddItem(db.Conn, "steak", itemId: steak, priority: "wait_for_sale");
        Price(db, steak, a, 10); Price(db, steak, a, 10); Price(db, steak, a, 10);
        Price(db, steak, a, 8); // most-recent = 8, ~20% below the ~10 usual

        var r = svc.Optimize("best_savings");

        Assert.Equal(a, StoreOf(r, steak));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("wait for sale"));
    }

    // AggregatePriority: a normal row for the same item overrides a wait_for_sale row — the item is
    // planned even though it is NOT on sale (both wait tests above use single-row items).
    [Fact]
    public void Normal_row_overrides_wait_for_sale_row_for_the_same_item()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var a = Store(db, "A");
        var steak = ItemsRepo.CreateItem(db.Conn, "steak").Id;
        ShoppingListRepo.AddItem(db.Conn, "steak", itemId: steak, priority: "wait_for_sale");
        ShoppingListRepo.AddItem(db.Conn, "steak", itemId: steak); // second row defaults to "normal"
        Price(db, steak, a, 10); Price(db, steak, a, 10); Price(db, steak, a, 10); // usual ~10 => not on sale

        var r = svc.Optimize("best_savings");

        Assert.Equal(a, StoreOf(r, steak));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("wait for sale"));
    }

    // PhraseSafeHit doubles as the Deals-page allergen gate — pin its whole-word/phrase semantics directly.
    [Theory]
    [InlineData("popcorn", "corn", false)]                    // whole-word: excluding corn must not hide popcorn
    [InlineData("corn flakes", "corn", true)]
    [InlineData("Corn Flakes", "CORN", true)]                 // case-insensitive on both sides
    [InlineData("extra virgin olive oil", "olive oil", true)] // multi-word term matches its full phrase
    [InlineData("olive tapenade", "olive oil", false)]        // ...never just one of its words
    [InlineData("canola oil", "olive oil", false)]
    public void PhraseSafeHit_matches_whole_words_and_full_phrases_only(string text, string term, bool expected)
        => Assert.Equal(expected, BasketOptimizerService.PhraseSafeHit(text, term));

    // ---------------- exact threshold boundaries at the default gates ----------------
    // Move gate is coded `cand <= price * (1 - pct)` (inclusive), store gate `saving >= minSave`
    // (inclusive). 10.00 * (1 - 0.10) == 9.00 exactly in doubles, so these boundaries are FP-safe.

    // qty 6 makes the would-be saving $5.94/$6.00, comfortably over the $5 floor — so the ONLY thing
    // separating these two tests is the percent comparison itself.
    [Fact]
    public void Item_exactly_ten_percent_cheaper_moves_stores()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int anchor = Listed(db, "anchor"), y = Listed(db, "y", quantity: 6);
        Price(db, anchor, a, 1.00); Price(db, anchor, b, 50.00); // keeps A primary
        Price(db, y, a, 10.00); Price(db, y, b, 9.00);           // exactly 10% cheaper

        var r = svc.Optimize("best_savings");

        Assert.Equal(2, r.Stores.Count);
        Assert.Equal(b, StoreOf(r, y));
    }

    [Fact]
    public void Item_just_under_ten_percent_cheaper_stays_at_primary()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int anchor = Listed(db, "anchor"), y = Listed(db, "y", quantity: 6);
        Price(db, anchor, a, 1.00); Price(db, anchor, b, 50.00);
        Price(db, y, a, 10.00); Price(db, y, b, 9.01);           // 9.9% cheaper — under the gate

        var r = svc.Optimize("best_savings");

        Assert.Single(r.Stores);
        Assert.Equal(a, StoreOf(r, y));
    }

    // Both floor tests are 50% cheaper (far past the percent gate) — only the dollar floor decides.
    [Fact]
    public void Store_saving_of_exactly_five_dollars_joins()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int anchor = Listed(db, "anchor"), y = Listed(db, "y");
        Price(db, anchor, a, 1.00); Price(db, anchor, b, 50.00);
        Price(db, y, a, 10.00); Price(db, y, b, 5.00);           // saving exactly $5.00

        var r = svc.Optimize("best_savings");

        Assert.Equal(2, r.Stores.Count);
        Assert.Equal(b, StoreOf(r, y));
    }

    [Fact]
    public void Store_saving_just_under_five_dollars_is_refused()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        int a = Store(db, "A"), b = Store(db, "B");
        int anchor = Listed(db, "anchor"), y = Listed(db, "y");
        Price(db, anchor, a, 1.00); Price(db, anchor, b, 50.00);
        Price(db, y, a, 10.00); Price(db, y, b, 5.01);           // saving $4.99

        var r = svc.Optimize("best_savings");

        Assert.Single(r.Stores);
        Assert.Equal(a, StoreOf(r, y));
    }

    // The cap is `plannedStores.Count < maxStores`: the same fully-qualifying store refused at the
    // default 3 is admitted once the setting says 4 — the cap alone was the refusal.
    [Fact]
    public void Fourth_qualifying_store_is_refused_at_cap_and_admitted_when_raised()
    {
        using var db = new TempDb();
        var (svc, config) = Build(db); // default maxStores = 3
        int a = Store(db, "A"), b = Store(db, "B"), c = Store(db, "C"), d = Store(db, "D");
        // anchor stays at A so the primary's plan is never empty (an empty store plan is dropped from the result)
        int anchor = Listed(db, "anchor");
        Price(db, anchor, a, 1); Price(db, anchor, b, 50); Price(db, anchor, c, 50); Price(db, anchor, d, 50);
        int i1 = Listed(db, "i1"), i2 = Listed(db, "i2"), i3 = Listed(db, "i3");
        foreach (var id in new[] { i1, i2, i3 }) Price(db, id, a, 20);
        // each rival is cheap on exactly one item (>=10% and >=$5), expensive elsewhere (A stays primary)
        Price(db, i1, b, 14); Price(db, i2, b, 30); Price(db, i3, b, 30); // save 6 — the weakest rival
        Price(db, i1, c, 30); Price(db, i2, c, 13); Price(db, i3, c, 30); // save 7
        Price(db, i1, d, 30); Price(db, i2, d, 30); Price(db, i3, d, 12); // save 8

        var capped = svc.Optimize("best_savings");
        Assert.Equal(3, capped.Stores.Count);
        Assert.DoesNotContain(capped.Stores, s => s.StoreId == b); // qualifies, refused by the cap alone
        Assert.Equal(a, StoreOf(capped, i1));

        config.Save(config.Load() with { MaxStores = 4 });
        var raised = svc.Optimize("best_savings");
        Assert.Equal(b, StoreOf(raised, i1)); // admitted now — the cap alone was the refusal
        Assert.Equal(4, raised.Stores.Count); // anchor keeps A in the plan, B/C/D each carry one item
        Assert.Equal(a, StoreOf(raised, anchor));
    }

    // I2: usualAvg is now inflation-adjusted + recency-weighted, so an old cheap price no longer drags the
    // baseline down. A naive two-point mean of (2, 4) = 3 would report SaveVsUsual = 3 - 4 = -1 (looks like
    // overpaying); the weighted-adjusted baseline stays near the recent 4, so the phantom "overpay" vanishes.
    [Fact]
    public void Old_cheap_point_no_longer_depresses_usual_average()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var a = Store(db, "A");
        var milk = Listed(db, "milk");
        PricesRepo.AddPricePoint(db.Conn, milk, a, 2.00, "each", source: "manual", date: "2026-01-01"); // old & cheap
        PricesRepo.AddPricePoint(db.Conn, milk, a, 4.00, "each", source: "manual", date: Today);        // recent

        var plan = svc.Optimize("best_savings").Stores.SelectMany(s => s.Items).Single(i => i.ItemId == milk);
        Assert.Equal(4.00, plan.UnitPrice); // chosen = most-recent store price
        Assert.NotNull(plan.SaveVsUsual);
        Assert.True(plan.SaveVsUsual > -0.7,
            $"weighted-adjusted usual should stay near 4, not the naive mean 3 (SaveVsUsual={plan.SaveVsUsual})");
    }

    [Fact]
    public void Totals_are_quantity_weighted()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var store = Store(db, "A");
        var milk = Listed(db, "milk", quantity: 3);
        Price(db, milk, store, 2);

        var r = svc.Optimize("best_savings");

        Assert.Equal(6, r.BasketTotalEstimated);
        Assert.Equal(6, Assert.Single(r.Stores).TotalEstimated);
    }

    [Fact]
    public void Hybrid_assignment_does_not_exceed_max_stores()
    {
        using var db = new TempDb();
        var (svc, config) = Build(db);
        config.Save(config.Load() with { MaxStores = 2 });

        int a = Store(db, "A"), b = Store(db, "B"), c = Store(db, "C");
        int x = Listed(db, "x"), y = Listed(db, "y"), z = Listed(db, "z");
        Price(db, x, a, 1);
        Price(db, y, b, 10);
        Price(db, z, c, 10);

        var r = svc.Optimize("best_savings");

        Assert.True(r.Stores.Count <= 2);
    }

    // Plan write-back: planned_store_id assigned per the plan, in one transaction.
    [Fact]
    public void ApplyOptimizerPlan_writes_planned_store_ids()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var list = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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
        var list = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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

    // Unlinked rows (item_id NULL) can't be priced — they must be disclosed, not silently dropped.
    [Fact]
    public void Unmapped_list_rows_produce_a_warning()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var store = Store(db, "A");
        var milk = Listed(db, "milk");
        Price(db, milk, store, 4);
        ShoppingListRepo.AddItem(db.Conn, "mystery scribble"); // no item link

        var r = svc.Optimize("best_savings");

        Assert.Contains(r.Warnings, w => w.Contains("aren't linked"));
        Assert.Equal(4, r.BasketTotalEstimated); // unlinked row priced nothing
    }

    [Fact]
    public void Fully_mapped_basket_has_no_unlinked_warning()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var store = Store(db, "A");
        Price(db, Listed(db, "milk"), store, 4);

        Assert.DoesNotContain(svc.Optimize("best_savings").Warnings, w => w.Contains("aren't linked"));
    }

    // Split-brain regression (flyer unification): an active flyer_deals row must win the item's quote.
    [Fact]
    public void Active_flyer_deal_prices_the_basket_line()
    {
        using var db = new TempDb();
        var (svc, _) = Build(db);
        var store = Store(db, "A");
        var milk = Listed(db, "milk");
        Price(db, milk, store, 10.0); // recent non-flyer price

        var flyerId = FlyersRepo.CreateFlyerBatch(db.Conn, store, Today, Today);
        FlyersRepo.AddDeals(db.Conn, new[] { Deal(flyerId, store, "Milk", unitPrice: 7.0m, itemId: milk) });

        var plan = Assert.Single(Assert.Single(svc.Optimize("best_savings").Stores).Items);
        Assert.Equal("flyer", plan.Source);
        Assert.Equal(7.0, plan.UnitPrice);
    }

    [Fact]
    public void ApplyOptimizerPlan_does_not_duplicate_hard_excluded_warning()
    {
        using var db = new TempDb();
        var (svc, config) = Build(db);
        var cfg = config.Load();
        cfg = cfg with { Preferences = cfg.Preferences! with { HardExcludes = ["pork"] } };
        config.Save(cfg);

        var store = Store(db, "A");
        var pork = Listed(db, "pork chops");
        Price(db, pork, store, 8);

        var result = svc.Optimize("best_savings");
        var applied = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory)).ApplyOptimizerPlanToActiveList(result);

        Assert.Single(applied.Warnings, w => w.Contains("hard-excluded"));
    }
}
