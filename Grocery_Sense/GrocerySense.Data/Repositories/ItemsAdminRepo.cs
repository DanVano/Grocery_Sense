using System.Collections.Immutable;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/.../data/repositories/items_admin_repo.py — the Item Manager admin ops:
// search, rename, and safe merge (re-point every item_id-bearing table). Adds CorrectLineMapping, the
// receipt-line alias-correction (fix line + learn) that has no Python source.
//
// No ensure_schema: items.is_tracked/default_unit come from the migration ledger, not a runtime ALTER.
// Merge + correction MUST be atomic (they touch many tables), so those take a required transaction; the
// caller owns it (matches ReceiptsRepo.IngestReceipt).
public static class ItemsAdminRepo
{
    // Canonical unit vocabulary (lowercased), mirroring UnitNormalizationService.NormalizeUnit's outputs.
    // Duplicated here because Data cannot reference Core; a merged item's default_unit is only ever set from
    // that vocabulary, so a too-narrow whitelist would silently drop a valid unit like "l" during promotion.
    private static readonly string[] ValidUnits =
        { "each", "lb", "kg", "g", "oz", "l", "ml", "fl_oz", "cup", "tbsp", "tsp", "gal", "pint", "dozen", "bunch", "case", "pack" };

    // Every table with an item_id column referencing items.id, re-pointed on merge. Verified against the
    // live schema (Database.cs), NOT the Python list — flyer_deals/price_drop_alerts/watchlist post-date it.
    // No table here has a UNIQUE on item_id, so a plain UPDATE never collides. watchlist is handled
    // separately below: it has no UNIQUE(item_id) either, so a blind UPDATE would leave the target with two
    // active watches. Any new item_id table MUST be added here (or to the watchlist special-case).
    // internal (not private) so a Tests schema-drift guard can assert this list plus watchlist covers
    // EVERY item_id-bearing table in the live schema — a new one added to neither orphans its rows on merge.
    // ImmutableArray (not string[]): now that it's exposed beyond this class, readonly alone guards only the
    // reference — array elements would still be reassignable. The names feed interpolated UPDATE SQL, so pin them.
    internal static readonly ImmutableArray<string> ItemIdTables =
        ["prices", "receipt_line_items", "shopping_list", "flyer_deals", "price_drop_alerts", "item_aliases"];

    private static readonly ItemAliasesRepo Aliases = new();

