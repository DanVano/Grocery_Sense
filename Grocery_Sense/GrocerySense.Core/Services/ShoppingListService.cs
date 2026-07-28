using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/shopping_list_service.py — thin wrapper over ShoppingListRepo.
// Opens its own connection per call via the factory (mirrors Python's module-level repo calls).
public sealed class ShoppingListService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;

    public ShoppingListService(SqliteConnectionFactory factory, IngredientMappingService mapper)
    {
        _factory = factory;
        _mapper = mapper;
    }

    // Split a comma-separated string into items, adding each (blank entries skipped). Returns the new rows.
    // Each name is mapped to a canonical item (match-only — typos stay unmapped, never force-created) so
    // manual adds reach the optimizer/Shop Mode intel; the user's typed text stays as the display name.
    public IReadOnlyList<ShoppingListRow> AddItemsFromText(string text, int? plannedStoreId = null,
        string? addedBy = null, int? memberId = null)
    {
        var created = new List<ShoppingListRow>();
        using (var conn = _factory.Open())
        {
            foreach (var raw in (text ?? "").Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                var rowId = ShoppingListRepo.AddItem(conn, name, addedBy: addedBy, addedByMemberId: memberId,
                    itemId: _mapper.MapToItem(conn, name).ItemId);
                if (plannedStoreId is not null) ShoppingListRepo.SetPlannedStoreId(conn, rowId, plannedStoreId);
                if (ShoppingListRepo.GetItem(conn, rowId) is { } match) created.Add(match);
            }
        }
        _mapper.FlushLearnedAliases();
        return created;
    }

    public IReadOnlyList<ShoppingListRow> GetActiveItems(int? storeId = null, bool includeCheckedOff = false)
    {
        using var conn = _factory.Open();
        return ShoppingListRepo.ListActiveItems(conn, storeId, includeCheckedOff);
    }

    public int AddSingleItem(string name, double? quantity = null, string unit = "", int? plannedStoreId = null,
        string? notes = null, string? addedBy = null, int? addedByMemberId = null, int? itemId = null)
    {
        // No explicit item link -> try to map the name (match-only; unknowns stay NULL and the optimizer
        // discloses them instead of silently dropping the row). Map on the same connection as the insert.
        int rowId;
        using (var conn = _factory.Open())
        {
            itemId ??= _mapper.MapToItem(conn, name).ItemId;
            rowId = ShoppingListRepo.AddItem(conn, name, quantity ?? 1.0, unit ?? "", category: "", notes: notes ?? "",
                addedBy: addedBy, addedByMemberId: addedByMemberId, plannedStoreId: plannedStoreId, itemId: itemId);
        }
        _mapper.FlushLearnedAliases();
        return rowId;
    }

    // Add a flyer deal to the active list. A mapped deal (ItemId set + item still exists) resolves to the
    // canonical item name so the row joins Shop Mode price intel and the optimizer; an unmapped deal (or one
    // whose item was deleted) lands as a plain reminder line, disclosed in notes — we never force-create an
    // item from flyer text, and never link a shopping row to a missing item_id (FK safety). Quantity is always
    // 1: flyer promo phrases ("2/$5") don't carry a reliable buy quantity (MultiBuyDealService parses effective
    // price, not offer count). Returns the new row id.
    public int AddDealToList(FlyerDeal deal)
    {
        using var conn = _factory.Open();

        string? canonical = null;
        if (deal.ItemId is { } itemId && ItemsRepo.GetItemsByIds(conn, [itemId]).TryGetValue(itemId, out var item))
            canonical = item.CanonicalName;

        var mapped = canonical is not null;
        var name = mapped ? canonical! : DealTitle(deal);
        var note = mapped ? DealNote(deal) : $"{DealNote(deal)} · not price-tracked";
        var itemLink = mapped ? deal.ItemId : null;   // drop the link when the item didn't resolve

        return ShoppingListRepo.AddItem(conn, name, quantity: 1.0, unit: deal.Unit ?? "", category: "",
            notes: note, plannedStoreId: deal.StoreId, itemId: itemLink);
    }

    // Add a price-drop / stock-up alert's item to the active list. The alert was just computed from the current
    // items/prices join, so ItemName/ItemId/StoreId are all live — no re-lookup needed. Carries the suggested
    // stock-up quantity + cadence note when present; SuggestedQty absent => 1. Returns the new row id.
    public int AddAlertToList(PriceDropAlert alert)
    {
        using var conn = _factory.Open();
        return ShoppingListRepo.AddItem(conn, alert.ItemName, quantity: alert.SuggestedQty ?? 1.0,
            unit: "", category: "", notes: alert.SuggestedQtyNote ?? "From price alert",
            plannedStoreId: alert.StoreId, itemId: alert.ItemId);
    }

    // Add a watchlist hit to the active list (F04) — mirrors AddAlertToList: the hit was just computed
    // from the live items/prices join, so ItemName/ItemId/StoreId need no re-lookup. The row lands mapped,
    // planned at the hit's store, with the hit price disclosed in the note. Returns the new row id.
    public int AddWatchHitToList(WatchlistHit hit)
    {
        using var conn = _factory.Open();
        var price = hit.BestPrice.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return ShoppingListRepo.AddItem(conn, hit.ItemName, quantity: 1.0, unit: "", category: "",
            notes: $"Watch hit: ${price} at {hit.StoreName} ({hit.Source})",
            plannedStoreId: hit.StoreId, itemId: hit.ItemId);
    }

    private static string DealTitle(FlyerDeal d) =>
        d.Title is { Length: > 0 } t ? t : d.Description is { Length: > 0 } de ? de : "(deal)";

    private static string DealNote(FlyerDeal d) =>
        d.PriceText is { Length: > 0 } pt ? $"From deal: {pt}"
        : d.UnitPrice is decimal up
            ? $"From deal: ${up.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}{(d.Unit is { Length: > 0 } u ? $"/{u}" : "")}"
            : "From deal";

    // Edit a row's quantity/unit/notes after the fact (F05).
    public void UpdateItemDetails(int rowId, double quantity, string unit, string notes)
    {
        using var conn = _factory.Open();
        ShoppingListRepo.UpdateItemDetails(conn, rowId, quantity, unit, notes);
    }

    // Plain-text export of the active list (F07): grouped by planned store (unplanned last), rows
    // alphabetical, checked-off marked — deterministic so a share always reads the same way. For the OS
    // share sheet; a family member without the app shops from it.
    public string FormatListAsText()
    {
        using var conn = _factory.Open();
        var rows = ShoppingListRepo.ListActiveItems(conn, includeCheckedOff: true);
        if (rows.Count == 0) return "Shopping list is empty.";
        var storeNames = StoresRepo.ListStores(conn, includeArchived: true).ToDictionary(s => s.Id, s => s.Name);
        string StoreLabel(int? id) => id is { } s ? storeNames.GetValueOrDefault(s, $"Store #{s}") : "Any store";

        var sb = new System.Text.StringBuilder("Shopping list");
        foreach (var group in rows
            .GroupBy(r => r.PlannedStoreId)
            .OrderBy(g => g.Key is null ? 1 : 0) // planned stores first, "Any store" last
            .ThenBy(g => StoreLabel(g.Key), StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('\n').Append('\n').Append(StoreLabel(group.Key)).Append(':');
            foreach (var r in group.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var qty = r.Quantity == 1.0 && r.Unit.Length == 0
                    ? ""
                    : $" — {r.Quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                      (r.Unit.Length > 0 ? $" {r.Unit}" : "");
                sb.Append('\n').Append(r.IsCheckedOff ? "[x] " : "[ ] ").Append(r.DisplayName).Append(qty);
            }
        }
        return sb.ToString();
    }

    public void SoftDeleteItem(int itemId)
    {
        using var conn = _factory.Open();
        ShoppingListRepo.DeleteItem(conn, itemId);
    }

    public void CheckOffItem(int itemId, bool checkedOff = true)
    {
        using var conn = _factory.Open();
        ShoppingListRepo.SetCheckedOff(conn, itemId, checkedOff);
    }

    // priority: must_have | normal | wait_for_sale. The optimizer leaves wait_for_sale items unplanned
    // unless they're currently on sale (see BasketOptimizerService).
    public void SetItemPriority(int rowId, string priority)
    {
        using var conn = _factory.Open();
        ShoppingListRepo.SetPriority(conn, rowId, priority);
    }

    public void ClearAllCheckedOff()
    {
        using var conn = _factory.Open();
        ShoppingListRepo.ClearCheckedOffItems(conn);
    }

    // Writes optimizer-chosen planned_store_id back onto the active list rows, keyed by item_id. Hard-excluded
    // lines are left unassigned (planned_store_id = NULL) so they stand out as unplanned. The clear + write run
    // in ONE transaction (rollback on failure -> no partial assignment).
    public ApplyPlanResult ApplyOptimizerPlanToActiveList(BasketOptimizationResult result, string mode = "fast",
        bool clearFirst = true)
    {
        var modeKey = (mode ?? "fast").Trim().ToLowerInvariant();
        if (result.Stores.Count == 0)
            return new ApplyPlanResult(false, modeKey, null, 0, 0, 0, 0, 0, 0, Array.Empty<string>(),
                "No plan available to apply.");

        var planLabel = result.Mode == "fewest_stops" ? "Fast trip (one store)" : "Savings (multiple stores)";

        var assignments = new List<(int ItemId, int? StoreId)>();
        var unassignedHard = 0;
        foreach (var sp in result.Stores)
            foreach (var ip in sp.Items)
            {
                if (ip.HardExcluded) unassignedHard++;
                assignments.Add((ip.ItemId, ip.HardExcluded ? null : sp.StoreId));
            }

        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        var cleared = clearFirst ? ShoppingListRepo.ClearPlannedStoreIdsForActiveItems(conn, false, tx) : 0;
        var updated = ShoppingListRepo.BulkSetPlannedStoreIdsByItemId(conn, assignments, activeOnly: true, tx);
        tx.Commit();

        var warnings = result.Warnings.ToList();
        if (unassignedHard > 0)
        {
            var warning = $"{unassignedHard} item(s) were hard-excluded by household preferences and were left unplanned.";
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }

        return new ApplyPlanResult(true, modeKey, planLabel, cleared, assignments.Count, updated,
            assignments.Count(a => a.StoreId is not null), assignments.Count(a => a.StoreId is null),
            unassignedHard, warnings, null);
    }
}
