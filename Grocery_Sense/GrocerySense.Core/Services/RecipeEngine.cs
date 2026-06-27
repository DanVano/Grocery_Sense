namespace GrocerySense.Core;

// Port of reference-python/.../recipes/recipe_engine.py — loads recipes.json (copy that file in as an
// embedded resource / MauiAsset), filters by ingredients + household profile (hard allergy/diet filter
// then ingredient-overlap scoring). Accepts both a bare list and a { "recipes": [...] } wrapper.
public sealed class RecipeEngine
{
    public IReadOnlyList<Dictionary<string, object?>> LoadAllRecipes(bool forceReload = false) => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> FilterRecipesByIngredientsAndProfile(
        IEnumerable<string> includeIngredients, Dictionary<string, object?>? profile = null, int maxResults = 10)
        => throw new NotImplementedException();

    public Dictionary<string, object?>? GetRecipeByName(string name) => throw new NotImplementedException();
}
