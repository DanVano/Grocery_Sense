using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/shopping_list_service.py — thin wrapper over ShoppingListRepo.
// Opens its own connection per call via the factory (mirrors Python's module-level repo calls).
public sealed class ShoppingListService
{
    private readonly SqliteConnectionFactory _factory;

    public ShoppingListService(SqliteConnectionFactory factory) => _factory = factory;

    // Split a comma-separated string into items, adding each (blank entries skipped). Returns the new rows.
    public IReadOnlyList<ShoppingListRow> AddItemsFromText(string text, int? plannedStoreId = null,
        string? addedBy = null, int? memberId = null)
    {
        using var conn = _factory.Open();
        var created = new List<ShoppingListRow>();
        foreach (var raw in (text ?? "").Split(','))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var rowId = ShoppingListRepo.AddItem(conn, name, addedBy: addedBy, addedByMemberId: memberId);
            if (plannedStoreId is not null) ShoppingListRepo.SetPlannedStoreId(conn, rowId, plannedStoreId);
            if (ShoppingListRepo.GetItem(conn, rowId) is { } match) created.Add(match);
        }
        return created;
    }

    public IReadOnlyList<ShoppingListRow> GetActiveItems(int? storeId = null, bool includeCheckedOff = false)
    {
        using var conn = _factory.Open();
        return ShoppingListRepo.ListActiveItems(conn, storeId, includeCheckedOff);
    }

    // autoMap is accepted for signature parity but unused (Python add_single_item ignores it too).
    public int AddSingleItem(string name, double? quantity = null, string unit = "", int? plannedStoreId = null,
        string? notes = null, string? addedBy = null, int? addedByMemberId = null, int? itemId = null,
        bool autoMap = false)
    {
        using var conn = _factory.Open();
        return ShoppingListRepo.AddItem(conn, name, quantity ?? 1.0, unit ?? "", category: "", notes: notes ?? "",
            addedBy: addedBy, addedByMemberId: addedByMemberId, plannedStoreId: plannedStoreId, itemId: itemId);
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
