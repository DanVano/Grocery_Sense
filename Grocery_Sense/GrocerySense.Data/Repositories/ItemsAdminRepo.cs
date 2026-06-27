using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/items_admin_repo.py
// Admin ops: rename, merge (re-points every item_id FK table, SAVEPOINT per table), toggle tracked.
public sealed class ItemsAdminRepo
{
    public static readonly string[] ValidUnits = { "each", "lb", "kg", "g" };

    public IReadOnlyList<ItemRow> SearchItems(string query = "", int limit = 250) => throw new NotImplementedException();

    public Dictionary<string, object?>? GetItem(int itemId) => throw new NotImplementedException();

    public int ToggleTracked(int itemId) => throw new NotImplementedException();

    public void SetDefaultUnit(int itemId, string? defaultUnit) => throw new NotImplementedException();

    public void RenameItem(int itemId, string newName) => throw new NotImplementedException();

    public void MergeItems(int targetItemId, int sourceItemId, bool keepSourceAsAlias = true) => throw new NotImplementedException();
}
