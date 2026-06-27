using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/shopping_list_repo.py
// Soft-delete (is_deleted) + check-off model. Planned-store assignment written back by the optimizer.
public static class ShoppingListRepo
{
    public static IReadOnlyList<ShoppingListRow> ListActiveItems(SqliteConnection conn, int? storeId = null,
        bool includeCheckedOff = false) => throw new NotImplementedException();

    public static IReadOnlyList<ShoppingListRow> ListAllItems(SqliteConnection conn) => throw new NotImplementedException();

    public static ShoppingListRow? GetItem(SqliteConnection conn, int rowId) => throw new NotImplementedException();

    public static int AddItem(SqliteConnection conn, string displayName, double quantity = 1.0, string unit = "",
        string category = "", string notes = "", string? addedBy = null, int? addedByMemberId = null,
        int? plannedStoreId = null, int? itemId = null) => throw new NotImplementedException();

    public static void SetCheckedOff(SqliteConnection conn, int itemId, bool checkedOff) => throw new NotImplementedException();

    public static void DeleteItem(SqliteConnection conn, int itemId) => throw new NotImplementedException();

    public static void ClearAllItems(SqliteConnection conn) => throw new NotImplementedException();

    public static void ClearCheckedOffItems(SqliteConnection conn) => throw new NotImplementedException();

    public static void SetPlannedStoreId(SqliteConnection conn, int itemId, int? plannedStoreId) => throw new NotImplementedException();

    public static int BulkSetPlannedStoreIdsByItemId(SqliteConnection conn,
        IReadOnlyList<(int ItemId, int? StoreId)> assignments, bool activeOnly = true) => throw new NotImplementedException();
}
