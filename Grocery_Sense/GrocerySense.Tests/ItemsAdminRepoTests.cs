using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class ItemsAdminRepoTests
{
    // Every item_id-bearing table (must match ItemsAdminRepo.ItemIdTables + watchlist).
    private static readonly string[] ItemIdTables =
    {
        "prices", "receipt_line_items", "shopping_list", "flyer_deals", "price_drop_alerts",
        "item_aliases", "watchlist",
    };

    private static int CountItemIdRows(SqliteConnection conn, int itemId)
    {
        var total = 0;
        foreach (var t in ItemIdTables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {t} WHERE item_id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            total += Convert.ToInt32(cmd.ExecuteScalar());
        }
        return total;
    }

    // Seeds exactly one referencing row in each of the 7 item_id tables for `itemId`.
    private static void SeedRefs(SqliteConnection conn, int itemId, int storeId, int receiptId, int flyerId,
        string aliasText)
    {
        Exec(conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) " +
                   $"VALUES ({itemId}, {storeId}, 'receipt', '2026-06-01', '4.99', 'each')");
        Exec(conn, $"INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description) " +
                   $"VALUES ({receiptId}, 0, {itemId}, 'milk')");
        Exec(conn, $"INSERT INTO shopping_list (item_id, display_name) VALUES ({itemId}, 'milk')");
        Exec(conn, $"INSERT INTO flyer_deals (flyer_id, store_id, item_id, created_at) " +
                   $"VALUES ({flyerId}, {storeId}, {itemId}, '2026-06-01')");
        Exec(conn, $"INSERT INTO price_drop_alerts (item_id, store_id) VALUES ({itemId}, {storeId})");
        Exec(conn, $"INSERT INTO item_aliases (alias_text, item_id) VALUES ('{aliasText}', {itemId})");
        Exec(conn, $"INSERT INTO watchlist (item_id) VALUES ({itemId})");
    }

    private static (int store, int receipt, int flyer) Fixtures(SqliteConnection conn)
    {
        var store = StoresRepo.CreateStore(conn, "Loblaws").Id;
        Exec(conn, $"INSERT INTO receipts (store_id, purchase_date, source) VALUES ({store}, '2026-06-01', 'receipt')");
        var receipt = (int)(long)ExecScalar(conn, "SELECT last_insert_rowid()");
        Exec(conn, "INSERT INTO flyer_batches (store_id, imported_at) VALUES (1, '2026-06-01')");
        var flyer = (int)(long)ExecScalar(conn, "SELECT last_insert_rowid()");
        return (store, receipt, flyer);
    }


    // Task 3 guard: the search must return only matching items with exact price stats, even when the DB
    // holds far more price history for unrelated items (the bounded query must not fold their rows in).
    [Fact]
    public void SearchItems_returns_only_matches_with_price_stats_ignoring_unrelated_history()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;

        var milk = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var milk2 = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;

        // 300 unrelated items, each with price history the search must never aggregate.
        for (var i = 0; i < 300; i++)
        {
            var id = ItemsRepo.CreateItem(db.Conn, $"widget {i:D3}").Id;
            Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) " +
                          $"VALUES ({id}, {store}, 'receipt', '2026-05-01', '1.00', 'each')");
        }

        // milk: 2 points, latest 2026-06-15. 2% milk: 1 point, latest 2026-06-10.
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) VALUES ({milk}, {store}, 'receipt', '2026-06-01', '4.99', 'each')");
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) VALUES ({milk}, {store}, 'receipt', '2026-06-15', '4.49', 'each')");
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) VALUES ({milk2}, {store}, 'receipt', '2026-06-10', '5.49', 'each')");

        var rows = ItemsAdminRepo.SearchItems(db.Conn, "milk", 50);

        Assert.Equal(2, rows.Count);
        var mRow = rows.Single(r => r.CanonicalName == "milk");
        var m2Row = rows.Single(r => r.CanonicalName == "2% milk");
        Assert.Equal(2, mRow.PricePoints);
        Assert.Equal("2026-06-15", mRow.LastPriceDate);
        Assert.Equal(1, m2Row.PricePoints);
        Assert.Equal("2026-06-10", m2Row.LastPriceDate);
    }

    // Empty search still returns items (bounded by limit), with zero stats for items that have no prices.
    [Fact]
    public void SearchItems_empty_query_returns_items_with_zero_stats_when_no_prices()
    {
        using var db = new TempDb();
        ItemsRepo.CreateItem(db.Conn, "milk");
        ItemsRepo.CreateItem(db.Conn, "bread");

        var rows = ItemsAdminRepo.SearchItems(db.Conn, "", 50);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.PricePoints));
        Assert.All(rows, r => Assert.Null(r.LastPriceDate));
    }

    [Fact]
    public void Merge_repoints_every_item_id_table_and_leaves_no_orphan()
    {
        using var db = new TempDb();
        var (store, receipt, flyer) = Fixtures(db.Conn);
        var target = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;
        SeedRefs(db.Conn, source, store, receipt, flyer, "src-alias");

        Assert.Equal(7, CountItemIdRows(db.Conn, source));

        using (var tx = db.Conn.BeginTransaction())
        {
            ItemsAdminRepo.MergeItems(db.Conn, tx, targetItemId: target, sourceItemId: source);
            tx.Commit();
        }

        Assert.Equal(0, CountItemIdRows(db.Conn, source));          // no dangling references
        Assert.Null(ItemsRepo.GetItemById(db.Conn, source));        // source item gone
        Assert.NotNull(ItemsRepo.GetItemById(db.Conn, target));
        // 7 seeded rows moved; the source name is now also an alias of the target (+1 alias row).
        Assert.Equal(8, CountItemIdRows(db.Conn, target));
        Assert.Equal(target, new ItemAliasesRepo().GetByAlias(db.Conn, "2% milk")!.ItemId);
    }

    [Fact]
    public void Merge_promotes_tracked_and_default_unit_to_the_target()
    {
        using var db = new TempDb();
        var target = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;
        Exec(db.Conn, $"UPDATE items SET is_tracked = 0 WHERE id = {target}");
        Exec(db.Conn, $"UPDATE items SET is_tracked = 1 WHERE id = {source}");
        Exec(db.Conn, $"UPDATE items SET default_unit = 'lb' WHERE id = {source}");

        using (var tx = db.Conn.BeginTransaction())
        {
            ItemsAdminRepo.MergeItems(db.Conn, tx, target, source);
            tx.Commit();
        }

        var merged = ItemsRepo.GetItemById(db.Conn, target)!;
        Assert.True(merged.IsTracked);
        Assert.Equal("lb", merged.DefaultUnit);
    }

    [Fact]
    public void Merge_dedupes_the_watchlist_keeping_one_active_watch()
    {
        using var db = new TempDb();
        var target = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;
        Exec(db.Conn, $"INSERT INTO watchlist (item_id) VALUES ({target})");
        Exec(db.Conn, $"INSERT INTO watchlist (item_id) VALUES ({source})");

        using (var tx = db.Conn.BeginTransaction())
        {
            ItemsAdminRepo.MergeItems(db.Conn, tx, target, source);
            tx.Commit();
        }

        Assert.Equal(1, Convert.ToInt32(ExecScalar(db.Conn, $"SELECT COUNT(*) FROM watchlist WHERE item_id = {target}")));
    }

    // Schema-drift guard: EVERY table with an item_id column in the live schema must be covered by the
    // merge repointing (ItemsAdminRepo.ItemIdTables) or the watchlist special-case. A new item_id table
    // added to neither silently orphans its rows on MergeItems — this fails the moment such a table lands.
    [Fact]
    public void ItemIdTables_covers_every_item_id_column_in_the_live_schema()
    {
        using var db = new TempDb();
        var inSchema = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) p " +
                "WHERE m.type = 'table' AND p.name = 'item_id'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) inSchema.Add(r.GetString(0));
        }

        // Source of truth = the production list (repointed on merge) + the watchlist special-case.
        var known = new HashSet<string>(ItemsAdminRepo.ItemIdTables, StringComparer.Ordinal) { "watchlist" };

        Assert.True(known.SetEquals(inSchema),
            $"item_id table drift — schema=[{string.Join(", ", inSchema.OrderBy(x => x))}] " +
            $"known=[{string.Join(", ", known.OrderBy(x => x))}]");
    }

    [Fact]
    public void Merge_is_atomic_a_rollback_reverts_every_table()
    {
        using var db = new TempDb();
        var (store, receipt, flyer) = Fixtures(db.Conn);
        var target = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;
        SeedRefs(db.Conn, source, store, receipt, flyer, "src-alias");

        using (var tx = db.Conn.BeginTransaction())
        {
            ItemsAdminRepo.MergeItems(db.Conn, tx, target, source);
            tx.Rollback(); // simulate a failure before commit
        }

        Assert.Equal(7, CountItemIdRows(db.Conn, source));   // everything reverted
        Assert.Equal(0, CountItemIdRows(db.Conn, target));
        Assert.NotNull(ItemsRepo.GetItemById(db.Conn, source));
    }

    [Fact]
    public void Rename_to_an_existing_name_throws_a_clear_error()
    {
        using var db = new TempDb();
        ItemsRepo.CreateItem(db.Conn, "milk");
        var other = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;

        var ex = Assert.Throws<InvalidOperationException>(() => ItemsAdminRepo.RenameItem(db.Conn, other, "milk"));
        Assert.Contains("already exists", ex.Message);
        Assert.Equal("2% milk", ItemsRepo.GetItemById(db.Conn, other)!.CanonicalName); // unchanged
    }

}
