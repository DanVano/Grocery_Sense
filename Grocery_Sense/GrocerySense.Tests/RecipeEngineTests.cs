using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

// Port of tests/planning/test_recipe_engine.py + the catalog-integrity half of test_recipes_catalog.py.
// Dropped as Python-only quirks (noted): the str()-coercion of non-string ingredients (C# is typed), and
// the module-singleton delegation tests (C# injects an engine instance — no global).
public sealed class RecipeEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_recipes_{Guid.NewGuid():N}");
    public RecipeEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static readonly string SampleFixture =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "recipes_sample.json");

    private RecipeEngine Sample() => new(SampleFixture);

    private string WriteJson(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- normalization on load ----

    [Fact]
    public void Load_trims_name_and_tags_and_drops_blank_ingredients()
    {
        var eng = new RecipeEngine(WriteJson(
            """[{"id":42,"name":"  Chicken Thighs  ","ingredients":["chicken","","  ","rice"],"tags":["Weeknight","  ","chicken"]}]"""));
        var r = Assert.Single(eng.LoadAllRecipes());
        Assert.Equal(42, r.Id);
        Assert.Equal("Chicken Thighs", r.Name);
        Assert.Equal(new[] { "chicken", "rice" }, r.Ingredients);
        Assert.Equal(new[] { "weeknight", "chicken" }, r.Tags);
    }

    [Fact]
    public void Load_handles_missing_keys()
    {
        var eng = new RecipeEngine(WriteJson("[{}]"));
        var r = Assert.Single(eng.LoadAllRecipes());
        Assert.Null(r.Id);
        Assert.Equal("", r.Name);
        Assert.Empty(r.Ingredients);
        Assert.Empty(r.Tags);
    }

    // ---- loading + caching ----

    [Fact]
    public void Loads_sample_fixture()
    {
        var recipes = Sample().LoadAllRecipes();
        Assert.Equal(8, recipes.Count);
        var names = recipes.Select(r => r.Name).ToHashSet();
        Assert.Superset(new[] { "Chicken Thighs with Rice", "Beef Stir Fry", "Salmon Teriyaki" }.ToHashSet(), names);
    }

    [Fact]
    public void Cache_reloads_when_file_mtime_changes()
    {
        var path = WriteJson("""[{"name":"A","ingredients":["x"]}]""");
        var eng = new RecipeEngine(path);
        Assert.Single(eng.LoadAllRecipes());

        File.WriteAllText(path, """[{"name":"A"},{"name":"B","ingredients":["y"]}]""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(2, eng.LoadAllRecipes().Count);
        Assert.Equal(2, eng.LoadAllRecipes(forceReload: true).Count);
    }

    [Fact]
    public void Missing_file_returns_empty()
    {
        Assert.Empty(new RecipeEngine(Path.Combine(_dir, "nope.json")).LoadAllRecipes());
    }

    [Fact]
    public void Dict_wrapper_shape_accepted()
    {
        var eng = new RecipeEngine(WriteJson("""{"recipes":[{"name":"Wrapped"}]}"""));
        Assert.Equal("Wrapped", Assert.Single(eng.LoadAllRecipes()).Name);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        var eng = new RecipeEngine(WriteJson("\"not a recipe list\""));
        Assert.Throws<InvalidDataException>(() => eng.LoadAllRecipes());
    }

    // ---- ingredient filter ----

    [Fact]
    public void Filter_ranks_by_ingredient_overlap()
    {
        var names = Sample().FilterByIngredientsAndProfile(new[] { "rice" }).Select(r => r.Name).ToHashSet();
        Assert.Contains("Chicken Thighs with Rice", names);
        Assert.Contains("Beef Stir Fry", names);
        Assert.Contains("Salmon Teriyaki", names);
        Assert.DoesNotContain("Veggie Pasta Primavera", names);
    }

    [Fact]
    public void Filter_empty_when_no_matches() =>
        Assert.Empty(Sample().FilterByIngredientsAndProfile(new[] { "unicorn" }));

    [Fact]
    public void Filter_respects_max_results() =>
        Assert.Single(Sample().FilterByIngredientsAndProfile(new[] { "rice" }, maxResults: 1));

    [Fact]
    public void Filter_higher_overlap_ranks_first()
    {
        var results = Sample().FilterByIngredientsAndProfile(new[] { "rice", "garlic" });
        Assert.Equal("Chicken Thighs with Rice", results[0].Name);
    }

    // ---- hard profile filters ----

    [Fact]
    public void Allergy_blocks_recipe()
    {
        var names = Sample().FilterByIngredientsAndProfile(new[] { "rice" },
            new MealProfile { Allergies = ["peanuts"] }).Select(r => r.Name).ToHashSet();
        Assert.Contains("Chicken Thighs with Rice", names);
        Assert.DoesNotContain("Peanut Chicken Noodles", names);
    }

    // Regression (#1): a plural allergy must block a compound ingredient — "peanuts" blocks "peanut butter".
    [Fact]
    public void Plural_allergy_blocks_compound_ingredient()
    {
        var eng = new RecipeEngine(WriteJson(
            """[{"id":1,"name":"Peanut Sauce Bowl","ingredients":["peanut butter","rice"]},{"id":2,"name":"Coconut Rice","ingredients":["coconut","rice"]}]"""));
        var names = eng.FilterByIngredientsAndProfile(new[] { "rice" },
            new MealProfile { Allergies = ["peanuts"] }).Select(r => r.Name).ToHashSet();
        Assert.DoesNotContain("Peanut Sauce Bowl", names);   // "peanuts" -> "peanut" token blocks "peanut butter"
        Assert.Contains("Coconut Rice", names);              // token match must NOT let "peanut" leak into "coconut"
    }

    [Fact]
    public void Avoid_ingredients_blocks_recipe() =>
        Assert.Empty(Sample().FilterByIngredientsAndProfile(new[] { "bread" },
            new MealProfile { AvoidIngredients = ["bread"] }));

    [Fact]
    public void No_pork_restriction() =>
        Assert.Empty(Sample().FilterByIngredientsAndProfile(new[] { "pork" },
            new MealProfile { Restrictions = ["no_pork"] }));

    [Fact]
    public void No_beef_restriction() =>
        Assert.Empty(Sample().FilterByIngredientsAndProfile(new[] { "beef" },
            new MealProfile { Restrictions = ["no_beef"] }));

    // no_meat / no_fish are umbrella diet flags, NOT single-ingredient bans (ProfileFilter carve-out) —
    // they must not hard-drop a recipe just because an ingredient literally contains "fish"/"meat"
    // (e.g. "fish sauce"). Enforced softly via meat-preference scoring, not the hard profile filter.
    [Fact]
    public void No_fish_umbrella_flag_does_not_hard_block_fish_ingredient()
    {
        var eng = new RecipeEngine(WriteJson(
            """[{"id":1,"name":"Fish Sauce Noodles","ingredients":["fish sauce","rice"]}]"""));
        var names = eng.FilterByIngredientsAndProfile(new[] { "rice" },
            new MealProfile { Restrictions = ["no_fish"] }).Select(r => r.Name).ToHashSet();
        Assert.Contains("Fish Sauce Noodles", names);
    }

    // ---- soft bonuses ----

    [Fact]
    public void Prefer_meats_bumps_beef_above_salmon()
    {
        var names = Sample().FilterByIngredientsAndProfile(new[] { "rice" },
            new MealProfile { PreferMeats = ["beef"] }, maxResults: 8).Select(r => r.Name).ToList();
        Assert.True(names.IndexOf("Beef Stir Fry") < names.IndexOf("Salmon Teriyaki"));
    }

    [Fact]
    public void Favorite_tags_bump_weeknight_recipe()
    {
        var names = Sample().FilterByIngredientsAndProfile(new[] { "rice" },
            new MealProfile { FavoriteTags = ["weeknight"] }, maxResults: 8).Select(r => r.Name).ToList();
        Assert.True(names.IndexOf("Beef Stir Fry") < names.IndexOf("Salmon Teriyaki"));
    }

    // ---- get_recipe_by_name ----

    [Fact]
    public void Get_recipe_by_name_is_case_insensitive() =>
        Assert.Equal(1, Sample().GetRecipeByName("CHICKEN thighs WITH rice")!.Id);

    [Fact]
    public void Get_recipe_by_name_unknown_is_null() =>
        Assert.Null(Sample().GetRecipeByName("nonexistent dish"));

    // ---- catalog integrity (the embedded production recipes.json) ----

    [Fact]
    public void Embedded_catalog_loads_at_least_50_recipes() =>
        Assert.True(new RecipeEngine().LoadAllRecipes().Count >= 50);

    [Fact]
    public void Embedded_catalog_ids_are_unique()
    {
        var ids = new RecipeEngine().LoadAllRecipes().Where(r => r.Id is not null).Select(r => r.Id!.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Embedded_catalog_every_recipe_has_required_fields()
    {
        foreach (var r in new RecipeEngine().LoadAllRecipes())
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.NotEmpty(r.Ingredients);
            Assert.True(r.Servings is > 0, $"Recipe {r.Id} missing/invalid servings");
        }
    }
}
