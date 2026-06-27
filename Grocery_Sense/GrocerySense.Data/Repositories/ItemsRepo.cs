using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/items_repo.py
public static class ItemsRepo
{
    public static Item CreateItem(SqliteConnection conn, string canonicalName, string? category = null, string? defaultUnit = null,
        double? typicalPackageSize = null, string? typicalPackageUnit = null, bool isTracked = true, string? notes = null)
        => throw new NotImplementedException();

    public static Item? GetItemById(SqliteConnection conn, int itemId) => throw new NotImplementedException();

    public static Item? GetItemByName(SqliteConnection conn, string canonicalName) => throw new NotImplementedException();

    public static IReadOnlyList<(int Id, string CanonicalName)> ListAllItemNames(SqliteConnection conn) => throw new NotImplementedException();

    public static IReadOnlyList<Item> ListItems(SqliteConnection conn, bool includeUntracked = false) => throw new NotImplementedException();

    public static void SetItemTracked(SqliteConnection conn, int itemId, bool isTracked) => throw new NotImplementedException();

    // Batch readers (chunk IN-lists at 900 params — see reference _SQL_PARAM_CHUNK).
    public static IReadOnlyDictionary<int, Item> GetItemsByIds(SqliteConnection conn, IReadOnlyList<int> itemIds) => throw new NotImplementedException();

    public static IReadOnlyDictionary<string, Item> GetItemsByNames(SqliteConnection conn, IReadOnlyList<string> names) => throw new NotImplementedException();
}
