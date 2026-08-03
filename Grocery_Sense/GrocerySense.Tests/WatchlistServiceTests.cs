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

    // The documented reactivate path: RemoveWatch is a soft toggle, so re-adding the item revives the SAME
    // row — new target, is_active back to 1, and the original created_at kept (not rewritten to now).
    [Fact]
    public void Re_add_after_remove_reactivates_the_same_row_keeping_created_at()
    {
        using var db = new TempDb();
        var svc = Build(db);
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;

        var id = svc.AddWatch(eggs, 3.00);
        // Backdate created_at so "kept" is distinguishable from "rewritten to now".
        Exec(db.Conn, $"UPDATE watchlist SET created_at = '2026-01-01 00:00:00' WHERE id = {id}");
        svc.RemoveWatch(id);
        Assert.Empty(svc.ListWatches());

        var readded = svc.AddWatch(eggs, 2.25);

        Assert.Equal(id, readded);                       // same row revived, not a duplicate
        Assert.Equal(1, Count(db.Conn, "watchlist"));
        var watch = Assert.Single(svc.ListWatches());
        Assert.True(watch.IsActive);
        Assert.Equal(2.25, watch.TargetPrice);
        Assert.Equal("2026-01-01 00:00:00", watch.CreatedAt); // original creation time survives the toggle
    }

    // Quote-ladder global fallback, unique to the watchlist: NO shop-here store has any price, so
    // PriceQuoteLadder.GlobalFallback supplies the quote from a non-shop-here store. The hit reports that
    // store's id with the honest "Unknown" name (it is outside the shop-here name map).
    [Fact]
    public void ComputeHits_falls_back_to_a_non_shop_here_store_when_no_shop_here_store_has_a_price()
    {
        using var db = new TempDb();
        var svc = Build(db);
        StoresRepo.CreateStore(db.Conn, "Loblaws");                       // shop-here, but has no prices
        var elsewhere = StoresRepo.CreateStore(db.Conn, "Costco").Id;
        StoresRepo.SetStoreShopHere(db.Conn, elsewhere, false);
        var eggs = ItemsRepo.CreateItem(db.Conn, "eggs").Id;
        Price(db, eggs, elsewhere, 2.50);                                 // only price lives off-roster
        svc.AddWatch(eggs, targetPrice: 3.00);

        var hit = Assert.Single(svc.ComputeHits());
        Assert.Equal("target", hit.HitReason);
        Assert.Equal(2.50, hit.BestPrice);
        Assert.Equal(elsewhere, hit.StoreId);   // the global fallback's store, not a shop-here one
        Assert.Equal("Unknown", hit.StoreName);
    }
}
