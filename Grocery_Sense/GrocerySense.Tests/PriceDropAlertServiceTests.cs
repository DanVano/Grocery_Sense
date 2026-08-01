using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class PriceDropAlertServiceTests
{

    // Staple item: 4 receipts @ $10 (the usual), plus a recent $7 (30% below). Returns (item, store).
    private static (int Item, int Store) SeedStapleWithDrop(TempDb db)
    {
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        foreach (var d in new[] { 40, 30, 20, 10 })
        {
            var rid = AddReceipt(db.Conn, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, 10.0, "each", source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        var recent = AddReceipt(db.Conn, store, DaysAgo(0));
        PricesRepo.AddPricePoint(db.Conn, item, store, 7.0, "each", source: "receipt", date: DaysAgo(0), receiptId: recent);
        return (item, store);
    }

    private static (int Item, int Store) SeedNonStapleManualDrop(TempDb db)
    {
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Saffron").Id;
        foreach (var d in new[] { 40, 30, 20 })
            PricesRepo.AddPricePoint(db.Conn, item, store, 10.0, "each", source: "manual", date: DaysAgo(d));
        PricesRepo.AddPricePoint(db.Conn, item, store, 7.0, "each", source: "manual", date: DaysAgo(0));
        return (item, store);
    }

    [Fact]
    public void Engine_emits_below_usual_alert_for_a_staple_drop()
    {
        using var db = new TempDb();
        SeedStapleWithDrop(db);
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Equal(1, svc.RefreshEngineAlerts());

        var a = Assert.Single(svc.GetAlerts());
        Assert.Equal("below_usual", a.AlertKind);
        Assert.Equal(7.0, a.CurrentPrice, 4);
        Assert.Equal(10.0, a.UsualPrice!.Value, 4);
        Assert.Equal(30.0, a.PctBelowUsual!.Value, 1);
        Assert.True(a.IsStaple);
        Assert.Equal("receipt_median", a.Basis);
        Assert.Equal("engine", a.Source);
    }

    [Fact]
    public void Dismiss_suppresses_the_same_alert_on_refresh()
    {
        using var db = new TempDb();
        SeedStapleWithDrop(db);
        var svc = new PriceDropAlertService(db.Factory);
        svc.RefreshEngineAlerts();

        svc.DismissAlert(svc.GetAlerts().Single().Id!.Value);

        Assert.Equal(0, svc.RefreshEngineAlerts()); // within the 30-day suppression window
        Assert.Empty(svc.GetAlerts());
    }

    [Fact]
    public void ScanRecentReceipts_opens_one_alert_per_item_store_for_cheap_lines()
    {
        using var db = new TempDb();
        SeedStapleWithDrop(db);
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Equal(1, svc.ScanRecentReceipts(21));

        var a = Assert.Single(svc.GetAlerts(0));
        Assert.Equal("below_usual", a.AlertKind);
        Assert.Equal("receipt", a.Source);
        Assert.Equal(7.0, a.CurrentPrice, 4);
    }

    [Fact]
    public void ScanRecentReceipts_does_not_duplicate_existing_open_receipt_alert()
    {
        using var db = new TempDb();
        SeedStapleWithDrop(db);
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Equal(1, svc.ScanRecentReceipts(21));
        Assert.Equal(0, svc.ScanRecentReceipts(21));

        Assert.Single(svc.GetAlerts(0));
    }

    [Fact]
    public void StaplesOnly_false_scans_non_staple_tracked_items()
    {
        using var db = new TempDb();
        SeedNonStapleManualDrop(db);
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Empty(svc.ComputeEngineAlerts(staplesOnly: true));

        var alert = Assert.Single(svc.ComputeEngineAlerts(staplesOnly: false));
        Assert.False(alert.IsStaple);
        Assert.Equal("below_usual", alert.AlertKind);
    }

    [Fact]
    public void No_stores_yields_no_alerts()
    {
        using var db = new TempDb();
        Assert.Empty(new PriceDropAlertService(db.Factory).ComputeEngineAlerts());
    }

    // Stock-up suggested qty is persisted (migration 6) and survives the write -> read-back round-trip.
    // Setup: usual $10, latest price $7 = the 6-month low, last seen 40 days ago (past the 30-day cooldown);
    // cadence = 4 receipts over 60 days (interval 20d, qty 1) -> 28-day horizon suggests 1.
    [Fact]
    public void Stockup_suggested_qty_survives_persist_and_read_back()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Oats").Id;
        foreach (var (d, price) in new[] { (100, 10.0), (80, 10.0), (60, 10.0), (40, 7.0) })
        {
            var rid = AddReceipt(db.Conn, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, price, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Equal(1, svc.RefreshEngineAlerts());

        var a = Assert.Single(svc.GetAlerts());
        Assert.Equal("both", a.AlertKind);
        Assert.Equal(1.0, a.SuggestedQty!.Value, 4);
        Assert.Contains("week", a.SuggestedQtyNote);
    }

    // Split-brain regression (flyer unification): a synced flyer_deals row is the "current price" the
    // engine compares against usual — proves the SyncCompleted -> RefreshEngineAlerts hook has data to see.
    [Fact]
    public void Flyer_deal_below_usual_produces_engine_alert()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        foreach (var d in new[] { 40, 30, 20, 10 }) // staple: steady $10 usual, no receipt-side drop
        {
            var rid = AddReceipt(db.Conn, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, 10.0, "each", source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        var flyers = new FlyersRepo();
        var flyerId = flyers.CreateFlyerBatch(db.Conn, store, DaysAgo(1), DaysAgo(-6));
        flyers.AddDeals(db.Conn, new[] { new GrocerySense.Domain.FlyerDeal(
            Id: 0, FlyerId: flyerId, AssetId: null, StoreId: store, PageIndex: null,
            Title: "Milk", Description: null, PriceText: null, DealQty: null, DealTotal: null,
            UnitPrice: 7.0m, Unit: "each", NormUnitPrice: null, NormUnit: null, NormNote: null,
            ItemId: item, MappingConfidence: null, Confidence: null, CreatedAt: null) });
        var svc = new PriceDropAlertService(db.Factory);

        Assert.Equal(1, svc.RefreshEngineAlerts());

        // $7 exists only in flyer_deals (receipts are all $10) — the alert firing at 7.0 proves the
        // engine read the flyer table. Persisted Source is the alert origin ("engine"), not the quote source.
        var a = Assert.Single(svc.GetAlerts());
        Assert.Equal(7.0, a.CurrentPrice);
        Assert.Equal(30.0, a.PctBelowUsual!.Value, 1);
    }
}
