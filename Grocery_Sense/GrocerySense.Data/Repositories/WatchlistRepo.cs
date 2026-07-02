using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// CRUD for the savings watchlist (one row per watched item). One active watch per item: re-adding an item
// updates its target and reactivates rather than stacking duplicate rows. Remove is a soft toggle (is_active=0)
// so a re-add keeps the original created_at.
public static class WatchlistRepo
{
    public static IReadOnlyList<SavingsWatchItem> ListActive(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            SELECT w.id, w.item_id, i.canonical_name, w.target_price, w.is_active, w.created_at
            FROM watchlist w JOIN items i ON i.id = w.item_id
            WHERE w.is_active = 1
            ORDER BY i.canonical_name ASC
            """);
        using var r = cmd.ExecuteReader();
        var rows = new List<SavingsWatchItem>();
        while (r.Read())
            rows.Add(new SavingsWatchItem(
                Id: r.GetInt32(0), ItemId: r.GetInt32(1), ItemName: r.GetString(2),
                TargetPrice: r.GetDoubleOrNull(3), IsActive: !r.IsDBNull(4) && r.GetBoolean(4),
                CreatedAt: r.GetStringOrNull(5)));
        return rows;
    }

    // Add or update the watch for an item. targetPrice null => "watch for any good deal". Returns the row id.
    public static int AddWatch(SqliteConnection conn, int itemId, double? targetPrice = null,
        SqliteTransaction? tx = null)
    {
        var existing = FindByItem(conn, itemId, tx);
        if (existing is not null)
        {
            using var upd = Db.Command(conn, tx,
                "UPDATE watchlist SET target_price = $t, is_active = 1 WHERE id = $id");
            upd.Parameters.AddWithValue("$t", Db.OrNull(targetPrice));
            upd.Parameters.AddWithValue("$id", existing.Value);
            upd.ExecuteNonQuery();
            return existing.Value;
        }

        using var cmd = Db.Command(conn, tx,
            "INSERT INTO watchlist (item_id, target_price) VALUES ($item, $t)");
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$t", Db.OrNull(targetPrice));
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static void RemoveWatch(SqliteConnection conn, int watchId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE watchlist SET is_active = 0 WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", watchId);
        cmd.ExecuteNonQuery();
    }

    private static int? FindByItem(SqliteConnection conn, int itemId, SqliteTransaction? tx)
    {
        using var cmd = Db.Command(conn, tx, "SELECT id FROM watchlist WHERE item_id = $item ORDER BY id LIMIT 1");
        cmd.Parameters.AddWithValue("$item", itemId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }
}
