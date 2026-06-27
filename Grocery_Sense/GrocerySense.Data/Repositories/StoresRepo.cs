using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/stores_repo.py
// CRUD only — raw parameterized SQL. Caller passes an open connection (and a transaction when it owns
// an atomic scope), mirroring Python's connection_scope(). distance_km / delete_store are dropped from
// v1 (optimizer redesign cut distance; delete_store was test-only in Python).
public static class StoresRepo
{
    private const string SelectCols =
        "id, name, address, city, postal_code, flipp_store_id, is_favorite, priority, shop_here, is_active, notes";

    private static Store Map(SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        Name: r.GetString(1),
        Address: r.GetStringOrNull(2),
        City: r.GetStringOrNull(3),
        PostalCode: r.GetStringOrNull(4),
        FlippStoreId: r.GetStringOrNull(5),
        IsFavorite: r.GetBoolean(6),
        Priority: r.IsDBNull(7) ? 0 : r.GetInt32(7),
        ShopHere: r.IsDBNull(8) || r.GetBoolean(8),
        IsActive: r.IsDBNull(9) || r.GetBoolean(9),
        Notes: r.GetStringOrNull(10));

    public static Store CreateStore(SqliteConnection conn, string name, string? address = null, string? city = null,
        string? postalCode = null, string? flippStoreId = null, bool isFavorite = false, int priority = 0,
        string? notes = null, SqliteTransaction? tx = null)
    {
        using (var cmd = Db.Command(conn, tx,
            """
            INSERT INTO stores (name, address, city, postal_code, flipp_store_id, is_favorite, priority, notes, created_at)
            VALUES ($name, $address, $city, $postal, $flipp, $fav, $prio, $notes, $created)
            """))
        {
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$address", Db.OrNull(address));
            cmd.Parameters.AddWithValue("$city", Db.OrNull(city));
            cmd.Parameters.AddWithValue("$postal", Db.OrNull(postalCode));
            cmd.Parameters.AddWithValue("$flipp", Db.OrNull(flippStoreId));
            cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$prio", priority);
            cmd.Parameters.AddWithValue("$notes", Db.OrNull(notes));
            cmd.Parameters.AddWithValue("$created", Db.NowIso());
            cmd.ExecuteNonQuery();
        }

        return GetStoreById(conn, (int)Db.LastRowId(conn, tx), tx)!;
    }

    public static Store? GetStoreById(SqliteConnection conn, int storeId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM stores WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", storeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public static IReadOnlyList<Store> ListStores(SqliteConnection conn, bool onlyFavorites = false,
        bool orderByPriority = true, int? limit = null, bool includeArchived = false, SqliteTransaction? tx = null)
    {
        var conditions = new List<string>();
        if (onlyFavorites) conditions.Add("is_favorite = 1");
        if (!includeArchived) conditions.Add("is_active = 1");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var order = orderByPriority ? "ORDER BY priority DESC, name ASC" : "ORDER BY name ASC";
        var limitClause = limit is not null ? " LIMIT $limit" : "";

        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM stores {where} {order}{limitClause}");
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", limit.Value);

        using var r = cmd.ExecuteReader();
        var stores = new List<Store>();
        while (r.Read()) stores.Add(Map(r));
        return stores;
    }

    public static void SetStoreFavorite(SqliteConnection conn, int storeId, bool isFavorite, int? priority = null,
        SqliteTransaction? tx = null)
    {
        var sql = priority is not null
            ? "UPDATE stores SET is_favorite = $fav, priority = $prio WHERE id = $id"
            : "UPDATE stores SET is_favorite = $fav WHERE id = $id";
        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
        if (priority is not null) cmd.Parameters.AddWithValue("$prio", priority.Value);
        cmd.Parameters.AddWithValue("$id", storeId);
        cmd.ExecuteNonQuery();
    }

    public static void SetStoreShopHere(SqliteConnection conn, int storeId, bool shopHere, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE stores SET shop_here = $v WHERE id = $id");
        cmd.Parameters.AddWithValue("$v", shopHere ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", storeId);
        cmd.ExecuteNonQuery();
    }

    public static void SetStoreActive(SqliteConnection conn, int storeId, bool isActive, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE stores SET is_active = $v WHERE id = $id");
        cmd.Parameters.AddWithValue("$v", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", storeId);
        cmd.ExecuteNonQuery();
    }

    public static void UpdateStoreAddress(SqliteConnection conn, int storeId, string? address = null,
        string? city = null, string? postalCode = null, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE stores SET address = $address, city = $city, postal_code = $postal WHERE id = $id");
        cmd.Parameters.AddWithValue("$address", Db.OrNull(address));
        cmd.Parameters.AddWithValue("$city", Db.OrNull(city));
        cmd.Parameters.AddWithValue("$postal", Db.OrNull(postalCode));
        cmd.Parameters.AddWithValue("$id", storeId);
        cmd.ExecuteNonQuery();
    }

    public static void UpdateStore(SqliteConnection conn, int storeId, string name, string? address = null,
        string? city = null, string? postalCode = null, string? flippStoreId = null, bool isFavorite = false,
        int priority = 0, string? notes = null, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            UPDATE stores
            SET name = $name, address = $address, city = $city, postal_code = $postal,
                flipp_store_id = $flipp, is_favorite = $fav, priority = $prio, notes = $notes
            WHERE id = $id
            """);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$address", Db.OrNull(address));
        cmd.Parameters.AddWithValue("$city", Db.OrNull(city));
        cmd.Parameters.AddWithValue("$postal", Db.OrNull(postalCode));
        cmd.Parameters.AddWithValue("$flipp", Db.OrNull(flippStoreId));
        cmd.Parameters.AddWithValue("$fav", isFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$prio", priority);
        cmd.Parameters.AddWithValue("$notes", Db.OrNull(notes));
        cmd.Parameters.AddWithValue("$id", storeId);
        cmd.ExecuteNonQuery();
    }

    public static Store UpsertStoreFromFlipp(SqliteConnection conn, string name, string flippStoreId,
        string? address = null, string? city = null, string? postalCode = null, SqliteTransaction? tx = null)
    {
        Store? existing;
        using (var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM stores WHERE flipp_store_id = $flipp"))
        {
            cmd.Parameters.AddWithValue("$flipp", flippStoreId);
            using var r = cmd.ExecuteReader();
            existing = r.Read() ? Map(r) : null;
        }

        if (existing is not null)
        {
            if (existing.Name != name || existing.Address != address
                || existing.City != city || existing.PostalCode != postalCode)
            {
                UpdateStoreAddress(conn, existing.Id, address, city, postalCode, tx);
                using var upd = Db.Command(conn, tx, "UPDATE stores SET name = $name WHERE id = $id");
                upd.Parameters.AddWithValue("$name", name);
                upd.Parameters.AddWithValue("$id", existing.Id);
                upd.ExecuteNonQuery();
            }
            return existing with { Name = name, Address = address, City = city, PostalCode = postalCode };
        }

        return CreateStore(conn, name, address, city, postalCode, flippStoreId, tx: tx);
    }
}
