using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// V3 Phase 3 gates: plan->list upsert semantics (grill Q6 — open-row matching, item_id-first, safe
// backfill, additive unit-aware quantity merge, metadata preservation) and the snapshot round-trip
// (grill Q11 — atomic save, stale-id validation with name fallback).
public sealed class SmartWeekServiceTests
{
    private static SmartWeekPlanSnapshot Snap(params (int? Id, string Name)[] recipes) => new(
        WeekStart: "2026-08-03", ConfirmedAt: "2026-08-03T10:00:00Z", Servings: 4,
        GroceryCap: 90.0, ProteinGoal: 25.0, WholeFoodPreferred: true,
        Recipes: recipes.Select(r => new SmartWeekSnapshotRecipe(r.Id, r.Name)).ToList(),
        Ingredients: []);

    private static SmartWeekConfirmIngredient Ing(string name, double qty, string unit, int? itemId = null,
        double? conf = null, params string[] recipes) =>
        new(name, qty, unit, itemId, conf, recipes.ToList());

    [Fact]
    public void New_ingredients_insert_with_used_in_notes()
    {
        using var db = new TempDb();
        var svc = new SmartWeekService(db.Factory);

        var outcomes = svc.ConfirmPlan(Snap((1, "Stir Fry")), [Ing("rice", 300, "g", recipes: "Stir Fry")]);

        Assert.Equal("added", Assert.Single(outcomes).Action);
        var row = Assert.Single(ShoppingListRepo.ListActiveItems(db.Conn));
        Assert.Equal("rice", row.DisplayName);
        Assert.Equal(300, row.Quantity);
        Assert.Contains("Used in: Stir Fry", row.Notes);
        Assert.Equal("Smart Week", row.AddedBy);
    }

    [Fact]
    public void Item_id_match_merges_additively_and_preserves_metadata()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rowId = ShoppingListRepo.AddItem(db.Conn, "Rice (basmati)", 2.0, "each",
            notes: "the good kind", plannedStoreId: store, itemId: rice, priority: "must_have");

        var svc = new SmartWeekService(db.Factory);
        var outcomes = svc.ConfirmPlan(Snap((1, "A")), [Ing("rice", 3, "each", itemId: rice, recipes: "A")]);

