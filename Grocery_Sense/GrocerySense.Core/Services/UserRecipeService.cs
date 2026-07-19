using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// CRUD over user_recipes + projection to RecipeEngine's Recipe shape. User recipes shadow same-name
// catalog recipes (handled by RecipeEngine); the only name uniqueness enforced here is within
// user_recipes (UNIQUE COLLATE NOCASE), surfaced as a clear message.
public sealed class UserRecipeService
{
    // Keeps user Recipe.Ids disjoint from the 62-recipe catalog ids (variety score uses id sets).
    internal const int UserRecipeIdOffset = 100_000;

    private readonly SqliteConnectionFactory _factory;

    public UserRecipeService(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<UserRecipeRow> List()
    {
        using var conn = _factory.Open();
        return UserRecipesRepo.ListAll(conn);
    }

    // ponytail: re-reads the table on every engine load — it's tiny; cache only if profiling says so.
    public IReadOnlyList<Recipe> ListAsRecipes() =>
        List().Select(r => new Recipe(
            UserRecipeIdOffset + r.Id,
            r.Name.Trim(),
            r.Servings,
            r.Ingredients.Select(i => i.Trim()).Where(i => i.Length > 0).ToList(),
            r.Steps.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
            r.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList())).ToList();

    public int Add(string name, int? servings, IReadOnlyList<string> ingredients,
        IReadOnlyList<string> steps, IReadOnlyList<string> tags)
    {
        Validate(name, servings, ingredients);
        using var conn = _factory.Open();
        try { return UserRecipesRepo.Add(conn, name, servings, Clean(ingredients), Clean(steps), Clean(tags)); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        { throw new InvalidOperationException($"A recipe named \"{name.Trim()}\" already exists."); }
    }

    public void Update(int id, string name, int? servings, IReadOnlyList<string> ingredients,
        IReadOnlyList<string> steps, IReadOnlyList<string> tags)
    {
        Validate(name, servings, ingredients);
        using var conn = _factory.Open();
        try { UserRecipesRepo.Update(conn, id, name, servings, Clean(ingredients), Clean(steps), Clean(tags)); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        { throw new InvalidOperationException($"A recipe named \"{name.Trim()}\" already exists."); }
    }

    public void Delete(int id)
    {
        using var conn = _factory.Open();
        UserRecipesRepo.Delete(conn, id);
    }

    private static void Validate(string name, int? servings, IReadOnlyList<string> ingredients)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Recipe name is required.");
        if (servings is <= 0) throw new ArgumentException("Servings must be positive when set.");
        if (!ingredients.Any(i => !string.IsNullOrWhiteSpace(i)))
            throw new ArgumentException("At least one ingredient is required.");
    }

    private static List<string> Clean(IReadOnlyList<string> values) =>
        values.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).ToList();
}
