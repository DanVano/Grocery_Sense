using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public sealed class FamilyRequestsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_family_{Guid.NewGuid():N}");
    public FamilyRequestsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static readonly string SampleFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "recipes_sample.json");

    private (FamilyRequestsService Svc, ConfigStore Config, ShoppingListService List) Build(TempDb db)
    {
        var config = new ConfigStore(_dir);
        var list = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var svc = new FamilyRequestsService(config, list, new RecipeEngine(SampleFixture),
            new PreferencesService(config), db.Factory);
        return (svc, config, list);
    }

    private void SetMasterAllergies(ConfigStore config, params string[] allergies)
    {
        var cfg = config.Load();
        var master = config.GetMasterMember();
        var profile = new Dictionary<string, object?>(master.Profile) { ["allergies"] = allergies.ToList() };
        var members = cfg.Household.Members.Select(m => m.Id == master.Id ? m with { Profile = profile } : m).ToList();
        config.Save(cfg with { Household = cfg.Household with { Members = members } });
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

    [Fact]
    public void Unknown_recipe_throws()
    {
        using var db = new TempDb();
        var (svc, config, _) = Build(db);
        var kid = config.AddMember("Kid");
        Assert.Throws<ArgumentException>(() => svc.PickMeal(kid.Id, "No Such Dish"));
    }
}
