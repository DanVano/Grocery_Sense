using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class UserRecipeServiceTests
{
    
    [Fact]
    public void ListAsRecipes_normalizes_and_offsets_ids()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        var id = svc.Add("Dad's Chili", 6, new[] { " ground beef ", "", "beans" }, Array.Empty<string>(),
            new[] { " Comfort " });

        var recipe = Assert.Single(svc.ListAsRecipes());
        Assert.Equal(UserRecipeService.UserRecipeIdOffset + id, recipe.Id);
        Assert.Equal(new[] { "ground beef", "beans" }, recipe.Ingredients); // trimmed, blanks dropped
        Assert.Equal(new[] { "comfort" }, recipe.Tags);                     // lowercased
        Assert.Equal(6, recipe.Servings);
    }

    [Fact]
    public void Add_rejects_blank_name_and_empty_ingredients()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        Assert.Throws<ArgumentException>(() => svc.Add("  ", null, new[] { "beef" }, [], []));
        Assert.Throws<ArgumentException>(() => svc.Add("Chili", null, Array.Empty<string>(), [], []));
    }

    [Fact]
    public void Duplicate_name_surfaces_a_clear_error()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        svc.Add("Chili", null, new[] { "beef" }, [], []);
        var ex = Assert.Throws<InvalidOperationException>(() => svc.Add("chili", null, new[] { "beef" }, [], []));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Engine_merges_user_recipes_and_they_shadow_same_name_catalog_entries()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        svc.Add("Beef Stir Fry", 2, new[] { "beef", "peppers" }, [], []); // same name as catalog recipe
        svc.Add("Dad's Chili", 6, new[] { "ground beef", "beans" }, [], []);

        var engine = new RecipeEngine(Fixtures.RecipesSamplePath, extraRecipes: svc.ListAsRecipes);
        var all = engine.LoadAllRecipes();

        Assert.Single(all, r => r.Name == "Beef Stir Fry");                     // no duplicate
        Assert.Equal(new[] { "beef", "peppers" }, engine.GetRecipeByName("Beef Stir Fry")!.Ingredients); // user wins
        Assert.Contains(all, r => r.Name == "Dad's Chili");
        Assert.Equal(9, all.Count); // 8 catalog (1 shadowed -> 7 remain) + 2 user
    }

    [Fact]
    public void User_recipe_with_allergen_is_filtered_by_the_profile()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        svc.Add("Peanut Bomb", 2, new[] { "peanuts", "noodles" }, [], []);

        var engine = new RecipeEngine(Fixtures.RecipesSamplePath, extraRecipes: svc.ListAsRecipes);
        var safe = engine.RecipesMatchingProfile(new MealProfile { Allergies = new[] { "peanuts" } });
        Assert.DoesNotContain(safe, r => r.Name == "Peanut Bomb");
    }

    [Fact]
    public void Engine_picks_up_changes_without_reload()
    {
        using var db = new TempDb();
        var svc = new UserRecipeService(db.Factory);
        var engine = new RecipeEngine(Fixtures.RecipesSamplePath, extraRecipes: svc.ListAsRecipes);
        Assert.Equal(8, engine.LoadAllRecipes().Count);
        svc.Add("Dad's Chili", 6, new[] { "beef" }, [], []);
        Assert.Equal(9, engine.LoadAllRecipes().Count); // extras re-read per call
    }
}
