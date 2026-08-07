using System.Text.Json;
using System.Text.Json.Serialization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// V3 Phase 3: the confirmed Smart Week plan — write it atomically with the shopping-list upsert, read it
// back validated. The snapshot lives in SQLite (migration 10, grill Q11: atomic with the list write,
// covered by DB backup, config JSON stays a settings file). Item ids inside snapshot_json escape
// MergeItems remapping (FK sweep covers item_id COLUMNS only), so LoadCurrent validates every id and
// falls back through normalized names before anything trusts them.
public sealed class SmartWeekService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly UnitNormalizationService _units = new();

    public SmartWeekService(SqliteConnectionFactory factory) => _factory = factory;

    // ---- read side ----

    public SmartWeekPlanSnapshot? LoadCurrent()
    {
        using var conn = _factory.Open();
        var row = SmartWeekPlanRepo.Get(conn);
        if (row is null) return null;

        SmartWeekPlanSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize(row.Value.SnapshotJson, SmartWeekJsonContext.Default.SmartWeekPlanSnapshot);
        }
        catch (JsonException)
        {
            return null; // corrupt snapshot reads as "no plan" — the UI offers a fresh plan, never a crash
        }
        if (snap is null) return null;

        // Validate item ids (stale after MergeItems); re-resolve dropped ones by exact canonical name.
        var ids = snap.Ingredients.Where(i => i.ItemId is not null).Select(i => i.ItemId!.Value).Distinct().ToList();
        var live = ids.Count > 0 ? ItemsRepo.GetItemsByIds(conn, ids).Keys.ToHashSet() : new HashSet<int>();
        var names = snap.Ingredients.Where(i => i.ItemId is null || !live.Contains(i.ItemId.Value))
            .Select(i => i.Name).ToList();
        var byName = names.Count > 0 ? ItemsRepo.GetItemsByNames(conn, names) : new Dictionary<string, Domain.Item>();

        var fixedIngredients = snap.Ingredients.Select(i =>
        {
            if (i.ItemId is { } id && live.Contains(id)) return i;
            var reResolved = byName.TryGetValue(i.Name.ToLowerInvariant(), out var item) ? item.Id : (int?)null;
            return i with { ItemId = reResolved };
        }).ToList();

        return snap with { Ingredients = fixedIngredients };
    }

    // Variety wiring (V3 finding 9): the previous confirmed plan's recipe ids feed
    // MealSuggestionService.recentlyUsedRecipeIds — the dead variety score finally gets its caller.
    public IReadOnlySet<int> RecentRecipeIds() =>
        LoadCurrent()?.Recipes.Where(r => r.Id is not null).Select(r => r.Id!.Value).ToHashSet()
        ?? (IReadOnlySet<int>)new HashSet<int>();

    // Names (lowercased) of plan ingredients that would MERGE into an existing open list row — the pantry
    // review's "already on list" group. Same matching rules as ConfirmPlan, read-only.
    public IReadOnlySet<string> PreviewExistingMatches(IReadOnlyList<SmartWeekConfirmIngredient> ingredients)
    {
        using var conn = _factory.Open();
        var open = ShoppingListRepo.ListActiveItems(conn, includeCheckedOff: false);
        var itemIds = open.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).ToHashSet();
        var names = open.Select(r => NormName(r.DisplayName)).Where(n => n.Length > 0).ToHashSet();
        return ingredients
            .Where(i => (i.ItemId is { } id && itemIds.Contains(id)) || names.Contains(NormName(i.Name)))
            .Select(i => i.Name.ToLowerInvariant())
            .ToHashSet();
    }

    // ---- confirm (the one write path) ----

    // Upserts the reviewed ingredients into the shopping list and persists the snapshot in ONE
    // transaction (grill Q6 + Q11). Returns per-ingredient outcomes for the confirmation UI.
    public IReadOnlyList<SmartWeekUpsertOutcome> ConfirmPlan(SmartWeekPlanSnapshot snapshot,
        IReadOnlyList<SmartWeekConfirmIngredient> toAdd, string? addedBy = null)
    {
        var outcomes = new List<SmartWeekUpsertOutcome>(toAdd.Count);
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        // Match against OPEN rows only (active, not deleted, not checked-off) — a checked-off row is a
        // completed purchase; a fresh row is correct there (grill Q6: StapleRestock's two keys, NOT its
        // checked-inclusive scope).
        var open = ShoppingListRepo.ListActiveItems(conn, includeCheckedOff: false, tx: tx);
        var byItemId = new Dictionary<int, Domain.ShoppingListRow>();
        foreach (var row in open)
            if (row.ItemId is { } iid && !byItemId.ContainsKey(iid)) byItemId[iid] = row;
        var byName = new Dictionary<string, Domain.ShoppingListRow>(StringComparer.Ordinal);
        foreach (var row in open)
        {
            var key = NormName(row.DisplayName);
            if (key.Length > 0 && !byName.ContainsKey(key)) byName[key] = row;
        }

        foreach (var ing in toAdd)
        {
            var match = ing.ItemId is { } iid && byItemId.TryGetValue(iid, out var mrow) ? mrow : null;
            var nameKey = NormName(ing.Name);
            if (match is null && byName.TryGetValue(nameKey, out var nrow))
            {
                // Name fallback only when a side is unmapped; conflicting non-null ids never merge.
                if (nrow.ItemId is null || ing.ItemId is null || nrow.ItemId == ing.ItemId) match = nrow;
            }

            if (match is null)
            {
                var notes = BuildNotes(ing, prefix: null);
                var rowId = ShoppingListRepo.AddItem(conn, ing.Name, Math.Max(ing.Quantity, 0.001), ing.Unit,
                    notes: notes, addedBy: addedBy ?? "Smart Week", itemId: ing.ItemId, tx: tx);
                outcomes.Add(new SmartWeekUpsertOutcome(ing.Name, "added", rowId));
                continue;
            }

            // Trusted backfill of a NULL item_id (guarded in SQL — a conflicting id is never replaced).
            if (match.ItemId is null && ing.ItemId is { } newId && ing.MatchConfidence is >= 0.9)
                ShoppingListRepo.SetItemIdIfNull(conn, match.Id, newId, tx);

            // Additive quantity merge with QUANTITY-direction unit conversion (Codex Q6: Convert() is a
            // per-unit PRICE factor, so quantities use the opposite direction: g->kg multiplies by
            // Convert(1, kg, g) = 0.001). Incompatible/unknown units leave the row's qty untouched and
            // disclose the extra need in a note instead.
            var rowUnit = _units.NormalizeUnit(match.Unit);
            var ingUnit = _units.NormalizeUnit(ing.Unit);
            double? qtyInRowUnits = rowUnit == ingUnit
                ? ing.Quantity
                : _units.Convert(1.0, rowUnit, ingUnit) is { } f ? ing.Quantity * f : null;

            var mergedNotes = MergeNotes(match.Notes, ing, qtyMerged: qtyInRowUnits is not null);
            var newQty = qtyInRowUnits is { } q ? match.Quantity + q : match.Quantity;
            ShoppingListRepo.UpdateItemDetails(conn, match.Id, newQty, match.Unit, mergedNotes, tx);
            outcomes.Add(new SmartWeekUpsertOutcome(ing.Name,
                qtyInRowUnits is not null ? "merged" : "merged_note_only", match.Id));
        }

        SmartWeekPlanRepo.Save(conn, snapshot.WeekStart, snapshot.ConfirmedAt,
            JsonSerializer.Serialize(snapshot, SmartWeekJsonContext.Default.SmartWeekPlanSnapshot), tx);
        tx.Commit();
        return outcomes;
    }

    public void ClearPlan()
    {
        using var conn = _factory.Open();
        SmartWeekPlanRepo.Clear(conn);
    }

    // ---- helpers ----

    internal static string NormName(string? s) =>
        string.Join(" ", (s ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string BuildNotes(SmartWeekConfirmIngredient ing, string? prefix)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) parts.Add(prefix);
        if (ing.RecipeNames.Count > 0) parts.Add("Used in: " + string.Join(", ", ing.RecipeNames));
        return string.Join(" | ", parts);
    }

    private string MergeNotes(string existing, SmartWeekConfirmIngredient ing, bool qtyMerged)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing)) parts.Add(existing.Trim());
        var usedIn = ing.RecipeNames.Count > 0 ? "Used in: " + string.Join(", ", ing.RecipeNames) : null;
        if (usedIn is not null && !existing.Contains(usedIn, StringComparison.OrdinalIgnoreCase))
            parts.Add(usedIn);
        if (!qtyMerged)
            parts.Add($"plan also needs {ing.Quantity:0.##} {ing.Unit}");
        return string.Join(" | ", parts);
    }
}

// One reviewed ingredient the user confirmed for the list (pantry-review output).
public sealed record SmartWeekConfirmIngredient(
    string Name, double Quantity, string Unit, int? ItemId, double? MatchConfidence,
    IReadOnlyList<string> RecipeNames);

public sealed record SmartWeekUpsertOutcome(string Name, string Action, int RowId);

// ---- persisted snapshot shape (source-gen JSON — Android AOT rule) ----

public sealed record SmartWeekSnapshotRecipe(int? Id, string Name);

public sealed record SmartWeekSnapshotIngredient(
    string Name, double Quantity, string Unit, int? ItemId, IReadOnlyList<string> RecipeNames);

public sealed record SmartWeekPlanSnapshot(
    string WeekStart, string ConfirmedAt, int Servings,
    double? GroceryCap, double? ProteinGoal, bool WholeFoodPreferred,
    IReadOnlyList<SmartWeekSnapshotRecipe> Recipes,
    IReadOnlyList<SmartWeekSnapshotIngredient> Ingredients);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SmartWeekPlanSnapshot))]
internal sealed partial class SmartWeekJsonContext : JsonSerializerContext;
