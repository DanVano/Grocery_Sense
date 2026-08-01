using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class PricesRepoTests
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static (int item, int store) Seed(TempDb db, string item = "Milk", string store = "Loblaws")
        => (ItemsRepo.CreateItem(db.Conn, item).Id, StoresRepo.CreateStore(db.Conn, store).Id);

    [Fact]
    public void AddPricePoint_then_get_most_recent_round_trips()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        PricesRepo.AddPricePoint(db.Conn, item, store, 3.49, "each", source: "receipt", date: DaysAgo(2));
        PricesRepo.AddPricePoint(db.Conn, item, store, 4.99, "each", source: "receipt", date: Today);

        var latest = PricesRepo.GetMostRecentPricesGlobalBatch(db.Conn, new[] { item })[item];
        Assert.Equal(4.99, latest.UnitPrice);
        Assert.Equal("each", latest.Unit);
        Assert.Equal("receipt", latest.Source);
    }

    [Fact]
    public void SixMonthLow_uses_numeric_not_lexical_ordering()
    {
        // Lexically "100.0" < "10.0" < "9.5"; numerically 9.5 is the min. Guards the CAST(... AS REAL).
        using var db = new TempDb();
        var (item, store) = Seed(db);
        foreach (var p in new[] { 9.5, 10.0, 100.0 })
            PricesRepo.AddPricePoint(db.Conn, item, store, p, "each", source: "receipt", date: DaysAgo(5));

        var (price, when) = PricesRepo.GetSixMonthLowBatch(db.Conn, new[] { item })[item];
        Assert.Equal(9.5, price);
        Assert.NotNull(when);
    }

    [Fact]
    public void LastReceiptPurchaseBatch_returns_latest_receipt_date_and_skips_manual_only_items()
    {
        using var db = new TempDb();
        var (bought, store) = Seed(db, "Rice");
        var manualOnly = ItemsRepo.CreateItem(db.Conn, "Saffron").Id;
        PricesRepo.AddPricePoint(db.Conn, bought, store, 3.0, "each", source: "receipt", date: DaysAgo(20));
        PricesRepo.AddPricePoint(db.Conn, bought, store, 3.0, "each", source: "receipt", date: DaysAgo(5));
        PricesRepo.AddPricePoint(db.Conn, manualOnly, store, 9.0, "each", source: "manual", date: DaysAgo(2));

        var map = PricesRepo.GetLastReceiptPurchaseBatch(db.Conn, new[] { bought, manualOnly });

        Assert.Equal(DaysAgo(5), map[bought]);
        Assert.False(map.ContainsKey(manualOnly));
    }

    [Fact]
    public void GetPricesForItemsBatch_returns_requested_items_oldest_first_and_excludes_others()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var a = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        var b = ItemsRepo.CreateItem(db.Conn, "Bread").Id;
        var noHistory = ItemsRepo.CreateItem(db.Conn, "Salt").Id;
        var other = ItemsRepo.CreateItem(db.Conn, "Eggs").Id;

        PricesRepo.AddPricePoint(db.Conn, a, store, 4.99, "each", source: "receipt", date: DaysAgo(10));
        PricesRepo.AddPricePoint(db.Conn, a, store, 4.49, "each", source: "receipt", date: DaysAgo(2));
        PricesRepo.AddPricePoint(db.Conn, b, store, 2.50, "each", source: "receipt", date: DaysAgo(5));
        PricesRepo.AddPricePoint(db.Conn, other, store, 3.00, "each", source: "receipt", date: DaysAgo(1));

        var map = PricesRepo.GetPricesForItemsBatch(db.Conn, new[] { a, b, noHistory });

        Assert.Equal(new[] { a, b, noHistory }.OrderBy(x => x), map.Keys.OrderBy(x => x));
        Assert.False(map.ContainsKey(other));                    // unrelated item never fetched
        Assert.Equal(new[] { DaysAgo(10), DaysAgo(2) }, map[a].Select(p => p.Date).ToArray()); // oldest-first
        Assert.Single(map[b]);
        Assert.Empty(map[noHistory]);                            // requested but priceless -> empty, not missing
    }

    [Fact]
    public void PriceStats_min_max_avg_are_numeric()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        foreach (var p in new[] { 9.5, 10.0, 100.0 })
            PricesRepo.AddPricePoint(db.Conn, item, store, p, "each", source: "receipt", date: DaysAgo(3));

        var stats = PricesRepo.GetPriceStatsForItem(db.Conn, item);
        Assert.Equal(3, stats.Count);
        Assert.Equal(9.5, stats.MinPrice);
        Assert.Equal(100.0, stats.MaxPrice);
        Assert.Equal((9.5 + 10.0 + 100.0) / 3.0, stats.AvgPrice!.Value, 6);
    }

    [Fact]
    public void UsualUnitPrice_basis_switches_on_sample_count()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);

        // Empty -> unknown.
        Assert.Equal((null, 0, "unknown"), PricesRepo.GetUsualUnitPrice(db.Conn, item, store));

        // 2 receipt samples (< minSamples 4) -> estimated_median fallback over all rows.
        PricesRepo.AddPricePoint(db.Conn, item, store, 2.0, "each", source: "receipt", date: DaysAgo(10));
        PricesRepo.AddPricePoint(db.Conn, item, store, 4.0, "each", source: "receipt", date: DaysAgo(8));
        var (p2, n2, b2) = PricesRepo.GetUsualUnitPrice(db.Conn, item, store);
        Assert.Equal("estimated_median", b2);
        Assert.Equal(3.0, p2);
        Assert.Equal(2, n2);

        // 4 receipt samples -> receipt_median.
        PricesRepo.AddPricePoint(db.Conn, item, store, 6.0, "each", source: "receipt", date: DaysAgo(6));
        PricesRepo.AddPricePoint(db.Conn, item, store, 8.0, "each", source: "receipt", date: DaysAgo(4));
        var (p4, n4, b4) = PricesRepo.GetUsualUnitPrice(db.Conn, item, store);
        Assert.Equal("receipt_median", b4);
        Assert.Equal(5.0, p4); // median of 2,4,6,8
        Assert.Equal(4, n4);
    }

    [Fact]
    public void UsualUnitPriceBatch_receiptOnly_false_reports_all_samples_when_unknown()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        PricesRepo.AddPricePoint(db.Conn, item, store, 1.99, "each", source: "flyer", date: DaysAgo(1));

        var batch = PricesRepo.GetUsualUnitPriceBatch(db.Conn, new[] { item }, receiptOnly: false);

        Assert.Equal((null, 1, "unknown"), batch[item]);
    }

    [Fact]
    public void MostRecentPricesByStoreBatch_picks_latest_per_pair()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Eggs").Id;
        var s1 = StoresRepo.CreateStore(db.Conn, "A").Id;
        var s2 = StoresRepo.CreateStore(db.Conn, "B").Id;

        PricesRepo.AddPricePoint(db.Conn, item, s1, 3.00, "each", source: "receipt", date: DaysAgo(9));
        PricesRepo.AddPricePoint(db.Conn, item, s1, 3.50, "each", source: "receipt", date: DaysAgo(1));
        PricesRepo.AddPricePoint(db.Conn, item, s2, 2.75, "each", source: "receipt", date: DaysAgo(2));

        var batch = PricesRepo.GetMostRecentPricesByStoreBatch(db.Conn, new[] { item }, new[] { s1, s2 });
        Assert.Equal(3.50, batch[(item, s1)].UnitPrice);
        Assert.Equal(2.75, batch[(item, s2)].UnitPrice);
    }

    [Fact]
    public void RecentAvgByStoreBatch_limits_to_most_recent_n()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        // Oldest -> newest; with limit 2 only the two newest (10, 12) count -> avg 11.
        PricesRepo.AddPricePoint(db.Conn, item, store, 2.0, "each", source: "receipt", date: DaysAgo(9));
        PricesRepo.AddPricePoint(db.Conn, item, store, 10.0, "each", source: "receipt", date: DaysAgo(5));
        PricesRepo.AddPricePoint(db.Conn, item, store, 12.0, "each", source: "receipt", date: DaysAgo(1));

        var avg = PricesRepo.GetRecentAvgUnitPriceByStoreBatch(db.Conn, new[] { item }, new[] { store }, limit: 2);
        Assert.Equal(11.0, avg[(item, store)], 6);
    }

    [Fact]
    public void PurchaseCadenceBatch_computes_interval_and_qty()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        // Two receipts 10 days apart, qty 1 and 3 -> interval 10, typical qty 2.
        AddReceiptRow(db.Conn, item, store, 3.0, qty: 1, date: DaysAgo(10), receiptId: AddReceipt(db.Conn, store, DaysAgo(10)));
        AddReceiptRow(db.Conn, item, store, 3.0, qty: 3, date: Today, receiptId: AddReceipt(db.Conn, store, Today));

        var cadence = PricesRepo.GetPurchaseCadenceBatch(db.Conn, new[] { item });
        var (interval, qty) = cadence[item];
        Assert.Equal(10.0, interval);
        Assert.Equal(2.0, qty);
    }

    // ---- GetActiveFlyerPricesBatch reads flyer_deals/flyer_batches (the populated family) ----

    private static int MakeBatch(TempDb db, int store, string? from, string? to, string status = "active")
        => FlyersRepo.CreateFlyerBatch(db.Conn, store, from, to, status: status);

    private static void MakeDeal(TempDb db, int flyerId, int store, int? item, decimal? unitPrice,
        decimal? normUnitPrice = null, string? normUnit = null)
        => FlyersRepo.AddDeals(db.Conn, new[] { new GrocerySense.Domain.FlyerDeal(
            Id: 0, FlyerId: flyerId, AssetId: null, StoreId: store, PageIndex: null,
            Title: "t", Description: null, PriceText: null, DealQty: null, DealTotal: null,
            UnitPrice: unitPrice, Unit: "each", NormUnitPrice: normUnitPrice, NormUnit: normUnit,
            NormNote: null, ItemId: item, MappingConfidence: null, Confidence: null, CreatedAt: null) });

    [Fact]
    public void ActiveFlyerPricesBatch_returns_min_active_deal_and_skips_inactive_expired_unmapped()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);

        var active = MakeBatch(db, store, DaysAgo(2), DaysAgo(-3));
        MakeDeal(db, active, store, item, 2.99m);
        MakeDeal(db, active, store, item, 2.49m);           // cheaper -> wins via MIN
        MakeDeal(db, active, store, null, 0.99m);           // unmapped: excluded
        var expired = MakeBatch(db, store, DaysAgo(20), DaysAgo(10));
        MakeDeal(db, expired, store, item, 0.50m);          // expired window: excluded
        var archived = MakeBatch(db, store, DaysAgo(2), DaysAgo(-3), status: "archived");
        MakeDeal(db, archived, store, item, 0.25m);         // inactive batch: excluded

        var map = PricesRepo.GetActiveFlyerPricesBatch(db.Conn, new[] { item }, new[] { store });

        var quote = map[(item, store)];
        Assert.Equal(2.49, quote.UnitPrice);
        Assert.Equal("flyer", quote.Source);
    }

    [Fact]
    public void ActiveFlyerPricesBatch_null_validity_is_open_ended_and_norm_price_preferred()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        var batch = MakeBatch(db, store, from: null, to: null); // NULL = open-ended, matches ListActiveDeals
        MakeDeal(db, batch, store, item, 4.00m, normUnitPrice: 0.40m, normUnit: "per_100g");

        var map = PricesRepo.GetActiveFlyerPricesBatch(db.Conn, new[] { item }, new[] { store });

        var quote = map[(item, store)];
        Assert.Equal(0.40, quote.UnitPrice);
        Assert.Equal("per_100g", quote.Unit);
    }

    [Fact]
    public void ActiveFlyerPricesBatch_excludes_zero_priced_deals()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        var batch = MakeBatch(db, store, DaysAgo(1), DaysAgo(-6));
        MakeDeal(db, batch, store, item, 0m); // "FREE"/parse-failure rows must not become a $0 quote

        Assert.Empty(PricesRepo.GetActiveFlyerPricesBatch(db.Conn, new[] { item }, new[] { store }));
    }

    [Fact]
    public void LastSeenAtOrBelow_returns_most_recent_under_ceiling()
    {
        using var db = new TempDb();
        var (item, store) = Seed(db);
        PricesRepo.AddPricePoint(db.Conn, item, store, 5.00, "each", source: "receipt", date: DaysAgo(20));
        PricesRepo.AddPricePoint(db.Conn, item, store, 4.00, "each", source: "receipt", date: DaysAgo(10));
        PricesRepo.AddPricePoint(db.Conn, item, store, 9.00, "each", source: "receipt", date: DaysAgo(1));

        // Ceiling 4.50: rows at/below are the 4.00 (d-10) and 5.00 is above; newest qualifying is d-10.
        var map = PricesRepo.GetLastSeenAtOrBelowBatch(db.Conn, new Dictionary<int, double> { [item] = 4.50 });
        Assert.Equal(DaysAgo(10), map[item]);
    }

    private static void AddReceiptRow(SqliteConnection conn, int item, int store, double price, double qty,
        string date, int receiptId)
        => PricesRepo.AddPricePoint(conn, item, store, price, "each", quantity: qty, source: "receipt",
            date: date, receiptId: receiptId);
}
