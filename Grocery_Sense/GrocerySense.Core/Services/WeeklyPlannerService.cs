using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// Port of reference-python/.../services/weekly_planner_service.py — picks a week of recipes via
// MealSuggestionService, aggregates their ingredients (deduped, best-effort item-mapped), and optionally
// writes them to the shopping list. Persist runs in one transaction.
public sealed class WeeklyPlannerService
{
    private readonly MealSuggestionService _meals;
    private readonly IngredientMappingService _mapper;
    private readonly SqliteConnectionFactory _factory;

    public WeeklyPlannerService(MealSuggestionService meals, IngredientMappingService mapper,
        SqliteConnectionFactory factory)
    {
        _meals = meals;
        _mapper = mapper;
        _factory = factory;
    }

    public WeeklyPlan BuildWeeklyPlan(int numRecipes = 6, IEnumerable<string>? targetIngredients = null,
        IReadOnlySet<int>? recentlyUsedRecipeIds = null, bool persistToShoppingList = false,
        int? plannedStoreId = null, string? addedBy = null, bool mapIngredients = true)
    {
        var suggestions = _meals.SuggestMealsForWeek(
            targetIngredients: targetIngredients, maxRecipes: numRecipes,
            recentlyUsedRecipeIds: recentlyUsedRecipeIds);

        var planned = AggregateIngredients(suggestions);

        if (mapIngredients)
        {
            _mapper.InvalidateChoices(); // pick up items added since the last build
            planned = planned.Select(ing =>
            {
                var res = _mapper.MapToItem(ing.Name);
                return res.ItemId is not null
                    ? ing with { ItemId = res.ItemId, CanonicalName = res.CanonicalName,
                                 MatchConfidence = res.Confidence, MatchMethod = res.Method }
                    : ing with { MatchConfidence = res.Confidence, MatchMethod = res.Method };
            }).ToList();
            _mapper.FlushLearnedAliases();
        }

        var plan = new WeeklyPlan(suggestions, planned);
        if (persistToShoppingList) PersistToShoppingList(plan, plannedStoreId, addedBy);
        return plan;
    }

    private void PersistToShoppingList(WeeklyPlan plan, int? plannedStoreId, string? addedBy)
    {
        var by = string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim();
        var rows = plan.PlannedIngredients.Select(ing =>
        {
            var notes = new List<string>();
            if (ing.RecipeNames.Count > 0) notes.Add("Used in: " + string.Join(", ", ing.RecipeNames));
            if (ing.ItemId is not null && ing.MatchConfidence is not null)
            {
                var label = ing.CanonicalName ?? $"item_id={ing.ItemId}";
                notes.Add($"Mapped: {label} ({ing.MatchConfidence.Value.ToString("0.00", CultureInfo.InvariantCulture)}, {ing.MatchMethod})");
            }
            return (
                DisplayName: (ing.Name ?? "").Trim(),
                Quantity: Math.Max(1.0, ing.ApproximateCount),
                Unit: "each",
                Category: "",
                Notes: notes.Count > 0 ? string.Join(" | ", notes) : "",
                AddedBy: by,
                AddedByMemberId: (int?)null,
                PlannedStoreId: plannedStoreId,
                ItemId: ing.ItemId);
        }).ToList();

        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        ShoppingListRepo.BulkAddItems(conn, rows, tx);
        tx.Commit();
    }

    // Dedup ingredients across the week's recipes by normalized name; track the recipes using each; sort by
    // count desc then name. Pure helper (no DB) — unit-tested directly.
    internal static List<PlannedIngredient> AggregateIngredients(IEnumerable<SuggestedMeal> suggestions)
    {
        var agg = new Dictionary<string, (string Display, SortedSet<string> Recipes, int Count)>();

        foreach (var s in suggestions)
        {
            var recipeName = s.Recipe.Name.Length > 0 ? s.Recipe.Name : "Unnamed Recipe";
            foreach (var ing in s.Recipe.Ingredients)
            {
                var norm = NormalizeName(ing);
                if (norm.Length == 0) continue;
                if (!agg.TryGetValue(norm, out var e))
                    e = (ing.Trim(), new SortedSet<string>(StringComparer.Ordinal), 0);
                e.Recipes.Add(recipeName);
                agg[norm] = (e.Display, e.Recipes, e.Count + 1);
            }
        }

        return agg.Values
            .Select(e => new PlannedIngredient(e.Display, e.Recipes.ToList(), e.Count))
            .OrderByDescending(p => p.ApproximateCount)
            .ThenBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    public static List<string> SummarizeWeeklyPlan(WeeklyPlan plan)
    {
        var lines = new List<string> { $"Weekly plan: {plan.Suggestions.Count} recipes" };
        var i = 1;
        foreach (var s in plan.Suggestions)
        {
            var name = s.Recipe.Name.Length > 0 ? s.Recipe.Name : $"Recipe {i}";
            lines.Add($"{i}. {name} (score={s.TotalScore.ToString("0.00", CultureInfo.InvariantCulture)})");
            i++;
        }
        if (plan.PlannedIngredients.Count > 0)
            lines.Add($"Planned ingredients: {plan.PlannedIngredients.Count} unique items");
        return lines;
    }

    private static string NormalizeName(string name) =>
        string.Join(" ", name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
