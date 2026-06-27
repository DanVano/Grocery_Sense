using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/stores_repo.py
// CRUD only — raw SQL, parameterized. Caller passes an open connection (the service owns the
// transaction scope, mirroring Python's connection_scope()).
public static class StoresRepo
{
    public static Store CreateStore(SqliteConnection conn, string name, string? address = null, string? city = null,
        string? postalCode = null, string? flippStoreId = null, bool isFavorite = false, int priority = 0, string? notes = null)
        => throw new NotImplementedException();

    public static Store? GetStoreById(SqliteConnection conn, int storeId) => throw new NotImplementedException();

    public static IReadOnlyList<Store> ListStores(SqliteConnection conn, bool onlyFavorites = false,
        bool orderByPriority = true, int? limit = null, bool includeArchived = false) => throw new NotImplementedException();

    public static void SetStoreFavorite(SqliteConnection conn, int storeId, bool isFavorite, int? priority = null)
        => throw new NotImplementedException();

    public static void SetStoreShopHere(SqliteConnection conn, int storeId, bool shopHere) => throw new NotImplementedException();

    public static void SetStoreDistanceKm(SqliteConnection conn, int storeId, double? distanceKm) => throw new NotImplementedException();

    public static void UpdateStore(SqliteConnection conn, int storeId, string name, string? address = null, string? city = null,
        string? postalCode = null, string? flippStoreId = null, bool isFavorite = false, int priority = 0, string? notes = null)
        => throw new NotImplementedException();

    public static void SetStoreActive(SqliteConnection conn, int storeId, bool isActive) => throw new NotImplementedException();

    public static Store UpsertStoreFromFlipp(SqliteConnection conn, string name, string flippStoreId,
        string? address = null, string? city = null, string? postalCode = null) => throw new NotImplementedException();

    // PORT: update_store_address, delete_store — see stores_repo.py
}
