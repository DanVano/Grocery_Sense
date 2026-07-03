using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ItemsAdminRepoTests
{
    // Every item_id-bearing table (must match ItemsAdminRepo.ItemIdTables + watchlist).
    private static readonly string[] ItemIdTables =
    {
        "prices", "receipt_line_items", "shopping_list", "flyer_deals", "price_drop_alerts",
        "item_aliases", "watchlist",
    };

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

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

    private static object ExecScalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
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
        ItemsRepo.SetItemTracked(db.Conn, target, false);
        ItemsRepo.SetItemTracked(db.Conn, source, true);
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

    [Fact]
    public void CorrectLineMapping_repoints_the_line_and_its_price_and_learns_the_alias()
    {
        using var db = new TempDb();
        var (store, receipt, _) = Fixtures(db.Conn);
        var wrong = ItemsRepo.CreateItem(db.Conn, "butter").Id;
        var right = ItemsRepo.CreateItem(db.Conn, "margarine").Id;
        Exec(db.Conn, $"INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description) " +
                      $"VALUES ({receipt}, 0, {wrong}, 'margerine tub')");
        var lineId = (int)(long)ExecScalar(db.Conn, "SELECT last_insert_rowid()");
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, receipt_id, source, date, unit_price, unit, raw_name) " +
                      $"VALUES ({wrong}, {store}, {receipt}, 'receipt', '2026-06-01', '3.49', 'each', 'margerine tub')");

        using (var tx = db.Conn.BeginTransaction())
        {
            ItemsAdminRepo.CorrectLineMapping(db.Conn, tx, lineId, receipt, "margerine tub",
                oldItemId: wrong, newItemId: right);
            tx.Commit();
        }

        Assert.Equal(0, CountItemIdRows(db.Conn, wrong));
        Assert.Equal(right, new ItemAliasesRepo().GetByAlias(db.Conn, "margerine tub")!.ItemId);
        Assert.Equal(right, Convert.ToInt32(ExecScalar(db.Conn,
            $"SELECT item_id FROM prices WHERE receipt_id = {receipt} AND raw_name = 'margerine tub'")));
    }
}
