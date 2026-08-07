using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/shopping_list_repo.py
// Soft-delete (is_deleted) + check-off model. Planned-store assignment is written back by the optimizer
// (Phase 4) inside its transaction, hence the tx parameter on the bulk setters.
public static class ShoppingListRepo
{
    private const string SelectCols =
        "id, display_name, quantity, unit, category, is_checked_off, notes, " +
        "added_by, added_by_member_id, is_active, planned_store_id, item_id, priority";

    // Valid shopping_list.priority values; anything else normalizes to 'normal'.
    private static readonly HashSet<string> Priorities = new(StringComparer.Ordinal)
        { "must_have", "normal", "wait_for_sale" };

    private static string NormalizePriority(string? p) =>
        p is not null && Priorities.Contains(p) ? p : "normal";

    private static ShoppingListRow Map(SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        DisplayName: r.GetStringOrNull(1) ?? "",
        Quantity: r.IsDBNull(2) ? 1.0 : r.GetDouble(2),
        Unit: r.GetStringOrNull(3) ?? "",
        Category: r.GetStringOrNull(4) ?? "",
        IsCheckedOff: !r.IsDBNull(5) && r.GetBoolean(5),
        Notes: r.GetStringOrNull(6) ?? "",
        AddedBy: string.IsNullOrEmpty(r.GetStringOrNull(7)) ? null : r.GetString(7),
        AddedByMemberId: r.GetIntOrNull(8),
        IsActive: !r.IsDBNull(9) && r.GetBoolean(9),
        PlannedStoreId: r.GetIntOrNull(10),
        ItemId: r.GetIntOrNull(11),
        Priority: r.GetStringOrNull(12) ?? "normal");

    public static IReadOnlyList<ShoppingListRow> ListActiveItems(SqliteConnection conn, int? storeId = null,
        bool includeCheckedOff = false, SqliteTransaction? tx = null)
    {
        var sql = $"SELECT {SelectCols} FROM shopping_list WHERE is_active = 1 AND is_deleted = 0";
        if (!includeCheckedOff) sql += " AND is_checked_off = 0";
        if (storeId is not null) sql += " AND planned_store_id = $store";
        sql += " ORDER BY id DESC";

        using var cmd = Db.Command(conn, tx, sql);
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        return ReadAll(cmd);
    }

