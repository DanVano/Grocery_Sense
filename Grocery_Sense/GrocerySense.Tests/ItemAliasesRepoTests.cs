using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ItemAliasesRepoTests
{
    [Fact]
    public void Upsert_then_get_is_case_insensitive_and_starts_at_one()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "chicken breast");
        var repo = new ItemAliasesRepo();

        repo.UpsertAlias(db.Conn, "BP CHK BRST", item.Id, confidence: 0.95, source: "manual");

        var got = repo.GetByAlias(db.Conn, "bp chk brst");
        Assert.NotNull(got);
        Assert.Equal(item.Id, got!.ItemId);
        Assert.Equal(0.95, got.Confidence);
        Assert.Equal(1, got.TimesSeen);
    }

    [Fact]
    public void Upsert_conflict_increments_times_seen_and_updates()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "rice");
        var repo = new ItemAliasesRepo();

        repo.UpsertAlias(db.Conn, "rice", item.Id);
        repo.UpsertAlias(db.Conn, "rice", item.Id, confidence: 0.5, source: "auto");
        repo.MarkSeen(db.Conn, "rice");

        var got = repo.GetByAlias(db.Conn, "rice")!;
        Assert.Equal(3, got.TimesSeen);     // insert(1) + upsert(+1) + mark_seen(+1)
        Assert.Equal("auto", got.Source);
        Assert.Equal(0.5, got.Confidence);
    }

    [Fact]
    public void ListByItem_orders_by_times_seen_desc()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "milk");
        var repo = new ItemAliasesRepo();
        repo.UpsertAlias(db.Conn, "rare", item.Id);
        repo.UpsertAlias(db.Conn, "common", item.Id);
        repo.MarkSeen(db.Conn, "common");

        var all = repo.ListByItem(db.Conn, item.Id);
        Assert.Equal(2, all.Count);
        Assert.Equal("common", all[0].AliasText); // higher times_seen first
    }
}
