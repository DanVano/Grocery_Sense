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
    public void Batch_assets_rawjson_and_deals_round_trip_money()
    {
        using var db = new TempDb();
        var storeId = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var batch = FlyersRepo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow, sourceType: "manual_upload");
        FlyersRepo.AddAsset(db.Conn, batch, "image", "/tmp/flyer.png", sha256: "abc");
        FlyersRepo.AddRawJson(db.Conn, batch, "{\"pages\":1}", sha256: "def");

        FlyersRepo.AddDeals(db.Conn, new[]
        {
            new FlyerDeal(0, batch, null, storeId, 1, "Cheddar", "400g block", "$3.99",
                DealQty: 1, DealTotal: 3.99m, UnitPrice: 9.98m, Unit: "kg",
                NormUnitPrice: 9.98m, NormUnit: "kg", NormNote: null,
                ItemId: null, MappingConfidence: 0.8, Confidence: 0.9, CreatedAt: null),
        });

        var deals = FlyersRepo.ListActiveDeals(db.Conn);
        var deal = Assert.Single(deals);
        Assert.Equal(3.99m, deal.DealTotal);
        Assert.Equal(9.98m, deal.UnitPrice);
        Assert.Equal("Cheddar", deal.Title);
    }

    [Fact]
    public void ListActiveDeals_filters_by_status_and_validity_and_count_agrees()
    {
        using var db = new TempDb();
        var storeId = StoresRepo.CreateStore(db.Conn, "Sobeys").Id;

        var live = FlyersRepo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow);
        FlyersRepo.AddDeals(db.Conn, new[] { Deal(live, storeId, "Live") });

        var expired = FlyersRepo.CreateFlyerBatch(db.Conn, storeId, LastWeek, Yesterday);
        FlyersRepo.AddDeals(db.Conn, new[] { Deal(expired, storeId, "Expired") });

        var archived = FlyersRepo.CreateFlyerBatch(db.Conn, storeId, Yesterday, Tomorrow, status: "archived");
        FlyersRepo.AddDeals(db.Conn, new[] { Deal(archived, storeId, "Archived") });

        var active = FlyersRepo.ListActiveDeals(db.Conn);
        Assert.Single(active);
        Assert.Equal("Live", active[0].Title);
        // The COUNT twin (Home dashboard) must always agree with the materializing reader.
        Assert.Equal(active.Count, FlyersRepo.CountActiveDeals(db.Conn));
    }

    private static FlyerDeal Deal(int flyerId, int storeId, string title) =>
        new(0, flyerId, null, storeId, null, title, null, null,
            null, null, null, null, null, null, null, null, null, null, null);
}
