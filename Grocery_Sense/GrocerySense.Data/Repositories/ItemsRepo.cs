using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

public static class ItemsRepo
{
    private const string SelectCols =
        "id, canonical_name, category, default_unit, typical_package_size, typical_package_unit, is_tracked, notes";

    private const int ParamChunk = 900; // SQLite default max variables is 999; stay under it.

    private static Item Map(SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        CanonicalName: r.GetString(1),
        Category: r.GetStringOrNull(2),
        DefaultUnit: r.GetStringOrNull(3),
        TypicalPackageSize: r.GetDoubleOrNull(4),
        TypicalPackageUnit: r.GetStringOrNull(5),
        IsTracked: r.GetBoolean(6),
        Notes: r.GetStringOrNull(7));

    public static Item CreateItem(SqliteConnection conn, string canonicalName, string? category = null,
        string? defaultUnit = null, double? typicalPackageSize = null, string? typicalPackageUnit = null,
        bool isTracked = true, string? notes = null, SqliteTransaction? tx = null)
    {
        var name = (canonicalName ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("canonical_name cannot be empty", nameof(canonicalName));

        // Case-insensitive dedupe: return the existing row rather than splitting price history.
        var existing = GetItemByName(conn, name, tx);
        if (existing is not null) return existing;

        using (var cmd = Db.Command(conn, tx,
            """
            INSERT OR IGNORE INTO items
                (canonical_name, category, default_unit, typical_package_size, typical_package_unit, is_tracked, notes)
            VALUES ($name, $category, $unit, $size, $pkgUnit, $tracked, $notes)
            """))
        {
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$category", Db.OrNull(category));
            cmd.Parameters.AddWithValue("$unit", Db.OrNull(defaultUnit));
            cmd.Parameters.AddWithValue("$size", Db.OrNull(typicalPackageSize));
            cmd.Parameters.AddWithValue("$pkgUnit", Db.OrNull(typicalPackageUnit));
            cmd.Parameters.AddWithValue("$tracked", isTracked ? 1 : 0);
            cmd.Parameters.AddWithValue("$notes", Db.OrNull(notes));
            var affected = cmd.ExecuteNonQuery();
            if (affected == 0)
                return GetItemByName(conn, name, tx)
                    ?? throw new InvalidOperationException("create_item: row exists but lookup returned null");
        }

        return GetItemById(conn, (int)Db.LastRowId(conn, tx), tx)
            ?? throw new InvalidOperationException("create_item succeeded but could not re-fetch item");
    }

    public static Item? GetItemById(SqliteConnection conn, int itemId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM items WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", itemId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public static Item? GetItemByName(SqliteConnection conn, string canonicalName, SqliteTransaction? tx = null)
    {
        var name = (canonicalName ?? "").Trim();
        if (name.Length == 0) return null;

        // COLLATE NOCASE (not lower()) so the seek uses idx_items_name_nocase; bind the trimmed name as-is.
        using var cmd = Db.Command(conn, tx,
            $"SELECT {SelectCols} FROM items WHERE canonical_name = $name COLLATE NOCASE LIMIT 1");
        cmd.Parameters.AddWithValue("$name", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public static IReadOnlyList<(int Id, string CanonicalName)> ListAllItemNames(SqliteConnection conn,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT id, canonical_name FROM items ORDER BY canonical_name ASC");
        using var r = cmd.ExecuteReader();
        var names = new List<(int, string)>();
        while (r.Read()) names.Add((r.GetInt32(0), r.GetString(1)));
        return names;
    }

    public static IReadOnlyList<Item> ListItems(SqliteConnection conn, bool includeUntracked = false,
        SqliteTransaction? tx = null)
    {
        var where = includeUntracked ? "" : "WHERE is_tracked = 1";
        using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM items {where} ORDER BY canonical_name ASC");
        using var r = cmd.ExecuteReader();
        var items = new List<Item>();
        while (r.Read()) items.Add(Map(r));
        return items;
    }

    public static void SetItemTracked(SqliteConnection conn, int itemId, bool isTracked, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE items SET is_tracked = $v WHERE id = $id");
        cmd.Parameters.AddWithValue("$v", isTracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyDictionary<int, Item> GetItemsByIds(SqliteConnection conn, IReadOnlyList<int> itemIds,
        SqliteTransaction? tx = null)
    {
        var ids = itemIds.Where(x => x > 0).Distinct().ToList();
        var result = new Dictionary<int, Item>();
        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var names = chunk.Select((_, i) => $"$p{i}").ToList();
            using var cmd = Db.Command(conn, tx, $"SELECT {SelectCols} FROM items WHERE id IN ({string.Join(",", names)})");
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var item = Map(r); result[item.Id] = item; }
        }
        return result;
    }

    public static IReadOnlyDictionary<string, Item> GetItemsByNames(SqliteConnection conn, IReadOnlyList<string> names,
        SqliteTransaction? tx = null)
    {
        var cleaned = names
            .Select(n => (n ?? "").Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        var result = new Dictionary<string, Item>();
        foreach (var chunk in cleaned.Chunk(ParamChunk))
        {
            var ph = chunk.Select((_, i) => $"$p{i}").ToList();
            // COLLATE NOCASE so each IN probe can seek idx_items_name_nocase; `cleaned` is already lowercased,
            // which NOCASE matches against any stored casing (result keying below stays lowercased to match callers).
            using var cmd = Db.Command(conn, tx,
                $"SELECT {SelectCols} FROM items WHERE canonical_name COLLATE NOCASE IN ({string.Join(",", ph)})");
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(ph[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var item = Map(r); result[item.CanonicalName.Trim().ToLowerInvariant()] = item; }
        }
        return result;
    }
}
