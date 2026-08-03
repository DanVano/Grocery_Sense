using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class FamilyRequestsServiceTests : TempDirTestBase
{

    
    private (FamilyRequestsService Svc, ConfigStore Config, ShoppingListService List) Build(TempDb db)
    {
        var config = new ConfigStore(_dir);
        var list = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var svc = new FamilyRequestsService(config, new RecipeEngine(Fixtures.RecipesSamplePath),
            new PreferencesService(config), new IngredientMappingService(db.Factory), db.Factory);
        return (svc, config, list);
    }

    // Mirrors Build's construction with a MealSuggestionService appended (deal-ranked picks).
    private (FamilyRequestsService Svc, ConfigStore Config) BuildWithMeals(TempDb db)
    {
        var config = new ConfigStore(_dir);
        var engine = new RecipeEngine(Fixtures.RecipesSamplePath);
        var meals = new MealSuggestionService(engine, priceHistory: null, factory: db.Factory);
        var svc = new FamilyRequestsService(config, engine, new PreferencesService(config),
            new IngredientMappingService(db.Factory), db.Factory, meals);
        return (svc, config);
    }


    private void SetMasterAllergies(ConfigStore config, params string[] allergies)
    {
        var cfg = config.Load();
        config.Save(cfg with { Preferences = cfg.Preferences! with { Allergies = [.. allergies] } });
    }

    [Fact]
    public void Secondary_meal_pick_adds_attributed_items_and_creates_a_request()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var kid = config.AddMember("Kid");

        var req = svc.PickMeal(kid.Id, "Beef Stir Fry"); // 5 ingredients

        Assert.NotNull(req);
        Assert.Equal("meal", req!.Kind);
        Assert.Equal("Beef Stir Fry", req.Label);
        Assert.Equal("Kid", req.MemberName);
        Assert.Equal(5, req.ItemRowIds.Count);
        Assert.Equal(1, svc.UnreviewedCount());

        var items = list.GetActiveItems();
        Assert.Equal(5, items.Count);
        Assert.All(items, i => Assert.Equal("Kid", i.AddedBy));
    }

    [Fact]
    public void Master_meal_pick_adds_items_but_creates_no_request()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var master = config.GetMasterMember();

        var req = svc.PickMeal(master.Id, "Beef Stir Fry");

        Assert.Null(req);
        Assert.Equal(0, svc.UnreviewedCount());
        Assert.Equal(5, list.GetActiveItems().Count); // still added to the shared list
    }

    [Fact]
    public void Secondary_item_pick_creates_an_item_request()
    {
        using var db = new TempDb();
        var (svc, config, _) = Build(db);
        var kid = config.AddMember("Kid");

        var req = svc.PickItem(kid.Id, "gummy bears");

        Assert.NotNull(req);
        Assert.Equal("item", req!.Kind);
        Assert.Equal("gummy bears", req.Label);
        Assert.Single(req.ItemRowIds);
    }

    [Fact]
    public void Allergen_recipe_is_not_pickable()
    {
        using var db = new TempDb();
        var (svc, config, _) = Build(db);
        SetMasterAllergies(config, "peanuts");

        var pickable = svc.PickableRecipes();

        Assert.DoesNotContain("Peanut Chicken Noodles", pickable);
        Assert.Contains("Beef Stir Fry", pickable);
    }

    [Fact]
    public void Ranked_puts_recipe_with_flyer_deals_first_and_flags_it_on_sale()
    {
        using var db = new TempDb();
        var (svc, _) = BuildWithMeals(db);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        // Two of Beef Stir Fry's five ingredients on sale -> DealScore 0.4 > 0.2 threshold.
        SeedActiveFlyerDeal(db, store, "beef", "2.99");
        SeedActiveFlyerDeal(db, store, "broccoli", "1.49");

        var ranked = svc.PickableRecipesRanked();

        Assert.Equal("Beef Stir Fry", ranked[0].Name);
        Assert.True(ranked[0].OnSaleThisWeek);
        Assert.False(ranked.First(p => p.Name == "Quinoa Salad").OnSaleThisWeek);
    }

    [Fact]
    public void Ranked_without_meals_service_falls_back_to_alphabetical_unflagged()
    {
        using var db = new TempDb();
        var (svc, _, _) = Build(db); // no MealSuggestionService
        var ranked = svc.PickableRecipesRanked();
        Assert.Equal(svc.PickableRecipes(), ranked.Select(p => p.Name).ToList());
        Assert.All(ranked, p => Assert.False(p.OnSaleThisWeek));
    }

    [Fact]
    public void Ranked_still_hides_allergen_recipes()
    {
        using var db = new TempDb();
        var (svc, config) = BuildWithMeals(db);
        SetMasterAllergies(config, "peanuts");
        Assert.DoesNotContain("Peanut Chicken Noodles", svc.PickableRecipesRanked().Select(p => p.Name));
    }

    [Fact]
    public void Remove_request_soft_deletes_exactly_its_rows_and_marks_reviewed()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var kid = config.AddMember("Kid");
        var req = svc.PickMeal(kid.Id, "Beef Stir Fry")!;
        Assert.Equal(5, list.GetActiveItems().Count);

        svc.RemoveRequest(req.Id);

        Assert.Empty(list.GetActiveItems());        // exactly the 5 created rows removed
        Assert.Equal(0, svc.UnreviewedCount());     // and marked reviewed
    }

    // Defense-in-depth: even when a stale /family list surfaces an allergen recipe, PickMeal's own
    // ProfileFilter recheck must refuse it — throw, and leave zero rows on the shared list.
    [Fact]
    public void Meal_pick_of_allergen_recipe_throws_and_writes_no_rows()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        SetMasterAllergies(config, "peanuts");
        var kid = config.AddMember("Kid");

        Assert.Throws<InvalidOperationException>(() => svc.PickMeal(kid.Id, "Peanut Chicken Noodles"));

        Assert.Empty(list.GetActiveItems());
        Assert.Equal(0, svc.UnreviewedCount());
    }

    [Fact]
    public void Unknown_recipe_throws()
    {
        using var db = new TempDb();
        var (svc, config, _) = Build(db);
        var kid = config.AddMember("Kid");
        Assert.Throws<ArgumentException>(() => svc.PickMeal(kid.Id, "No Such Dish"));
    }

    // Pick ingredients map to canonical items (match-only) so family picks reach the optimizer.
    [Fact]
    public void Meal_pick_maps_known_ingredients_to_item_ids()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var beef = ItemsRepo.CreateItem(db.Conn, "Beef").Id; // exact ingredient (case differs), maps via fuzzy
        var kid = config.AddMember("Kid");

        svc.PickMeal(kid.Id, "Beef Stir Fry");

        var rows = list.GetActiveItems();
        Assert.Contains(rows, r => r.ItemId == beef);       // known ingredient linked
        Assert.Contains(rows, r => r.ItemId is null);        // unknown ingredients stay unmapped, never force-created
    }

    // No-partial-rows (CLAUDE.md): if the request insert fails, the ingredient rows roll back with it.
    [Fact]
    public void Meal_pick_rolls_back_ingredient_rows_when_request_insert_fails()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var kid = config.AddMember("Kid");

        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER fail_req BEFORE INSERT ON member_requests " +
                              "BEGIN SELECT RAISE(ABORT, 'boom'); END;";
            cmd.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => svc.PickMeal(kid.Id, "Beef Stir Fry"));

        Assert.Empty(list.GetActiveItems()); // zero ingredient rows survived the failed pick
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "DROP TRIGGER fail_req;";
            cmd.ExecuteNonQuery();
        }
    }

    // No-partial-rows (CLAUDE.md): RemoveRequest soft-deletes the pick's rows + marks reviewed in ONE
    // transaction — if the reviewed-marking write fails, every soft-delete must roll back with it (a
    // half-undone pick would leave rows gone but the request still claiming them).
    [Fact]
    public void Remove_request_rolls_back_soft_deletes_when_mark_reviewed_fails()
    {
        using var db = new TempDb();
        var (svc, config, list) = Build(db);
        var kid = config.AddMember("Kid");
        var req = svc.PickMeal(kid.Id, "Beef Stir Fry")!;
        Assert.Equal(5, list.GetActiveItems().Count);

        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER fail_rev BEFORE UPDATE ON member_requests " +
                              "BEGIN SELECT RAISE(ABORT, 'boom'); END;";
            cmd.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => svc.RemoveRequest(req.Id));

        Assert.Equal(5, list.GetActiveItems().Count); // no partial soft-delete survived the failed undo
        Assert.Equal(1, svc.UnreviewedCount());       // and the request is still unreviewed
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "DROP TRIGGER fail_rev;";
            cmd.ExecuteNonQuery();
        }
    }
}
