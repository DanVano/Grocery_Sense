using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Xunit;

namespace GrocerySense.Tests;

// Port of tests/planning/test_meal_suggestion.py + the _compute_cost_estimate half of test_recipes_catalog.py.
public sealed class MealSuggestionServiceTests
{
    private static readonly string SampleFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "recipes_sample.json");

    private static MealSuggestionService Svc(TempDb db) =>
        new(new RecipeEngine(SampleFixture), priceHistory: null, factory: db.Factory);

    private static List<string> Names(IEnumerable<SuggestedMeal> s) => s.Select(m => m.Recipe.Name).ToList();

    // ---- hard profile filters (user-safety critical) ----

    [Fact]
    public void Allergy_blocks_recipe()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile { Allergies = ["peanuts"] }, maxRecipes: 20);
        Assert.DoesNotContain("Peanut Chicken Noodles", Names(s));
    }

    [Fact]
    public void Avoid_ingredients_block()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile { AvoidIngredients = ["bread"] }, maxRecipes: 20);
        Assert.DoesNotContain("Bread and Butter", Names(s));
    }

    [Fact]
    public void No_pork_and_no_beef_restrictions()
    {
        using var db = new TempDb();
        Assert.DoesNotContain("Pork Chops with Apple",
            Names(Svc(db).SuggestMealsForWeek(new MealProfile { Restrictions = ["no_pork"] }, maxRecipes: 20)));
        Assert.DoesNotContain("Beef Stir Fry",
            Names(Svc(db).SuggestMealsForWeek(new MealProfile { Restrictions = ["no_beef"] }, maxRecipes: 20)));
    }

    [Fact]
    public void Allergy_applies_even_when_recipe_matches_target_ingredients()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile { Allergies = ["peanuts"] },
            targetIngredients: ["peanuts", "chicken", "soy sauce"], maxRecipes: 20);
        Assert.DoesNotContain("Peanut Chicken Noodles", Names(s));
    }

    [Fact]
    public void Empty_after_filters_returns_empty()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(
            new MealProfile { Allergies = ["rice", "pasta", "peanuts", "beef", "pork", "salmon", "quinoa", "bread"] },
            maxRecipes: 20);
        Assert.Empty(s);
    }

    // ---- scoring + ordering ----

    [Fact]
    public void Returns_suggestions_without_profile()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 5);
        Assert.True(s.Count <= 5);
        Assert.All(s, m => Assert.InRange(m.PreferenceScore, 0.0, 1.0));
    }

    [Fact]
    public void Target_ingredients_bias_candidate_set()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile(), targetIngredients: ["salmon"], maxRecipes: 20);
        Assert.Equal(new[] { "Salmon Teriyaki" }, Names(s));
    }

    [Fact]
    public void Max_recipes_caps_output()
    {
        using var db = new TempDb();
        Assert.Equal(2, Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 2).Count);
    }

    [Fact]
    public void Prefer_meats_and_favorite_tags_lift_preference()
    {
        using var db = new TempDb();
        var beef = Svc(db).SuggestMealsForWeek(new MealProfile { PreferMeats = ["beef"] }, maxRecipes: 20)
            .First(m => m.Recipe.Name == "Beef Stir Fry");
        Assert.True(beef.PreferenceScore > 0.5);

        var chicken = Svc(db).SuggestMealsForWeek(new MealProfile { FavoriteTags = ["weeknight"] }, maxRecipes: 20)
            .First(m => m.Recipe.Name == "Chicken Thighs with Rice");
        Assert.True(chicken.PreferenceScore > 0.5);
    }

    [Fact]
    public void Avoid_meats_pushes_preference_below_neutral_but_total_stays_nonnegative()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile { AvoidMeats = ["pork"] }, maxRecipes: 20);
        var pork = s.FirstOrDefault(m => m.Recipe.Name == "Pork Chops with Apple");
        if (pork is not null)
        {
            Assert.True(pork.PreferenceScore < 0.5);
            Assert.True(pork.TotalScore >= 0.0);
        }
    }

    // ---- variety ----

    [Fact]
    public void Recently_used_recipes_get_negative_variety_with_a_reason()
    {
        using var db = new TempDb();
        var s = Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 20, recentlyUsedRecipeIds: new HashSet<int> { 2 });
        var beef = s.First(m => m.Recipe.Name == "Beef Stir Fry");
        Assert.True(beef.VarietyScore < 0);
        Assert.Contains(beef.Reasons, r => r.ToLowerInvariant().Contains("recently"));
    }

    [Fact]
    public void No_recent_ids_means_zero_variety()
    {
        using var db = new TempDb();
        Assert.All(Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 20), m => Assert.Equal(0.0, m.VarietyScore));
    }

    // ---- flyer-deal integration ----

    [Fact]
    public void Active_flyer_deal_boosts_price_score()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Deal Mart").Id;
        var repo = new FlyersRepo();
        var today = DateTime.Now;
        var batch = repo.CreateFlyerBatch(db.Conn, store,
            today.AddDays(-1).ToString("yyyy-MM-dd"), today.AddDays(6).ToString("yyyy-MM-dd"), sourceType: "test");
        repo.AddDeals(db.Conn, new[]
        {
            new FlyerDeal(0, batch, null, store, null, "chicken thighs", "family pack", "$5.99/kg",
                null, null, 5.99m, "kg", null, null, null, null, null, null, null),
        });

        var s = Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 20);
        var chicken = s.First(m => m.Recipe.Name == "Chicken Thighs with Rice");
        Assert.True(chicken.PriceScore > 0);
    }

    [Fact]
    public void No_deals_means_zero_price_score()
    {
        using var db = new TempDb();
        Assert.All(Svc(db).SuggestMealsForWeek(new MealProfile(), maxRecipes: 20), m => Assert.Equal(0.0, m.PriceScore));
    }

    // ---- injected engine is USED (Python's bug is fixed here) ----

    [Fact]
    public void Injected_recipe_engine_drives_results()
    {
        using var db = new TempDb();
        var empty = new MealSuggestionService(new RecipeEngine(Path.Combine(Path.GetTempPath(), "missing.json")),
            factory: db.Factory);
        Assert.Empty(empty.SuggestMealsForWeek(new MealProfile(), maxRecipes: 3));
    }

    // ---- cost estimate (test_recipes_catalog #2) ----

    private static Recipe Recipe(int? servings, params string[] ingredients) =>
        new(99, "Test", servings, ingredients, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void Cost_all_known()
    {
        var baseline = new Dictionary<string, double?> { ["chicken thighs"] = 5.00, ["rice"] = 2.00, ["garlic"] = 1.00 };
        var (total, perServing, ratio) = MealSuggestionService.CostEstimate(Recipe(4, "chicken thighs", "rice", "garlic"), baseline);
        Assert.Equal(8.00, total!.Value, 5);
        Assert.Equal(2.00, perServing!.Value, 5);
        Assert.Equal(1.0, ratio, 5);
    }

    [Fact]
    public void Cost_partial_known()
    {
        var baseline = new Dictionary<string, double?> { ["chicken thighs"] = 5.00 };
        var (total, perServing, ratio) = MealSuggestionService.CostEstimate(Recipe(4, "chicken thighs", "rice", "garlic"), baseline);
        Assert.Equal(5.00, total!.Value, 5);
        Assert.Equal(1.25, perServing!.Value, 5);
        Assert.Equal(1.0 / 3.0, ratio, 5);
    }

    [Fact]
    public void Cost_none_known()
    {
        var (total, perServing, ratio) = MealSuggestionService.CostEstimate(
            Recipe(4, "exotic ingredient", "mystery spice"), new Dictionary<string, double?>());
        Assert.Null(total);
        Assert.Null(perServing);
        Assert.Equal(0.0, ratio, 5);
    }

    [Fact]
    public void Cost_no_ingredients()
    {
        var (total, perServing, ratio) = MealSuggestionService.CostEstimate(
            Recipe(4), new Dictionary<string, double?> { ["rice"] = 2.00 });
        Assert.Null(total);
        Assert.Null(perServing);
        Assert.Equal(0.0, ratio, 5);
    }

    [Fact]
    public void Cost_no_servings_leaves_per_serving_null()
    {
        var baseline = new Dictionary<string, double?> { ["rice"] = 2.00 };
        var (total, perServing, ratio) = MealSuggestionService.CostEstimate(Recipe(null, "rice"), baseline);
        Assert.Equal(2.00, total!.Value, 5);
        Assert.Null(perServing);
        Assert.Equal(1.0, ratio, 5);
    }
}
