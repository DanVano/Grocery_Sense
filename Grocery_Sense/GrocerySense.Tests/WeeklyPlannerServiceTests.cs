using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// Port of tests/planning/test_weekly_planner.py.
public sealed class WeeklyPlannerServiceTests
{
    
    private static WeeklyPlannerService Planner(TempDb db) => new(
        new MealSuggestionService(new RecipeEngine(Fixtures.RecipesSamplePath), priceHistory: null, factory: db.Factory),
        new IngredientMappingService(db.Factory), db.Factory);

    private static ShoppingListService List(TempDb db) => new(db.Factory, new IngredientMappingService(db.Factory));

    private static SuggestedMeal Meal(string name, string[] ingredients, int rid = 1) =>
        new(new Recipe(rid, name, null, ingredients, Array.Empty<string>(), Array.Empty<string>()),
            1.0, 0.5, 0.0, 0.0, 0.0, Array.Empty<string>());

    // ---- AggregateIngredients (pure) ----

    [Fact]
    public void Aggregate_dedups_by_normalized_name()
    {
        var planned = WeeklyPlannerService.AggregateIngredients(new[]
        {
            Meal("A", new[] { "Rice", "garlic" }, 1),
            Meal("B", new[] { "RICE ", "onion" }, 2),
        });
        Assert.Equal(1, planned.Count(p => p.Name.ToLowerInvariant() == "rice"));
    }

    [Fact]
    public void Aggregate_tracks_all_recipes_using_an_ingredient()
    {
        var planned = WeeklyPlannerService.AggregateIngredients(new[]
        {
            Meal("A", new[] { "rice" }, 1), Meal("B", new[] { "rice" }, 2), Meal("C", new[] { "rice" }, 3),
        });
        var rice = planned.First(p => p.Name.ToLowerInvariant() == "rice");
        Assert.Equal(new[] { "A", "B", "C" }, rice.RecipeNames.OrderBy(x => x));
        Assert.Equal(3, rice.ApproximateCount);
    }

    [Fact]
    public void Aggregate_sorts_by_count_then_name()
    {
        var planned = WeeklyPlannerService.AggregateIngredients(new[]
        {
            Meal("A", new[] { "rice", "zucchini" }, 1),
            Meal("B", new[] { "rice", "apples" }, 2),
        });
        Assert.Equal("rice", planned[0].Name.ToLowerInvariant());
        var singletons = planned.Skip(1).Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.True(singletons.IndexOf("apples") < singletons.IndexOf("zucchini"));
    }

    [Fact]
    public void Aggregate_empty_returns_empty() =>
        Assert.Empty(WeeklyPlannerService.AggregateIngredients(Array.Empty<SuggestedMeal>()));

    // ---- BuildWeeklyPlan ----

