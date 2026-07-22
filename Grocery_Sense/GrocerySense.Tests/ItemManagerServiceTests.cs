using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

// ItemManagerService owns the transaction the UI used to drive. The deep merge/correction logic is already
// covered by ItemsAdminRepoTests; these tests prove the service commits it, and that the mapper cache
// invalidation fires — observed through mapping behaviour, not private state.
public sealed class ItemManagerServiceTests
{
    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object ExecScalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }

    [Fact]
    public void MergeItems_commits_the_repoint_and_delete_with_no_caller_transaction()
    {
        using var db = new TempDb();
        var svc = new ItemManagerService(db.Factory, new IngredientMappingService(db.Factory));
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var target = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "2% milk").Id;
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, source, date, unit_price, unit) " +
                      $"VALUES ({source}, {store}, 'receipt', '2026-06-01', '5.49', 'each')");

        svc.MergeItems(target, source);

        Assert.Null(ItemsRepo.GetItemById(db.Conn, source));                    // source gone
        Assert.Equal(0, Convert.ToInt32(ExecScalar(db.Conn,                     // no orphaned price row
            $"SELECT COUNT(*) FROM prices WHERE item_id = {source}")));
        Assert.Equal(1, Convert.ToInt32(ExecScalar(db.Conn,                     // repointed onto target
            $"SELECT COUNT(*) FROM prices WHERE item_id = {target}")));
    }

    [Fact]
    public void MergeItems_invalidates_the_mapper_choice_cache()
    {
        using var db = new TempDb();
        var mapper = new IngredientMappingService(db.Factory);
        var svc = new ItemManagerService(db.Factory, mapper);

        var target = ItemsRepo.CreateItem(db.Conn, "apples").Id;
        var source = ItemsRepo.CreateItem(db.Conn, "apple").Id;

        mapper.MapToItem("apples"); // primes the cached candidate list (knows apples/apple, not the item below)

        var fresh = ItemsRepo.CreateItem(db.Conn, "zzq unique widget").Id;
        // Stale cache can't resolve an item created after priming — proves the cache is real, not per-call.
        Assert.NotEqual(fresh, mapper.MapToItem("zzq unique widget").ItemId ?? -1);

        svc.MergeItems(target, source); // drops the cache

        Assert.Equal(fresh, mapper.MapToItem("zzq unique widget").ItemId);
    }

    [Fact]
    public void CorrectLineMapping_repoints_the_line_and_its_price_and_learns_the_alias()
    {
        using var db = new TempDb();
        var svc = new ItemManagerService(db.Factory, new IngredientMappingService(db.Factory));
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        Exec(db.Conn, $"INSERT INTO receipts (store_id, purchase_date, source) VALUES ({store}, '2026-06-01', 'receipt')");
        var receipt = (int)(long)ExecScalar(db.Conn, "SELECT last_insert_rowid()");
        var wrong = ItemsRepo.CreateItem(db.Conn, "butter").Id;
        var right = ItemsRepo.CreateItem(db.Conn, "margarine").Id;
        Exec(db.Conn, $"INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description) " +
                      $"VALUES ({receipt}, 0, {wrong}, 'margerine tub')");
        var lineId = (int)(long)ExecScalar(db.Conn, "SELECT last_insert_rowid()");
        Exec(db.Conn, $"INSERT INTO prices (item_id, store_id, receipt_id, source, date, unit_price, unit, raw_name) " +
                      $"VALUES ({wrong}, {store}, {receipt}, 'receipt', '2026-06-01', '3.49', 'each', 'margerine tub')");

        svc.CorrectLineMapping(lineId, right);

        Assert.Equal(right, Convert.ToInt32(ExecScalar(db.Conn,
            $"SELECT item_id FROM receipt_line_items WHERE id = {lineId}")));
        Assert.Equal(right, Convert.ToInt32(ExecScalar(db.Conn,
            $"SELECT item_id FROM prices WHERE receipt_id = {receipt} AND raw_name = 'margerine tub'")));
        Assert.Equal(right, new ItemAliasesRepo().GetByAlias(db.Conn, "margerine tub")!.ItemId);
    }
}
