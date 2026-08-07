using GrocerySense.Core;

namespace GrocerySense.Tests;

// V3 Phase 1 gates: the embedded catalog's curated details (structured quantities, protein, whole-food
// classes) are held to a strict bar here — the runtime parser is deliberately tolerant (user/legacy JSON),
// so THIS is where bad curated data fails the build. Also guards the snake_case JSON binding regression
// (camelCase context policy would silently null the details fields) and the dietary-consistency rule that
// previously let five vegetarian recipes carry chicken broth.
public sealed class RecipeCatalogValidationTests : TempDirTestBase
{
    private static readonly IReadOnlyList<Recipe> Catalog = new RecipeEngine().LoadAllRecipes();

    private static readonly HashSet<string> AllowedUnits = ["g", "ml", "each", "tbsp", "tsp", "clove"];
    private static readonly HashSet<string> AllowedClasses =
        [RecipeDetails.ClassWhole, RecipeDetails.ClassProcessed, RecipeDetails.ClassUltraProcessed];

    // Meat/fish terms a vegetarian recipe must never contain (word-boundary match per ingredient).
    private static readonly string[] MeatTerms =
        ["chicken", "beef", "pork", "bacon", "turkey", "lamb", "ham", "sausage", "salmon", "tuna",
         "tilapia", "shrimp", "clam", "clams", "anchovy", "oyster", "fish", "gelatin"];
    // Additional animal-product terms a VEGAN recipe must never contain. "coconut milk" is exempted below.
    private static readonly string[] AnimalTerms =
        ["cheese", "milk", "butter", "cream", "yogurt", "egg", "eggs", "honey", "mayonnaise"];

    private static bool ContainsTerm(string ingredient, string term)
    {
        if (ingredient == "coconut milk" && term == "milk") return false; // plant-based, vegan-safe
        var words = ingredient.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Contains(term);
    }

