namespace GrocerySense.Core;

// Port of reference-python/.../services/meal_suggestion_service.py — combines RecipeEngine filtering,
// preference scoring, deal matching, price history, and variety scoring into ranked meals.
public sealed class MealSuggestionService
{
    public IReadOnlyList<SuggestedMeal> SuggestMealsForWeek(Dictionary<string, object?>? profile = null,
        IEnumerable<string>? targetIngredients = null, int maxRecipes = 6,
        IEnumerable<object>? recentlyUsedRecipeIds = null) => throw new NotImplementedException();

    public string ExplainSuggestedMeal(SuggestedMeal meal) => throw new NotImplementedException();
}
