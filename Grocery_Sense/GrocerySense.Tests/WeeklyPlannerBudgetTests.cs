using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public sealed class WeeklyPlannerBudgetTests
{
    private static SuggestedMeal Meal(string name, double score, double? cost, int rid, double knownRatio = 1.0) =>
        new(new Recipe(rid, name, 4, new[] { "x" }, Array.Empty<string>(), Array.Empty<string>()),
            score, 0.5, 0.0, 0.0, 0.0, Array.Empty<string>(), CostTotal: cost,
            CostPerServing: cost / 4, CostKnownRatio: cost is null ? 0.0 : knownRatio);

    [Fact]
    public void Meal_count_beats_one_expensive_high_score_meal()
    {
        // Score-first greedy would pick only A (30) and starve the week.
        var candidates = new[]
        {
            Meal("A", 0.9, 30.0, 1),
            Meal("B", 0.8, 25.0, 2),
            Meal("C", 0.7, 15.0, 3),
            Meal("D", 0.6, 10.0, 4),
        };
        var (picked, over, noCost, total) = WeeklyPlannerService.SelectUnderBudget(candidates, 50.0, maxRecipes: 6);
        Assert.Equal(3, picked.Count); // D(10) + C(15) + B(25) = 50 — three meals, not one
        Assert.Equal(50.0, total, 2);
        Assert.Equal(1, over);   // A didn't fit
        Assert.Equal(0, noCost);
    }

    [Fact]
    public void Swap_pass_lifts_score_when_the_cap_allows()
    {
        // Count pass picks B(10) + C(12) = 22. A(.9, 15) can replace C(.8, 12): 25 <= cap, same count, better score.
        var candidates = new[] { Meal("A", 0.9, 15.0, 1), Meal("B", 0.3, 10.0, 2), Meal("C", 0.8, 12.0, 3) };
        var (picked, _, _, total) = WeeklyPlannerService.SelectUnderBudget(candidates, 25.0, maxRecipes: 6);
        Assert.Equal(2, picked.Count);
        Assert.Contains(picked, p => p.Recipe.Name == "A");
        Assert.Contains(picked, p => p.Recipe.Name == "B");
        Assert.Equal(25.0, total, 2);
    }

    [Fact]
    public void Unpriced_and_partial_estimates_are_excluded_and_counted()
    {
        var candidates = new[]
        {
            Meal("A", 0.9, null, 1),                    // no estimate
            Meal("B", 0.8, 20.0, 2, knownRatio: 0.3),   // partial < 0.5 -> would understate cost
            Meal("C", 0.7, 20.0, 3),                    // qualifies
        };
        var (picked, over, noCost, total) = WeeklyPlannerService.SelectUnderBudget(candidates, 50.0, 6);
        Assert.Equal(new[] { "C" }, picked.Select(p => p.Recipe.Name));
        Assert.Equal(2, noCost);
        Assert.Equal(0, over);
        Assert.Equal(20.0, total, 2);
    }

    [Fact]
    public void Max_recipes_cap_does_not_report_leftovers_as_over_budget()
    {
        var candidates = Enumerable.Range(1, 5).Select(i => Meal($"R{i}", 1.0 - i * 0.1, 5.0, i)).ToArray();
        var (picked, over, _, _) = WeeklyPlannerService.SelectUnderBudget(candidates, 100.0, maxRecipes: 3);
        Assert.Equal(3, picked.Count);
        Assert.Equal(0, over); // the recipe cap stopped us, not the budget — R4/R5 were affordable
    }

    [Fact]
    public void Cap_below_cheapest_estimate_yields_empty_plan()
    {
        var candidates = new[] { Meal("A", 0.9, 30.0, 1) };
        var (picked, over, _, total) = WeeklyPlannerService.SelectUnderBudget(candidates, 10.0, 6);
        Assert.Empty(picked);
        Assert.Equal(1, over);
        Assert.Equal(0.0, total, 2);
    }
}