    [Fact]
    public void Build_returns_plan_with_suggestions_and_ingredients()
    {
        using var db = new TempDb();
        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 3);
        Assert.Equal(3, plan.Suggestions.Count);
        Assert.NotEmpty(plan.PlannedIngredients);
    }

    [Fact]
    public void Build_num_recipes_caps_suggestions()
    {
        using var db = new TempDb();
        Assert.Equal(2, Planner(db).BuildWeeklyPlan(numRecipes: 2).Suggestions.Count);
    }

    [Fact]
    public void Build_annotates_a_seeded_item()
    {
        using var db = new TempDb();
        ItemsRepo.CreateItem(db.Conn, "rice");
        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 4);
        var rice = plan.PlannedIngredients.FirstOrDefault(p => p.Name.ToLowerInvariant() == "rice");
        Assert.NotNull(rice);
        Assert.NotNull(rice!.ItemId);
        Assert.Equal("rice", rice.CanonicalName);
        Assert.Contains(rice.MatchMethod, new[] { "alias", "fuzzy" });
        Assert.NotNull(rice.MatchConfidence);
    }

    [Fact]
    public void Build_marks_unmatched_as_none()
    {
        using var db = new TempDb();
        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 2);
        Assert.All(plan.PlannedIngredients, ing =>
        {
            Assert.Null(ing.ItemId);
            Assert.Equal("none", ing.MatchMethod);
        });
    }

    // ---- pantry inference (LikelyHave) ----


    // Receipt purchases of "rice" spaced 10 days apart, ending `lastDaysAgo` ago (cadence interval = 10d).
    private static void SeedRiceCadence(TempDb db, int lastDaysAgo)
    {
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        foreach (var d in new[] { lastDaysAgo + 30, lastDaysAgo + 20, lastDaysAgo + 10, lastDaysAgo })
        {
            var rid = AddReceipt(db, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, 4.0, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
    }

    [Fact]
    public void Build_marks_recently_bought_ingredient_likely_have()
    {
        using var db = new TempDb();
        SeedRiceCadence(db, lastDaysAgo: 2); // 2 days since < 0.75 x 10-day interval
        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 4);

        var rice = plan.PlannedIngredients.First(p => p.Name.ToLowerInvariant() == "rice");
        Assert.True(rice.LikelyHave);
        Assert.Contains("bought 2 day(s) ago", rice.LikelyHaveReason);
    }

    [Fact]
    public void Build_does_not_mark_likely_have_past_the_cadence_fraction()
    {
        using var db = new TempDb();
        SeedRiceCadence(db, lastDaysAgo: 9); // 9 days since >= 0.75 x 10-day interval
        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 4);

        var rice = plan.PlannedIngredients.First(p => p.Name.ToLowerInvariant() == "rice");
        Assert.False(rice.LikelyHave);
        Assert.Null(rice.LikelyHaveReason);
    }

    [Fact]
    public void Build_never_marks_likely_have_without_cadence()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        var rid = AddReceipt(db, store, DaysAgo(1)); // single purchase -> no interval -> no inference
        PricesRepo.AddPricePoint(db.Conn, item, store, 4.0, "each", quantity: 1.0,
            source: "receipt", date: DaysAgo(1), receiptId: rid);

        var plan = Planner(db).BuildWeeklyPlan(numRecipes: 4);
        Assert.False(plan.PlannedIngredients.First(p => p.Name.ToLowerInvariant() == "rice").LikelyHave);
    }

    [Fact]
    public void Persist_appends_likely_have_reason_to_notes_but_still_adds_the_row()
    {
        using var db = new TempDb();
        SeedRiceCadence(db, lastDaysAgo: 2);
        var planner = Planner(db);
        planner.PersistToShoppingList(planner.BuildWeeklyPlan(numRecipes: 4));

        var row = List(db).GetActiveItems().First(r => r.DisplayName.ToLowerInvariant() == "rice");
        Assert.Contains("May already have:", row.Notes);
    }

    // ---- persistence ----

    [Fact]
    public void Persist_writes_every_ingredient_with_used_in_notes()
    {
        using var db = new TempDb();
        var planner = Planner(db);
        var plan = planner.BuildWeeklyPlan(numRecipes: 2);
        planner.PersistToShoppingList(plan, addedBy: "test");
        var items = List(db).GetActiveItems();
        Assert.Equal(plan.PlannedIngredients.Count, items.Count);
        Assert.All(items, row => Assert.Contains("Used in:", row.Notes));
    }

    [Fact]
    public void Persist_quantity_reflects_approximate_count()
    {
        using var db = new TempDb();
        var planner = Planner(db);
        var plan = planner.BuildWeeklyPlan(numRecipes: 4);
        planner.PersistToShoppingList(plan);
        var byName = List(db).GetActiveItems().ToDictionary(i => i.DisplayName.ToLowerInvariant());
        foreach (var ing in plan.PlannedIngredients)
        {
            var row = byName[ing.Name.ToLowerInvariant()];
            Assert.True(row.Quantity >= 1.0);
            if (ing.ApproximateCount > 1) Assert.Equal((double)ing.ApproximateCount, row.Quantity);
        }
    }

    // Multi-row tx-write convention: one poisoned insert rolls the whole persist back — never a partial list.
    [Fact]
    public void Persist_leaves_zero_rows_when_one_insert_fails()
    {
        using var db = new TempDb();
        var planner = Planner(db);
        var plan = planner.BuildWeeklyPlan(numRecipes: 2);
        Assert.True(plan.PlannedIngredients.Count >= 2); // earlier rows must exist for "partial" to be possible

        // Poison the LAST ingredient's insert, so every earlier row has already landed inside the tx.
        var poisoned = plan.PlannedIngredients[^1].Name.Trim().Replace("'", "''");
        Exec(db.Conn, $"""
            CREATE TRIGGER poison_persist BEFORE INSERT ON shopping_list
            WHEN NEW.display_name = '{poisoned}'
            BEGIN SELECT RAISE(ABORT, 'poisoned'); END;
            """);

        Assert.ThrowsAny<SqliteException>(() => planner.PersistToShoppingList(plan));
        Assert.Equal(0L, Count(db.Conn, "shopping_list"));
    }
}
