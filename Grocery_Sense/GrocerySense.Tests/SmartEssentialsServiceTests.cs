using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// V3 Phase 4 gates: Smart Essentials composes staples/cadence/prices/watches/plan links without inventing
// data, and Cook-This-Deal's targeted build respects mapping credibility and hard exclusions.
public sealed class SmartEssentialsServiceTests : TempDirTestBase
{
    // An item on 4 receipts, 10 days apart, ending lastDaysAgo ago -> staple with a 10d cadence.
    private static int SeedStaple(TempDb db, int storeId, string name, double price, int lastDaysAgo)
    {
        var item = ItemsRepo.CreateItem(db.Conn, name).Id;
        foreach (var d in new[] { lastDaysAgo + 30, lastDaysAgo + 20, lastDaysAgo + 10, lastDaysAgo })
        {
            var rid = AddReceipt(db, storeId, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, storeId, price, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        return item;
    }

    private SmartEssentialsService Build(TempDb db)
    {
        var config = new ConfigStore(_dir);
        var watchlist = new WatchlistService(db.Factory, config);
        var alerts = new PriceDropAlertService(db.Factory);
        return new SmartEssentialsService(db.Factory, watchlist, alerts, new SmartWeekService(db.Factory));
    }

    [Fact]
    public void Overdue_staple_is_due_soon_with_price_context()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        SeedStaple(db, store, "milk", 4.50, lastDaysAgo: 12); // 12 >= 10d cadence -> due
        SeedStaple(db, store, "rice", 6.00, lastDaysAgo: 2);  // 2 < 10d -> not due

        var rows = Build(db).BuildEssentials();

        var milk = rows.Single(r => r.Name == "milk");
        Assert.True(milk.DueSoon);
        Assert.Equal(12, milk.DaysSinceLast);
        Assert.NotNull(milk.CurrentPrice);
        Assert.False(rows.Single(r => r.Name == "rice").DueSoon);
        Assert.Equal("milk", rows[0].Name); // most-overdue first
    }

    [Fact]
    public void Watch_and_plan_links_join_by_item()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var milk = SeedStaple(db, store, "milk", 4.50, lastDaysAgo: 2);

        var config = new ConfigStore(_dir);
        new WatchlistService(db.Factory, config).AddWatch(milk, 3.99);
        var smartWeek = new SmartWeekService(db.Factory);
        smartWeek.ConfirmPlan(new SmartWeekPlanSnapshot("2026-08-03", "2026-08-03T10:00:00Z", 4,
            null, null, false, [new SmartWeekSnapshotRecipe(1, "Porridge")],
            [new SmartWeekSnapshotIngredient("milk", 500, "ml", milk, ["Porridge"])]), []);

        var row = Build(db).BuildEssentials().Single(r => r.Name == "milk");
        Assert.True(row.Watched);
        Assert.Equal(3.99, row.TargetPrice);
        Assert.Equal(new[] { "Porridge" }, row.SupportsRecipes);
    }

    [Fact]
    public void No_staples_yields_empty_never_fabricated()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        Assert.Empty(Build(db).BuildEssentials());
    }

    // ---- Cook This Deal: targeted BuildSmartWeek (V3 F2) ----

    private static WeeklyPlannerService Planner(TempDb db, string _dir2, MealProfile? profile = null)
    {
        var mapper = new IngredientMappingService(db.Factory);
        var meals = new MealSuggestionService(new RecipeEngine(Fixtures.RecipesSamplePath),
            new PriceHistoryService(db.Factory), db.Factory,
            defaultProfile: profile is null ? null : () => profile);
        return new WeeklyPlannerService(meals, mapper, db.Factory,
            new PlanCostService(db.Factory, mapper), new SmartWeekService(db.Factory));
    }

    [Fact]
    public void Targeted_build_returns_only_recipes_using_the_ingredient()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        var build = Planner(db, _dir).BuildSmartWeek(numRecipes: 10, targetIngredients: ["salmon"]);
        Assert.All(build.Suggestions, s =>
            Assert.Contains(s.Recipe.Ingredients, i => i.Contains("salmon", StringComparison.OrdinalIgnoreCase)));
        Assert.NotEmpty(build.Suggestions);
    }

    [Fact]
    public void Targeted_build_with_no_credible_match_is_empty_not_guessed()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        Assert.Empty(Planner(db, _dir).BuildSmartWeek(numRecipes: 10,
            targetIngredients: ["plutonium"]).Suggestions);
    }

    [Fact]
    public void Targeted_build_still_respects_allergies()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        var build = Planner(db, _dir, new MealProfile { Allergies = ["peanuts"] })
            .BuildSmartWeek(numRecipes: 10, targetIngredients: ["chicken"]);
        Assert.DoesNotContain(build.Suggestions, s => s.Recipe.Name == "Peanut Chicken Noodles");
    }
}
