using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/item_aliases_repo.py
// Kept instance-based (the Phase-3 IngredientMappingService composes it). Stateless: the caller always
// supplies the connection (+ tx for the buffered-write flush), so the Python optional-conn-or-open-own
// behavior collapses to a required connection.
public sealed class ItemAliasesRepo
{
    private const string SelectCols =
        "id, alias_text, item_id, confidence, source, created_at, last_seen_at, times_seen";

    private static ItemAlias Map(SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        AliasText: r.GetString(1),
        ItemId: r.GetInt32(2),
        Confidence: r.GetDouble(3),
        Source: r.GetString(4),
        CreatedAt: r.GetStringOrNull(5),
        LastSeenAt: r.GetStringOrNull(6),
        TimesSeen: r.GetInt32(7));

    public ItemAlias? GetByAlias(SqliteConnection conn, string aliasText, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM item_aliases WHERE alias_text = $alias");
        cmd.Parameters.AddWithValue("$alias", aliasText.Trim().ToLowerInvariant());
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public void UpsertAlias(SqliteConnection conn, string aliasText, int itemId, double confidence = 1.0,
        string source = "manual", SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            INSERT INTO item_aliases (alias_text, item_id, confidence, source, created_at, last_seen_at, times_seen)
            VALUES ($alias, $item, $conf, $source, $now, $now, 1)
            ON CONFLICT(alias_text) DO UPDATE SET
                item_id = excluded.item_id,
                confidence = excluded.confidence,
                source = excluded.source,
                last_seen_at = excluded.last_seen_at,
                times_seen = item_aliases.times_seen + 1
            """);
        var now = Db.NowIso();
        cmd.Parameters.AddWithValue("$alias", aliasText.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$conf", confidence);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void MarkSeen(SqliteConnection conn, string aliasText, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "UPDATE item_aliases SET last_seen_at = $now, times_seen = times_seen + 1 WHERE alias_text = $alias");
        cmd.Parameters.AddWithValue("$now", Db.NowIso());
        cmd.Parameters.AddWithValue("$alias", aliasText.Trim().ToLowerInvariant());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ItemAlias> ListAll(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            $"SELECT {SelectCols} FROM item_aliases ORDER BY times_seen DESC, alias_text ASC");
        using var r = cmd.ExecuteReader();
        var aliases = new List<ItemAlias>();
        while (r.Read()) aliases.Add(Map(r));
        return aliases;
    }

    // Aliases for a single item (uses idx_item_aliases_item_id) — avoids loading the whole table to filter.
    public IReadOnlyList<ItemAlias> ListByItem(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            $"SELECT {SelectCols} FROM item_aliases WHERE item_id = $item ORDER BY times_seen DESC, alias_text ASC");
        cmd.Parameters.AddWithValue("$item", itemId);
        using var r = cmd.ExecuteReader();
        var aliases = new List<ItemAlias>();
        while (r.Read()) aliases.Add(Map(r));
        return aliases;
    }
}
