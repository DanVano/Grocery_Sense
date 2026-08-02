using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class WatchlistServiceTests : TempDirTestBase
{


    private WatchlistService Build(TempDb db) => new(db.Factory, new ConfigStore(_dir));

    private static void Price(TempDb db, int itemId, int storeId, double price) =>
        PricesRepo.AddPricePoint(db.Conn, itemId, storeId, price, "each", source: "manual", date: Today);

    [Fact]
    public void Add_dedupes_per_item_and_remove_is_soft()
    {
        using var db = new TempDb();
        var svc = Build(db);
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;

        var id1 = svc.AddWatch(eggs, 3.00);
        var id2 = svc.AddWatch(eggs, 2.50); // same item -> updates, does not stack
        Assert.Equal(id1, id2);

        var watches = svc.ListWatches();
        Assert.Single(watches);
        Assert.Equal(2.50, watches[0].TargetPrice);

        svc.RemoveWatch(id1);
        Assert.Empty(svc.ListWatches());
    }

    [Fact]
    public void Target_price_hit_is_reported()
    {
        using var db = new TempDb();
        var svc = Build(db);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;
        Price(db, eggs, store, 2.50);
        svc.AddWatch(eggs, targetPrice: 3.00);

        var hit = Assert.Single(svc.ComputeHits());
        Assert.Equal("target", hit.HitReason);
        Assert.Equal(2.50, hit.BestPrice);
        Assert.Equal(store, hit.StoreId);
    }

    [Fact]
    public void No_target_hits_when_price_is_below_usual()
    {
        using var db = new TempDb();
        var svc = Build(db);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;
        // usual ~10; most-recent price 8 (~20% below) -> below_usual hit against the 10% default margin.
        Price(db, eggs, store, 10); Price(db, eggs, store, 10); Price(db, eggs, store, 10);
        Price(db, eggs, store, 8);
        svc.AddWatch(eggs, targetPrice: null);

        var hit = Assert.Single(svc.ComputeHits());
        Assert.Equal("below_usual", hit.HitReason);
        Assert.Equal(8, hit.BestPrice);
        Assert.True(hit.PctBelowUsual > 0);
    }

    [Fact]
    public void No_hit_when_price_above_target()
    {
        using var db = new TempDb();
        var svc = Build(db);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;
        Price(db, eggs, store, 2.50);
        svc.AddWatch(eggs, targetPrice: 2.00); // best 2.50 is above the 2.00 target

        Assert.Empty(svc.ComputeHits());
    }
}