    public static ShoppingListRow? GetItem(SqliteConnection conn, int rowId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM shopping_list WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", rowId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public static int AddItem(SqliteConnection conn, string displayName, double quantity = 1.0, string unit = "",
        string category = "", string notes = "", string? addedBy = null, int? addedByMemberId = null,
        int? plannedStoreId = null, int? itemId = null, string priority = "normal", SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            INSERT INTO shopping_list
                (display_name, quantity, unit, category, notes, added_by, added_by_member_id, planned_store_id,
                 item_id, priority)
            VALUES ($name, $qty, $unit, $cat, $notes, $by, $member, $store, $item, $priority)
            """);
        cmd.Parameters.AddWithValue("$name", (displayName ?? "").Trim());
        cmd.Parameters.AddWithValue("$qty", quantity);
        cmd.Parameters.AddWithValue("$unit", (unit ?? "").Trim());
        cmd.Parameters.AddWithValue("$cat", (category ?? "").Trim());
        cmd.Parameters.AddWithValue("$notes", (notes ?? "").Trim());
        var by = (addedBy ?? "").Trim();
        cmd.Parameters.AddWithValue("$by", by.Length == 0 ? DBNull.Value : by);
        cmd.Parameters.AddWithValue("$member", Db.OrNull(addedByMemberId));
        cmd.Parameters.AddWithValue("$store", Db.OrNull(plannedStoreId));
        cmd.Parameters.AddWithValue("$item", Db.OrNull(itemId));
        cmd.Parameters.AddWithValue("$priority", NormalizePriority(priority));
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    // Repoint a row at a different catalog item (V3 actionable swaps): display name + item_id change,
    // everything else (qty/unit/notes/priority/planned store/attribution) is preserved by not touching it.
    public static void RepointItem(SqliteConnection conn, int rowId, int itemId, string displayName,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE shopping_list SET item_id = $item, display_name = $name WHERE id = $id");
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$name", (displayName ?? "").Trim());
        cmd.Parameters.AddWithValue("$id", rowId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new ArgumentException($"Shopping list row not found: {rowId}", nameof(rowId));
    }

    // Backfill a NULL item_id after a trusted name match (V3 upsert, grill Q6). Guarded in SQL: a row that
    // already has a DIFFERENT item_id is never overwritten — conflicting mappings surface instead of merging.
    public static void SetItemIdIfNull(SqliteConnection conn, int rowId, int itemId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE shopping_list SET item_id = $item WHERE id = $id AND item_id IS NULL");
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.ExecuteNonQuery();
    }

    // Update the user-editable details of one row (F05). An unknown row throws — a silent no-op would
    // read as a successful save.
    public static void UpdateItemDetails(SqliteConnection conn, int rowId, double quantity, string unit,
        string notes, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE shopping_list SET quantity = $q, unit = $u, notes = $n WHERE id = $id");
        cmd.Parameters.AddWithValue("$q", quantity);
        cmd.Parameters.AddWithValue("$u", unit ?? "");
        cmd.Parameters.AddWithValue("$n", notes ?? "");
        cmd.Parameters.AddWithValue("$id", rowId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new ArgumentException($"Shopping list row not found: {rowId}", nameof(rowId));
    }

    public static void SetPriority(SqliteConnection conn, int rowId, string priority, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE shopping_list SET priority = $p WHERE id = $id");
        cmd.Parameters.AddWithValue("$p", NormalizePriority(priority));
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.ExecuteNonQuery();
    }

    public static void SetCheckedOff(SqliteConnection conn, int itemId, bool checkedOff, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE shopping_list SET is_checked_off = $v WHERE id = $id");
        cmd.Parameters.AddWithValue("$v", checkedOff ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    public static void DeleteItem(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE shopping_list SET is_deleted = 1 WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    public static void ClearCheckedOffItems(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE shopping_list SET is_deleted = 1 WHERE is_checked_off = 1 AND is_active = 1");
        cmd.ExecuteNonQuery();
    }

    public static int ClearPlannedStoreIdsForActiveItems(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            UPDATE shopping_list SET planned_store_id = NULL
            WHERE is_active = 1 AND is_deleted = 0 AND is_checked_off = 0
            """);
        return cmd.ExecuteNonQuery();
    }

    public static void SetPlannedStoreId(SqliteConnection conn, int itemId, int? plannedStoreId,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE shopping_list SET planned_store_id = $store WHERE id = $id");
        cmd.Parameters.AddWithValue("$store", Db.OrNull(plannedStoreId));
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    // Keyed by canonical items.id (what an optimizer result holds). Returns rows actually updated.
    public static int BulkSetPlannedStoreIdsByItemId(SqliteConnection conn,
        IReadOnlyList<(int ItemId, int? StoreId)> assignments, bool activeOnly = true, SqliteTransaction? tx = null)
    {
        var sql = activeOnly
            ? "UPDATE shopping_list SET planned_store_id = $store WHERE item_id = $item AND is_active = 1 AND is_deleted = 0"
            : "UPDATE shopping_list SET planned_store_id = $store WHERE item_id = $item";
        var updated = 0;
        foreach (var (itemId, storeId) in assignments)
        {
            using var cmd = Db.Command(conn, tx, sql);
            cmd.Parameters.AddWithValue("$store", Db.OrNull(storeId));
            cmd.Parameters.AddWithValue("$item", itemId);
            updated += cmd.ExecuteNonQuery();
        }
        return updated;
    }

    private static IReadOnlyList<ShoppingListRow> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<ShoppingListRow>();
        while (r.Read()) rows.Add(Map(r));
        return rows;
    }
}
