using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/family_requests_service.py — "family picks": a household member
// picks a meal or item, it lands on the shared shopping list attributed to them, and the parent (master)
// reviews after the fact. No approval gate — picks add immediately; a review row is created ONLY for a
// SECONDARY picker (the master adding their own things never self-notifies). Household hard excludes /
// allergies are enforced by PickableRecipes so a kid can't pick an allergen recipe.
public sealed class FamilyRequestsService
{
    private readonly ConfigStore _config;
    private readonly ShoppingListService _shopping;
    private readonly RecipeEngine _engine;
    private readonly PreferencesService _preferences;
    private readonly SqliteConnectionFactory _factory;

    public FamilyRequestsService(ConfigStore config, ShoppingListService shopping, RecipeEngine engine,
        PreferencesService preferences, SqliteConnectionFactory factory)
    {
        _config = config;
        _shopping = shopping;
        _engine = engine;
        _preferences = preferences;
        _factory = factory;
    }

    // Add a recipe's ingredients to the shared list, attributed to the member. Returns the created request
    // (secondary picker) or null (master picker). Throws on an unknown recipe — fail loud, not an empty pick.
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
        var rowIds = recipe.Ingredients
            .Select(ing => _shopping.AddSingleItem(ing, 1.0, "each", notes: $"Family pick: {recipeName}",
                addedBy: name, addedByMemberId: memberId))
            .ToList();

        return CreateRequestIfSecondary(memberId, name, "meal", recipeName, rowIds);
    }

    // Add a single item to the shared list, attributed to the member.
    public MemberRequestRow? PickItem(int memberId, string text, double quantity = 1.0, string unit = "each")
    {
        var label = (text ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("Item text is required.");

        var name = MemberName(memberId);
        var rowId = _shopping.AddSingleItem(label, quantity, string.IsNullOrEmpty(unit) ? "each" : unit,
            notes: "Family pick", addedBy: name, addedByMemberId: memberId);

        return CreateRequestIfSecondary(memberId, name, "item", label, new[] { rowId });
    }

    // Recipe names a member may pick: household hard excludes / allergies are hidden (whole-household, so no
    // per-member arg). Soft excludes do NOT filter. Sorted case-insensitively.
    public IReadOnlyList<string> PickableRecipes() =>
        _engine.RecipesMatchingProfile(_preferences.GetMealProfile())
            .Select(r => r.Name).Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

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

    // Undo a pick: soft-delete the shopping_list rows it created, then mark the request reviewed.
    public void RemoveRequest(int requestId)
    {
        using var conn = _factory.Open();
        var req = MemberRequestsRepo.GetRequest(conn, requestId);
        if (req is null) return;
        foreach (var rowId in req.ItemRowIds) _shopping.SoftDeleteItem(rowId);
        MemberRequestsRepo.MarkReviewed(conn, requestId);
    }

    private MemberRequestRow? CreateRequestIfSecondary(int memberId, string name, string kind, string label,
        IReadOnlyList<int> rowIds)
    {
        if (!_config.IsSecondary(memberId)) return null; // master picks don't create a review row
        using var conn = _factory.Open();
        var reqId = MemberRequestsRepo.AddRequest(conn, memberId, name, kind, label, rowIds);
        return MemberRequestsRepo.GetRequest(conn, reqId);
    }

    private string MemberName(int memberId) => _config.GetMember(memberId)?.Name is { Length: > 0 } n
        ? n : $"Member {memberId}";
}
