using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ItemsRepoTests
{
    [Fact]
    public void Create_then_get_round_trips()
    {
        using var db = new TempDb();
        var created = ItemsRepo.CreateItem(db.Conn, "Chicken Breast", category: "meat", defaultUnit: "kg",
            typicalPackageSize: 1.5, typicalPackageUnit: "kg", notes: "lean");

        var got = ItemsRepo.GetItemById(db.Conn, created.Id)!;
        Assert.Equal("Chicken Breast", got.CanonicalName);
        Assert.Equal("meat", got.Category);
        Assert.Equal("kg", got.DefaultUnit);
        Assert.Equal(1.5, got.TypicalPackageSize);
        Assert.True(got.IsTracked);
    }

    [Fact]
    public void Create_is_case_insensitive_dedupe()
    {
        using var db = new TempDb();
        var first = ItemsRepo.CreateItem(db.Conn, "Milk");
        var second = ItemsRepo.CreateItem(db.Conn, "milk");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(ItemsRepo.ListItems(db.Conn));
        Assert.NotNull(ItemsRepo.GetItemByName(db.Conn, "MILK"));
    }

    [Fact]
    public void Create_rejects_empty_name()
    {
        using var db = new TempDb();
        Assert.Throws<ArgumentException>(() => ItemsRepo.CreateItem(db.Conn, "   "));
    }

    [Fact]
    public void ListItems_returns_tracked_only()
    {
        using var db = new TempDb();
        var tracked = ItemsRepo.CreateItem(db.Conn, "Apples");
        var untracked = ItemsRepo.CreateItem(db.Conn, "Caviar");
        SetTracked(db.Conn, untracked.Id, tracked: false);

        var only = Assert.Single(ItemsRepo.ListItems(db.Conn));
        Assert.Equal(tracked.Id, only.Id);
    }

    private static void SetTracked(Microsoft.Data.Sqlite.SqliteConnection conn, int itemId, bool tracked)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET is_tracked = $v WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", tracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Batch_readers_return_maps_and_skip_missing()
    {
        using var db = new TempDb();
        var a = ItemsRepo.CreateItem(db.Conn, "Eggs");
        var b = ItemsRepo.CreateItem(db.Conn, "Bread");

        var byId = ItemsRepo.GetItemsByIds(db.Conn, new[] { a.Id, b.Id, 9999 });
        Assert.Equal(2, byId.Count);
        Assert.Equal("Eggs", byId[a.Id].CanonicalName);

        var byName = ItemsRepo.GetItemsByNames(db.Conn, new[] { "EGGS", "missing" });
        Assert.Single(byName);
        Assert.Equal(a.Id, byName["eggs"].Id);
    }
}