        Assert.Equal("merged", Assert.Single(outcomes).Action);
        var row = Assert.Single(ShoppingListRepo.ListActiveItems(db.Conn));
        Assert.Equal(rowId, row.Id);
        Assert.Equal(5.0, row.Quantity);                  // 2 + 3, additive
        Assert.Equal("Rice (basmati)", row.DisplayName);  // display name preserved
        Assert.Equal("must_have", row.Priority);          // never downgraded
        Assert.Equal(store, row.PlannedStoreId);          // store choice preserved
        Assert.Contains("the good kind", row.Notes);      // existing notes kept
        Assert.Contains("Used in: A", row.Notes);
    }

    [Fact]
    public void Quantity_merge_converts_compatible_units()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        ShoppingListRepo.AddItem(db.Conn, "rice", 1.0, "kg", itemId: rice);

        new SmartWeekService(db.Factory).ConfirmPlan(Snap((1, "A")),
            [Ing("rice", 500, "g", itemId: rice, recipes: "A")]);

        var row = Assert.Single(ShoppingListRepo.ListActiveItems(db.Conn));
        Assert.Equal(1.5, row.Quantity, 3); // 1 kg + 500 g, converted into the ROW's unit
        Assert.Equal("kg", row.Unit);
    }

    [Fact]
    public void Incompatible_units_keep_quantity_and_disclose_in_note()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        ShoppingListRepo.AddItem(db.Conn, "rice", 2.0, "each", itemId: rice);

        var outcomes = new SmartWeekService(db.Factory).ConfirmPlan(Snap((1, "A")),
            [Ing("rice", 300, "g", itemId: rice, recipes: "A")]);

        Assert.Equal("merged_note_only", Assert.Single(outcomes).Action);
        var row = Assert.Single(ShoppingListRepo.ListActiveItems(db.Conn));
        Assert.Equal(2.0, row.Quantity); // untouched — each vs g is not convertible
        Assert.Contains("plan also needs 300 g", row.Notes);
    }

    [Fact]
    public void Name_match_backfills_null_item_id_but_never_replaces_a_set_one()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        var beans = ItemsRepo.CreateItem(db.Conn, "beans").Id;
        ShoppingListRepo.AddItem(db.Conn, "rice", 1.0, "g");                  // unmapped -> backfill ok
        ShoppingListRepo.AddItem(db.Conn, "beans", 1.0, "g", itemId: beans); // mapped -> conflict guard

        var svc = new SmartWeekService(db.Factory);
        svc.ConfirmPlan(Snap((1, "A")), [
            Ing("rice", 100, "g", itemId: rice, conf: 0.95, recipes: "A"),
            Ing("beans", 100, "g", itemId: rice /* WRONG id on purpose */, conf: 0.95, recipes: "A"),
        ]);

        var rows = ShoppingListRepo.ListActiveItems(db.Conn);
        Assert.Equal(rice, rows.Single(r => r.DisplayName == "rice").ItemId); // backfilled
        var beansRows = rows.Where(r => r.DisplayName == "beans").ToList();
        Assert.Equal(2, beansRows.Count); // conflicting ids never merge — a second row is correct
        Assert.Contains(beansRows, r => r.ItemId == beans);
        Assert.Contains(beansRows, r => r.ItemId == rice);
    }

    [Fact]
    public void Checked_off_rows_never_match()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        var done = ShoppingListRepo.AddItem(db.Conn, "rice", 1.0, "g", itemId: rice);
        ShoppingListRepo.SetCheckedOff(db.Conn, done, true);

        new SmartWeekService(db.Factory).ConfirmPlan(Snap((1, "A")),
            [Ing("rice", 300, "g", itemId: rice, recipes: "A")]);

        var open = ShoppingListRepo.ListActiveItems(db.Conn);
        Assert.Equal(300, Assert.Single(open).Quantity); // fresh row; the completed purchase is untouched
        Assert.Equal(1.0, ShoppingListRepo.GetItem(db.Conn, done)!.Quantity);
    }

    [Fact]
    public void Snapshot_round_trips_and_supplies_recent_recipe_ids()
    {
        using var db = new TempDb();
        var svc = new SmartWeekService(db.Factory);

        svc.ConfirmPlan(Snap((3, "A"), (100005, "My Custom")), []);

        var snap = svc.LoadCurrent();
        Assert.NotNull(snap);
        Assert.Equal("2026-08-03", snap!.WeekStart);
        Assert.Equal(25.0, snap.ProteinGoal);
        Assert.Equal(new[] { 3, 100005 }, svc.RecentRecipeIds().OrderBy(x => x));
    }

    [Fact]
    public void Stale_item_ids_are_dropped_and_re_resolved_by_name()
    {
        using var db = new TempDb();
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        var svc = new SmartWeekService(db.Factory);

        var snap = Snap((1, "A")) with
        {
            Ingredients = [new SmartWeekSnapshotIngredient("rice", 300, "g", ItemId: 999_999, RecipeNames: ["A"])],
        };
        svc.ConfirmPlan(snap, []);

        var loaded = svc.LoadCurrent();
        // 999999 does not exist (simulates a post-merge stale id) -> re-resolved to the live "rice" item.
        Assert.Equal(rice, Assert.Single(loaded!.Ingredients).ItemId);
    }

    [Fact]
    public void Confirm_is_one_transaction_snapshot_and_rows_together()
    {
        using var db = new TempDb();
        var svc = new SmartWeekService(db.Factory);
        svc.ConfirmPlan(Snap((1, "A")), [Ing("rice", 300, "g", recipes: "A")]);

        Assert.Equal(1L, Count(db.Conn, "selected_smart_week_plan"));
        Assert.Equal(1L, Count(db.Conn, "shopping_list"));

        // Re-confirm replaces the singleton, never accumulates.
        svc.ConfirmPlan(Snap((2, "B")), []);
        Assert.Equal(1L, Count(db.Conn, "selected_smart_week_plan"));
        Assert.Equal(new[] { 2 }, svc.RecentRecipeIds());
    }
}
