using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Xunit;

namespace GrocerySense.Tests;

public sealed class FlyersRepoTests
{
    private static readonly string Yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
    private static readonly string Tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
    private static readonly string LastWeek = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");

    [Fact]
    public void UpsertStore_dedupes_by_name()
    {
        using var db = new TempDb();
        var repo = new FlyersRepo();
        var a = repo.UpsertStore(db.Conn, "Walmart");
        var b = repo.UpsertStore(db.Conn, "Walmart");
        Assert.Equal(a, b);
        Assert.Single(repo.ListStores(db.Conn));
    }

    [Fact]
    public void Batch_assets_rawjson_and_deals_round_trip_money()
    {
        using var db = new TempDb();
        var repo = new FlyersRepo();
        var storeId = repo.UpsertStore(db.Conn, "Loblaws");
        var batch = repo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow, sourceType: "manual_upload");
        repo.AddAsset(db.Conn, batch, "image", "/tmp/flyer.png", sha256: "abc");
        repo.AddRawJson(db.Conn, batch, "{\"pages\":1}", sha256: "def");

        repo.AddDeals(db.Conn, new[]
        {
            new FlyerDeal(0, batch, null, storeId, 1, "Cheddar", "400g block", "$3.99",
                DealQty: 1, DealTotal: 3.99m, UnitPrice: 9.98m, Unit: "kg",
                NormUnitPrice: 9.98m, NormUnit: "kg", NormNote: null,
                ItemId: null, MappingConfidence: 0.8, Confidence: 0.9, CreatedAt: null),
        });

        var deals = repo.ListDealsForFlyer(db.Conn, batch);
        var deal = Assert.Single(deals);
        Assert.Equal(3.99m, deal.DealTotal);
        Assert.Equal(9.98m, deal.UnitPrice);
        Assert.Equal("Cheddar", deal.Title);
    }

    [Fact]
    public void ListActiveDeals_filters_by_status_and_validity()
    {
        using var db = new TempDb();
        var repo = new FlyersRepo();
        var storeId = repo.UpsertStore(db.Conn, "Sobeys");

        var live = repo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow);
        repo.AddDeals(db.Conn, new[] { Deal(live, storeId, "Live") });

        var expired = repo.CreateFlyerBatch(db.Conn, storeId, LastWeek, Yesterday);
        repo.AddDeals(db.Conn, new[] { Deal(expired, storeId, "Expired") });

        var archived = repo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow);
        repo.AddDeals(db.Conn, new[] { Deal(archived, storeId, "Archived") });
        repo.SetBatchStatus(db.Conn, archived, "archived");

        var active = repo.ListActiveDeals(db.Conn);
        Assert.Single(active);
        Assert.Equal("Live", active[0].Title);
    }

    [Fact]
    public void ListActiveDeals_empty_store_filter_returns_none()
    {
        using var db = new TempDb();
        var repo = new FlyersRepo();
        var storeId = repo.UpsertStore(db.Conn, "Sobeys");
        var batch = repo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow);
        repo.AddDeals(db.Conn, new[] { Deal(batch, storeId, "Live") });

        Assert.Empty(repo.ListActiveDeals(db.Conn, storeIds: Array.Empty<int>()));
    }

    private static FlyerDeal Deal(int flyerId, int storeId, string title) =>
        new(0, flyerId, null, storeId, null, title, null, null,
            null, null, null, null, null, null, null, null, null, null, null);
}
