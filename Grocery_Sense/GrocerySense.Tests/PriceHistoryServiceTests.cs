using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class PriceHistoryServiceTests
{
    private static int Store(TempDb db, string name = "Loblaws") => StoresRepo.CreateStore(db.Conn, name).Id;

    // Seed the way production does — straight into items/prices. CreateItem is already get-or-create
    // (case-insensitive dedupe), and AddPricePoint defaults date to today.
    private static int Item(TempDb db, string name) => ItemsRepo.CreateItem(db.Conn, name).Id;

    private static void Price(TempDb db, string itemName, int storeId, double unitPrice, string? date = null) =>
        PricesRepo.AddPricePoint(db.Conn, Item(db, itemName), storeId, unitPrice, "each",
            source: "receipt", date: date);

    [Fact]
    public void GetBaselinePrices_aggregates_case_insensitively()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);

        Price(db, "Milk", store, 3.00);
        Price(db, "Milk", store, 5.00);

        // Batched baseline (the API MealSuggestionService uses) = trailing-window average, keyed by the
        // trimmed input; lookup is case-insensitive (recorded "Milk", queried "milk"). Repo-level min/max/count
        // is covered by PricesRepoTests.
        var baselines = svc.GetBaselinePrices(new[] { "milk" });
        Assert.Equal(4.00, baselines["milk"]!.Value);
    }

    [Fact]
    public void ItemPriceProfile_returns_points_newest_first_with_store_names_and_stats()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        var itemId = Item(db, "Milk");
        Price(db, "Milk", store, 3.00, "2026-07-01");
        Price(db, "Milk", store, 4.00, "2026-07-20");
        Price(db, "Milk", store, 3.50, "2026-07-10");
        Price(db, "Milk", store, 5.00, "2026-07-15");

        var profile = svc.GetItemPriceProfile(itemId);

        Assert.Equal(4, profile.SampleCount);
        Assert.Equal(new[] { "2026-07-20", "2026-07-15", "2026-07-10", "2026-07-01" },
            profile.Points.Select(p => p.Date).ToArray());
        Assert.All(profile.Points, p => Assert.Equal("Loblaws", p.StoreName));
        Assert.Equal(3.00, profile.MinPrice);
        Assert.Equal(5.00, profile.MaxPrice);
        Assert.NotNull(profile.UsualPrice); // 4 receipt samples = enough for a receipt_median
        Assert.Equal("receipt_median", profile.UsualBasis);
    }

    [Fact]
    public void ItemPriceProfile_with_no_history_is_honest_not_fabricated()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var itemId = Item(db, "Never Bought");

        var profile = svc.GetItemPriceProfile(itemId);

        Assert.Empty(profile.Points);
        Assert.Equal(0, profile.SampleCount);
        Assert.Null(profile.UsualPrice);
        Assert.Null(profile.MinPrice);
        Assert.Equal("unknown", profile.UsualBasis);
    }

    [Fact]
    public void ClassifyDeal_thresholds()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        foreach (var _ in new[] { 1, 2, 3 }) Price(db, "Eggs", store, 10.00); // avg 10, n=3

        Assert.Equal("great", svc.ClassifyDeal("Eggs", 7.00).Classification);   // +30%
        Assert.Equal("good", svc.ClassifyDeal("Eggs", 9.00).Classification);    // +10%
        Assert.Equal("typical", svc.ClassifyDeal("Eggs", 10.00).Classification);// 0%
        Assert.Equal("expensive", svc.ClassifyDeal("Eggs", 12.00).Classification); // -20%
    }

    [Fact]
    public void ClassifyDeal_weak_data_under_three_samples_and_no_data_when_unknown()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        Price(db, "Bread", store, 2.00);
        Price(db, "Bread", store, 4.00); // n=2

        Assert.Equal("weak_data", svc.ClassifyDeal("Bread", 1.00).Classification);
        Assert.Equal("no_data", svc.ClassifyDeal("Nonexistent", 1.00).Classification);
        Assert.False(svc.ClassifyDeal("Nonexistent", 1.00).HasHistory);
    }

    [Fact]
    public void ClassifyDeal_uses_new_15_7_bands()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        foreach (var _ in new[] { 1, 2, 3 }) Price(db, "Eggs", store, 10.00); // adj avg 10 (all today)

        // 8% below: "good" under the new >=7 band (would have been "typical" under the old >=10).
        Assert.Equal("good", svc.ClassifyDeal("Eggs", 9.20).Classification);
        // 16% below: "great" under the new >=15 band (would have been "good" under the old >=20).
        Assert.Equal("great", svc.ClassifyDeal("Eggs", 8.40).Classification);
    }

    [Fact]
    public void ClassifyDeal_lifts_old_prices_above_nominal()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory); // null ConfigStore -> InflationRates.Seed
        var store = Store(db);
        // Two ~1-year-old points at 5.00. Nominal average is 5.00; inflation must pull the baseline above it.
        Price(db, "Butter", store, 5.00, "2025-07-01");
        Price(db, "Butter", store, 5.00, "2025-07-01");

        var c = svc.ClassifyDeal("Butter", 5.00);
        Assert.True(c.HasHistory);
        Assert.NotNull(c.AvgUnitPrice);
        Assert.True(c.AvgUnitPrice > 5.00, $"adjusted baseline {c.AvgUnitPrice} should exceed nominal 5.00");
        Assert.Equal(5.00, c.MinUnitPrice); // range stays nominal
    }

    [Fact]
    public void ClassifyDeal_sparse_yearly_data_still_yields_a_baseline()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        Price(db, "Flour", store, 6.00, "2025-01-01");
        Price(db, "Flour", store, 6.00, "2026-01-01"); // one point per year

        var c = svc.ClassifyDeal("Flour", 6.00);
        Assert.True(c.HasHistory);
        Assert.NotNull(c.AvgUnitPrice);
        Assert.Equal("weak_data", c.Classification); // n=2, but a baseline exists — not no_data
    }

    [Fact]
    public void ClassifyDeal_skips_undated_points()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        var itemId = Item(db, "Cheese");
        PricesRepo.AddPricePoint(db.Conn, itemId, store, 8.00, "each"); // dated today
        PricesRepo.AddPricePoint(db.Conn, itemId, store, 8.00, "each");
        PricesRepo.AddPricePoint(db.Conn, itemId, store, 8.00, "each");
        PricesRepo.AddPricePoint(db.Conn, itemId, store, 999.00, "each", date: "garbage-date"); // unparseable

        var c = svc.ClassifyDeal("Cheese", 6.00);
        Assert.Equal(3, c.SampleCount);   // the garbage-dated point is excluded
        Assert.Equal(8.00, c.MaxUnitPrice); // and never pollutes the nominal range
    }
}
