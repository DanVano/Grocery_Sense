using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/shopping_list_service.py
public sealed class ShoppingListService
{
    public IReadOnlyList<ShoppingListRow> AddItemsFromText(string text, int? plannedStoreId = null,
        string? addedBy = null, int? memberId = null) => throw new NotImplementedException();

    public IReadOnlyList<ShoppingListRow> GetActiveItems(int? storeId = null, bool includeCheckedOff = false)
        => throw new NotImplementedException();

    public int AddSingleItem(string name, double? quantity = null, string unit = "", int? plannedStoreId = null,
        string? notes = null, string? addedBy = null, int? addedByMemberId = null, int? itemId = null, bool autoMap = false)
        => throw new NotImplementedException();

    public void SoftDeleteItem(int itemId) => throw new NotImplementedException();

    public void CheckOffItem(int itemId, bool checkedOff = true) => throw new NotImplementedException();

    public void ClearAllCheckedOff() => throw new NotImplementedException();

    // Writes optimizer-chosen planned_store_id back onto the active list rows.
    public Dictionary<string, object?> ApplyOptimizerPlanToActiveList(BasketOptimizationResult result,
        string mode = "fast", bool clearFirst = true) => throw new NotImplementedException();
}
