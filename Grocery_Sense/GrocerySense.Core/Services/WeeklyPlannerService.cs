namespace GrocerySense.Core;

// Port of reference-python/.../services/weekly_planner_service.py — orchestrates meal suggestion,
// aggregates ingredients, maps them to canonical items, optionally persists to the shopping list.
public sealed class WeeklyPlannerService
{
    public WeeklyPlan BuildWeeklyPlan(int numRecipes = 6, IEnumerable<string>? targetIngredients = null,
        IEnumerable<object>? recentlyUsedRecipeIds = null, bool persistToShoppingList = false,
        int? plannedStoreId = null, string? addedBy = null, bool mapIngredients = true) => throw new NotImplementedException();

    public IReadOnlyList<string> SummarizeWeeklyPlan(WeeklyPlan plan) => throw new NotImplementedException();
}