    // Items with light price stats, newest-tracked first. Optional case-insensitive name filter.
    public static IReadOnlyList<ItemAdminRow> SearchItems(SqliteConnection conn, string query = "",
        int limit = 250, SqliteTransaction? tx = null)
    {
        var q = (query ?? "").Trim();
        // Escape LIKE metacharacters so a query like "2% milk" doesn't become a wildcard that over-matches the
        // merge picker (picking the wrong item destructively merges price history).
        var where = q.Length > 0 ? @"WHERE i.canonical_name LIKE $q ESCAPE '\'" : "";
        // Pick the (bounded) matching items FIRST, then aggregate prices only for those ids. The old shape
        // GROUP BY'd the entire prices table before applying the LIMIT, so search cost grew with total price
        // history rather than with the handful of rows actually returned.
        using var cmd = Db.Command(conn, tx, $"""
            WITH selected AS (
                SELECT i.id, i.canonical_name, i.is_tracked, i.default_unit
                FROM items i
                {where}
                ORDER BY COALESCE(i.is_tracked, 0) DESC, i.canonical_name ASC
                LIMIT $limit
            ),
            price_stats AS (
                SELECT p.item_id, COUNT(1) AS price_points, MAX(p.date) AS last_price_date
                FROM prices p JOIN selected s ON s.id = p.item_id
                GROUP BY p.item_id
            )
            SELECT s.id, COALESCE(s.canonical_name, ''), COALESCE(s.is_tracked, 0), s.default_unit,
                   COALESCE(ps.price_points, 0), ps.last_price_date
            FROM selected s
            LEFT JOIN price_stats ps ON ps.item_id = s.id
            ORDER BY COALESCE(s.is_tracked, 0) DESC, s.canonical_name ASC
            """);
        if (q.Length > 0) cmd.Parameters.AddWithValue("$q", $"%{EscapeLike(q)}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var r = cmd.ExecuteReader();
        var rows = new List<ItemAdminRow>();
        while (r.Read())
            rows.Add(new ItemAdminRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2) != 0,
                r.GetStringOrNull(3)?.Trim().ToLowerInvariant(), r.GetInt32(4), r.GetStringOrNull(5)));
        return rows;
    }

    // Rename an item's canonical name. canonical_name is UNIQUE, so colliding with an existing name is a
    // constraint error -> surface it as a clear message rather than a raw SqliteException.
    public static void RenameItem(SqliteConnection conn, int itemId, string newName, SqliteTransaction? tx = null)
    {
        var name = (newName ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("New name cannot be empty.", nameof(newName));

        using var cmd = Db.Command(conn, tx, "UPDATE items SET canonical_name = $name WHERE id = $id");
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", itemId);
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"An item named \"{name}\" already exists — merge into it instead.");
        }
    }

    // Merge source -> target: move every item_id reference, promote tracked/default_unit, keep the source
    // name as an alias, delete source. All within the caller's transaction (all-or-nothing).
    public static void MergeItems(SqliteConnection conn, SqliteTransaction tx, int targetItemId, int sourceItemId,
        bool keepSourceAsAlias = true)
    {
        if (targetItemId == sourceItemId) throw new ArgumentException("Target and source item are the same.");

        var target = ItemsRepo.GetItemById(conn, targetItemId, tx)
            ?? throw new InvalidOperationException($"Target item not found: {targetItemId}");
        var source = ItemsRepo.GetItemById(conn, sourceItemId, tx)
            ?? throw new InvalidOperationException($"Source item not found: {sourceItemId}");

        // Promote: if either is tracked, target is tracked; adopt source's default unit if target has none.
        if (!target.IsTracked && source.IsTracked)
            Exec(conn, tx, "UPDATE items SET is_tracked = 1 WHERE id = $id", ("$id", targetItemId));

        var sourceUnit = source.DefaultUnit?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(target.DefaultUnit) && sourceUnit is not null && ValidUnits.Contains(sourceUnit))
            Exec(conn, tx, "UPDATE items SET default_unit = $u WHERE id = $id",
                ("$u", sourceUnit), ("$id", targetItemId));

        // Move references. No item_id UNIQUE on these tables, so a straight UPDATE is safe.
        foreach (var table in ItemIdTables)
            Exec(conn, tx, $"UPDATE {table} SET item_id = $t WHERE item_id = $s",
                ("$t", targetItemId), ("$s", sourceItemId));

        // watchlist: prevent two ACTIVE watches for the merged item. Only drop the source's watch when the
        // target already has an active one — scoping on is_active=1 so a paused (soft-deleted) target watch
        // never causes the source's live watch to be silently deleted.
        Exec(conn, tx,
            "DELETE FROM watchlist WHERE item_id = $s AND EXISTS (SELECT 1 FROM watchlist WHERE item_id = $t AND is_active = 1)",
            ("$s", sourceItemId), ("$t", targetItemId));
        Exec(conn, tx, "UPDATE watchlist SET item_id = $t WHERE item_id = $s",
            ("$t", targetItemId), ("$s", sourceItemId));

        // Keep the source name discoverable: its canonical name becomes an alias of the target.
        if (keepSourceAsAlias && !string.IsNullOrWhiteSpace(source.CanonicalName))
            Aliases.UpsertAlias(conn, source.CanonicalName, targetItemId, 1.0, "merge", tx);

        Exec(conn, tx, "DELETE FROM items WHERE id = $id", ("$id", sourceItemId));
    }

    // Alias-correction ("fix line + learn"): re-point one receipt line and the price row it produced to the
    // correct item, and learn description -> item so future scans map right. No retro-sweep — earlier
    // mis-mapped receipts are cleaned via MergeItems. All in the caller's transaction.
    // ponytail: an originally-unmapped line (oldItemId null) has no price row to move; it still re-points the
    // line + learns the alias. Back-creating the missing historical price is out of scope (re-import covers it).
    public static void CorrectLineMapping(SqliteConnection conn, SqliteTransaction tx, int lineItemId,
        int receiptId, string description, int? oldItemId, int newItemId)
    {
        Exec(conn, tx, "UPDATE receipt_line_items SET item_id = $new WHERE id = $line",
            ("$new", newItemId), ("$line", lineItemId));

        // The price row from this line is keyed (receipt_id, raw_name = description, item_id = old). Move ONLY
        // ONE matching row: two identical-description lines on the same receipt map to identical price rows, so
        // an unbounded UPDATE would move both when fixing the first line, orphaning the second correction.
        // ponytail: no line_item_id link on prices; single-row move keeps counts/attribution correct without a
        // schema migration (identical rows make the exact pairing immaterial). Add line_item_id if that changes.
        if (oldItemId is int old)
            Exec(conn, tx,
                """
                UPDATE prices SET item_id = $new WHERE id = (
                    SELECT id FROM prices
                    WHERE receipt_id = $rid AND raw_name = $raw AND item_id = $old
                    ORDER BY id LIMIT 1)
                """,
                ("$new", newItemId), ("$rid", receiptId), ("$raw", description), ("$old", old));

        Aliases.UpsertAlias(conn, description, newItemId, 1.0, "manual_correction", tx);
    }

    // Escape LIKE wildcards for use with ESCAPE '\'. Backslash first so we don't double-escape our own escapes.
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] ps)
    {
        using var cmd = Db.Command(conn, tx, sql);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
