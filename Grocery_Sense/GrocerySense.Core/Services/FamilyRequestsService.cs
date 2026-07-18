using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/family_requests_service.py — "family picks": a household member
// picks a meal or item, it lands on the shared shopping list attributed to them, and the parent (master)
// reviews after the fact. No approval gate — picks add immediately; a review row is created ONLY for a
// SECONDARY picker (the master adding their own things never self-notifies). Household hard excludes /
// allergies are enforced by PickableRecipes so a kid can't pick an allergen recipe.
public sealed class FamilyRequestsService
{
    private readonly ConfigStore _config;
    private readonly RecipeEngine _engine;
    private readonly PreferencesService _preferences;
    private readonly IngredientMappingService _mapper;
    private readonly SqliteConnectionFactory _factory;
    private readonly MealSuggestionService? _meals; // null => PickableRecipesRanked falls back alphabetical

    public FamilyRequestsService(ConfigStore config, RecipeEngine engine,
        PreferencesService preferences, IngredientMappingService mapper, SqliteConnectionFactory factory,
        MealSuggestionService? meals = null)
    {
        _config = config;
        _engine = engine;
        _preferences = preferences;
        _mapper = mapper;
        _factory = factory;
        _meals = meals;
    }

    // Add a recipe's ingredients to the shared list, attributed to the member. Returns the created request
    // (secondary picker) or null (master picker). Throws on an unknown recipe — fail loud, not an empty pick.
    // Ingredient rows + the review request commit in ONE transaction: a mid-pick failure must not leave a
    // partial meal on the list (or orphaned rows with no request to undo them from).
    public MemberRequestRow? PickMeal(int memberId, string recipeName)
    {
        var recipe = _engine.GetRecipeByName(recipeName)
            ?? throw new ArgumentException($"Unknown recipe: {recipeName}");

        // Defense-in-depth: re-check the household hard filter at pick time. PickableRecipes filters the list,
        // but a stale list (e.g. an allergy added while a kid had /family open) could still surface a now-unsafe
        // recipe — refuse rather than add an allergen to the shared list. Mirrors MealSuggestionService's net.
        if (ProfileFilter.Violates(recipe.Ingredients, _preferences.GetMealProfile()))
            throw new InvalidOperationException(
                $"\"{recipe.Name}\" is no longer allowed by the household's allergy/exclude settings.");

        var name = MemberName(memberId);
        // Map before the write transaction (match-only; the mapper opens its own connection).
        var mapped = recipe.Ingredients.Select(ing => (Name: ing, ItemId: _mapper.MapToItem(ing).ItemId)).ToList();

        MemberRequestRow? request;
        using (var conn = _factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var rowIds = mapped
                .Select(m => ShoppingListRepo.AddItem(conn, m.Name, 1.0, "each", notes: $"Family pick: {recipeName}",
                    addedBy: name, addedByMemberId: memberId, itemId: m.ItemId, tx: tx))
                .ToList();
            request = CreateRequestIfSecondary(conn, tx, memberId, name, "meal", recipeName, rowIds);
            tx.Commit();
        }
        _mapper.FlushLearnedAliases(); // after commit — the flush opens its own write connection
        return request;
    }

    // Add a single item to the shared list, attributed to the member.
    public MemberRequestRow? PickItem(int memberId, string text, double quantity = 1.0, string unit = "each")
    {
        var label = (text ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("Item text is required.");

        var name = MemberName(memberId);
        var itemId = _mapper.MapToItem(label).ItemId;

        MemberRequestRow? request;
        using (var conn = _factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var rowId = ShoppingListRepo.AddItem(conn, label, quantity, string.IsNullOrEmpty(unit) ? "each" : unit,
                notes: "Family pick", addedBy: name, addedByMemberId: memberId, itemId: itemId, tx: tx);
            request = CreateRequestIfSecondary(conn, tx, memberId, name, "item", label, new[] { rowId });
            tx.Commit();
        }
        _mapper.FlushLearnedAliases();
        return request;
    }

    // Recipe names a member may pick: household hard excludes / allergies are hidden (whole-household, so no
    // per-member arg). Soft excludes do NOT filter. Sorted case-insensitively.
    public IReadOnlyList<string> PickableRecipes() =>
        _engine.RecipesMatchingProfile(_preferences.GetMealProfile())
            .Select(r => r.Name).Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // Pickable recipes ranked by the meal-suggestion value score, so kids choose from what's cheap
    // this week. Both lists filter through the same ProfileFilter, so every pickable name gets a score.
    // No MealSuggestionService (older tests) -> alphabetical, unflagged — same behavior as before.
    public IReadOnlyList<PickableRecipe> PickableRecipesRanked()
    {
        var names = PickableRecipes();
        if (_meals is null || names.Count == 0)
            return names.Select(n => new PickableRecipe(n, false)).ToList();

        var scored = _meals.SuggestMealsForWeek(_preferences.GetMealProfile(), maxRecipes: int.MaxValue);
        var byName = scored.ToDictionary(s => s.Recipe.Name, StringComparer.OrdinalIgnoreCase);
        return names
            .OrderByDescending(n => byName.TryGetValue(n, out var s) ? s.TotalScore : double.MinValue)
            .Select(n => new PickableRecipe(n,
                byName.TryGetValue(n, out var s) && s.DealScore > 0.2)) // 0.2 = FormatMealExplanation's on-sale bar
            .ToList();
    }

    // --- parent review queue ---

    public int UnreviewedCount()
    {
        using var conn = _factory.Open();
        return MemberRequestsRepo.CountUnreviewed(conn);
    }

    public IReadOnlyList<MemberRequestRow> ListUnreviewed()
    {
        using var conn = _factory.Open();
        return MemberRequestsRepo.ListUnreviewed(conn);
    }

    public void MarkReviewed(int requestId)
    {
        using var conn = _factory.Open();
        MemberRequestsRepo.MarkReviewed(conn, requestId);
    }

    // Undo a pick: soft-delete the shopping_list rows it created + mark reviewed, in one transaction.
    public void RemoveRequest(int requestId)
    {
        using var conn = _factory.Open();
        var req = MemberRequestsRepo.GetRequest(conn, requestId);
        if (req is null) return;
        using var tx = conn.BeginTransaction();
        foreach (var rowId in req.ItemRowIds) ShoppingListRepo.DeleteItem(conn, rowId, tx);
        MemberRequestsRepo.MarkReviewed(conn, requestId, tx);
        tx.Commit();
    }

    private MemberRequestRow? CreateRequestIfSecondary(SqliteConnection conn, SqliteTransaction tx,
        int memberId, string name, string kind, string label, IReadOnlyList<int> rowIds)
    {
        if (!_config.IsSecondary(memberId)) return null; // master picks don't create a review row
        var reqId = MemberRequestsRepo.AddRequest(conn, memberId, name, kind, label, rowIds, tx);
        return MemberRequestsRepo.GetRequest(conn, reqId, tx);
    }

    private string MemberName(int memberId) => _config.GetMember(memberId)?.Name is { Length: > 0 } n
        ? n : $"Member {memberId}";
}