    [Fact]
    public void Catalog_has_62_recipes_with_unique_names()
    {
        Assert.Equal(62, Catalog.Count);
        Assert.Equal(62, Catalog.Select(r => r.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Every_catalog_recipe_has_curated_details()
    {
        foreach (var r in Catalog)
        {
            Assert.True(r.Details is not null, $"'{r.Name}' has no details block");
            Assert.Equal("curated", r.Details!.Provenance);
        }
    }

    [Fact]
    public void Structured_ingredients_match_the_flat_ingredient_list()
    {
        foreach (var r in Catalog)
        {
            var flat = r.Ingredients.Select(i => i.ToLowerInvariant()).ToHashSet();
            var structured = r.Details!.StructuredIngredients.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
            Assert.True(flat.SetEquals(structured),
                $"'{r.Name}' flat vs structured mismatch: [{string.Join(", ", flat.Except(structured))}] " +
                $"vs [{string.Join(", ", structured.Except(flat))}]");
        }
    }

    [Fact]
    public void Quantities_positive_units_and_classes_recognized()
    {
        foreach (var r in Catalog)
            foreach (var i in r.Details!.StructuredIngredients)
            {
                Assert.True(i.Quantity > 0, $"'{r.Name}' / '{i.Name}': non-positive quantity");
                Assert.True(AllowedUnits.Contains(i.Unit), $"'{r.Name}' / '{i.Name}': unknown unit '{i.Unit}'");
                Assert.True(AllowedClasses.Contains(i.Class), $"'{r.Name}' / '{i.Name}': unknown class '{i.Class}'");
            }
    }

    [Fact]
    public void Protein_present_and_in_plausible_range()
    {
        foreach (var r in Catalog)
        {
            var p = r.Details!.ProteinGPerServing;
            Assert.True(p is > 0 and <= 80, $"'{r.Name}' protein/serving {p?.ToString() ?? "null"} out of (0, 80]");
        }
    }

    [Fact]
    public void Protein_sources_are_actual_ingredients()
    {
        foreach (var r in Catalog)
        {
            var names = r.Details!.StructuredIngredients.Select(i => i.Name.ToLowerInvariant()).ToHashSet();
            foreach (var s in r.Details.ProteinSources)
                Assert.True(names.Contains(s.ToLowerInvariant()), $"'{r.Name}' protein source '{s}' not an ingredient");
            // A meaningfully-protein recipe must say where the protein comes from.
            if (r.Details.ProteinGPerServing is >= 10)
                Assert.True(r.Details.ProteinSources.Count > 0, $"'{r.Name}' has protein but no sources");
        }
    }

    [Fact]
    public void Vegetarian_recipes_contain_no_meat_or_fish()
    {
        foreach (var r in Catalog.Where(r => r.Tags.Contains("vegetarian") || r.Tags.Contains("vegan")))
            foreach (var ing in r.Ingredients)
                foreach (var term in MeatTerms)
                    Assert.False(ContainsTerm(ing, term),
                        $"'{r.Name}' is tagged vegetarian/vegan but contains '{ing}'");
    }

    [Fact]
    public void Vegan_recipes_contain_no_animal_products()
    {
        foreach (var r in Catalog.Where(r => r.Tags.Contains("vegan")))
            foreach (var ing in r.Ingredients)
                foreach (var term in AnimalTerms)
                    Assert.False(ContainsTerm(ing, term),
                        $"'{r.Name}' is tagged vegan but contains '{ing}'");
    }

    // ---- whole-food rollup rubric (grill Q5: by count, >=70% whole / >=50% processed) ----

    private static RecipeIngredientDetail Ing(string cls) => new("x", 1, "g", cls);

    [Fact]
    public void Rollup_70_pct_whole_is_mostly_whole()
    {
        var d = new RecipeDetails(
            Enumerable.Repeat(Ing("whole"), 7).Concat(Enumerable.Repeat(Ing("processed"), 3)).ToList(),
            null, [], "curated");
        Assert.Equal(RecipeDetails.MostlyWhole, d.WholeFoodRollup().Class);
    }

    [Fact]
    public void Rollup_half_processed_is_mostly_processed()
    {
        var d = new RecipeDetails(
            Enumerable.Repeat(Ing("whole"), 5).Concat(Enumerable.Repeat(Ing("ultra_processed"), 5)).ToList(),
            null, [], "curated");
        Assert.Equal(RecipeDetails.MostlyProcessed, d.WholeFoodRollup().Class);
    }

    [Fact]
    public void Rollup_between_thresholds_is_mixed()
    {
        var d = new RecipeDetails(
            Enumerable.Repeat(Ing("whole"), 6).Concat(Enumerable.Repeat(Ing("processed"), 4)).ToList(),
            null, [], "curated");
        var (cls, whole, total) = d.WholeFoodRollup();
        Assert.Equal(RecipeDetails.Mixed, cls);
        Assert.Equal(6, whole);
        Assert.Equal(10, total);
    }

    [Fact]
    public void Rollup_empty_is_mixed() =>
        Assert.Equal(RecipeDetails.Mixed, new RecipeDetails([], null, [], "curated").WholeFoodRollup().Class);

    // ---- parsing: snake_case binding + legacy tolerance ----

    private string WriteJson(string content) => WriteFile(content, ".json");

    // THE regression the grill flagged (Q2): the context's camelCase policy would silently null-out
    // snake_case keys without explicit [JsonPropertyName] binding. If this fails, details are being
    // dropped on load and every nutrition surface shows "not rated".
    [Fact]
    public void Details_snake_case_keys_bind()
    {
        var eng = new RecipeEngine(WriteJson(
            """
            [{"id":1,"name":"Bound","servings":2,"ingredients":["chicken breast"],
              "details":{"structured_ingredients":[{"name":"chicken breast","quantity":300,"unit":"g","class":"whole"}],
                         "protein_g_per_serving":34.5,"protein_sources":["chicken breast"],"provenance":"curated"}}]
            """));
        var d = Assert.Single(eng.LoadAllRecipes()).Details;
        Assert.NotNull(d);
        var i = Assert.Single(d!.StructuredIngredients);
        Assert.Equal(("chicken breast", 300.0, "g", "whole"), (i.Name, i.Quantity, i.Unit, i.Class));
        Assert.Equal(34.5, d.ProteinGPerServing);
        Assert.Equal(["chicken breast"], d.ProteinSources);
        Assert.Equal("curated", d.Provenance);
    }

    [Fact]
    public void Legacy_recipe_without_details_parses_with_null_details()
    {
        var eng = new RecipeEngine(WriteJson("""[{"id":1,"name":"Old","ingredients":["rice"]}]"""));
        Assert.Null(Assert.Single(eng.LoadAllRecipes()).Details);
    }

    [Fact]
    public void Malformed_detail_entries_are_dropped_not_fatal()
    {
        var eng = new RecipeEngine(WriteJson(
            """
            [{"id":1,"name":"Messy","ingredients":["rice"],
              "details":{"structured_ingredients":[{"name":"","quantity":1,"unit":"g","class":"whole"},
                                                   {"name":"rice","quantity":-5,"unit":"g","class":"whole"},
                                                   {"name":"rice","quantity":100,"unit":"g","class":"whole"}],
                         "protein_g_per_serving":4,"provenance":"curated"}}]
            """));
        var d = Assert.Single(eng.LoadAllRecipes()).Details;
        Assert.NotNull(d);
        Assert.Single(d!.StructuredIngredients); // only the valid row survives
    }

    [Fact]
    public void Empty_details_block_becomes_null()
    {
        var eng = new RecipeEngine(WriteJson("""[{"id":1,"name":"Empty","ingredients":["rice"],"details":{}}]"""));
        Assert.Null(Assert.Single(eng.LoadAllRecipes()).Details);
    }
}
