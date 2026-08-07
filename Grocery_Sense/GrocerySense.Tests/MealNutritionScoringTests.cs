using GrocerySense.Core;

namespace GrocerySense.Tests;

// V3 Smart Week scoring gates: nutrition bonuses are soft and additive (grill Q4 — never a hard filter,
// zero when the preferences are off), protein-weight magnitudes finally matter (finding 10), and safety
// (allergies) always outranks nutrition.
public sealed class MealNutritionScoringTests
{
    private static Recipe Rated(string name, double protein, params string[] classes) => new(
        1, name, 4, classes.Select((_, i) => $"ing{i}").ToList(), [], [],
        new RecipeDetails(
            classes.Select((c, i) => new RecipeIngredientDetail($"ing{i}", 100, "g", c)).ToList(),
            protein, ["ing0"], "curated"));

    private static readonly Recipe Unrated = new(2, "Unrated", 4, ["mystery"], [], []);

    [Fact]
    public void Bonus_is_zero_when_preferences_are_off()
    {
        var r = Rated("A", 40, "whole", "whole", "whole");
        Assert.Equal(0.0, MealSuggestionService.NutritionBonus(r, new MealProfile()));
    }

    [Fact]
    public void Protein_goal_met_beats_missed_soft_not_hard()
    {
        var profile = new MealProfile { ProteinPerServingGoal = 25.0 };
        var high = MealSuggestionService.NutritionBonus(Rated("High", 30, "whole"), profile);
        var low = MealSuggestionService.NutritionBonus(Rated("Low", 15, "whole"), profile);
        Assert.Equal(0.25, high);
        Assert.Equal(0.0, low); // ranked lower, never excluded
    }

    [Fact]
    public void Unrated_recipes_get_no_bonus_and_no_penalty()
    {
        var profile = new MealProfile { ProteinPerServingGoal = 25.0, PreferWholeFoodForward = true };
        Assert.Equal(0.0, MealSuggestionService.NutritionBonus(Unrated, profile));
    }

    [Fact]
    public void Whole_food_rollup_tiers_score_descending()
    {
        var profile = new MealProfile { PreferWholeFoodForward = true };
        var whole = MealSuggestionService.NutritionBonus(
            Rated("W", 10, "whole", "whole", "whole", "whole", "whole", "whole", "whole", "processed"), profile);
        var mixed = MealSuggestionService.NutritionBonus(
            Rated("M", 10, "whole", "whole", "whole", "processed", "processed"), profile);
        var processed = MealSuggestionService.NutritionBonus(
            Rated("P", 10, "processed", "processed", "whole"), profile);
        Assert.Equal(0.15, whole);
        Assert.Equal(0.05, mixed);
        Assert.Equal(0.0, processed);
    }

    [Fact]
    public void Protein_goal_outranks_whole_food_preference()
    {
        var profile = new MealProfile { ProteinPerServingGoal = 25.0, PreferWholeFoodForward = true };
        var proteinOnly = MealSuggestionService.NutritionBonus(
            Rated("P", 30, "processed", "processed", "processed"), profile);
        var wholeOnly = MealSuggestionService.NutritionBonus(
            Rated("W", 10, "whole", "whole", "whole"), profile);
        Assert.True(proteinOnly > wholeOnly); // 0.25 > 0.15 — the plan's constraint order
    }

    [Fact]
    public void Prefer_meat_weight_magnitudes_scale_the_bonus()
    {
        var beefRecipe = new Recipe(3, "Beef", 4, ["beef sirloin", "rice"], [], []);
        double Score(double weight) => MealSuggestionService.PreferenceScore(beefRecipe, new MealProfile
        {
            PreferMeats = ["beef"],
            PreferMeatWeights = new Dictionary<string, double> { ["beef"] = weight },
        });
        var strong = Score(2.0);
        var mild = Score(1.2);
        Assert.True(strong > mild); // magnitudes matter now (previously collapsed to a boolean)

        // Legacy profiles without weights keep the flat +0.3 behavior.
        var flat = MealSuggestionService.PreferenceScore(beefRecipe, new MealProfile { PreferMeats = ["beef"] });
        Assert.Equal(strong, flat, 6);
    }

    [Fact]
    public void Allergies_still_outrank_every_nutrition_bonus()
    {
        // A rated, goal-meeting, mostly-whole recipe containing an allergen must not appear at all.
        var engine = new RecipeEngine(Fixtures.RecipesSamplePath);
        var profile = new MealProfile
        {
            Allergies = ["peanuts"], ProteinPerServingGoal = 1.0, PreferWholeFoodForward = true,
        };
        var names = engine.RecipesMatchingProfile(profile).Select(r => r.Name);
        Assert.DoesNotContain("Peanut Chicken Noodles", names);
    }
}
