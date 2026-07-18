using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class PriceHistoryServiceTests
{
    private static int Store(TempDb db, string name = "Loblaws") => StoresRepo.CreateStore(db.Conn, name).Id;

    [Fact]
    public void Record_then_GetBaselinePrices_aggregates_case_insensitively()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);

        svc.RecordPriceFromReceipt("Milk", store, 3.00, "each");
        svc.RecordPriceFromReceipt("Milk", store, 5.00, "each");

        // Batched baseline (the API MealSuggestionService uses) = trailing-window average, keyed by the
        // trimmed input; lookup is case-insensitive (recorded "Milk", queried "milk"). Repo-level min/max/count
        // is covered by PricesRepoTests.
        var baselines = svc.GetBaselinePrices(new[] { "milk" });
        Assert.Equal(4.00, baselines["milk"]!.Value);
    }

    [Fact]
    public void ClassifyDeal_thresholds()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);
        foreach (var _ in new[] { 1, 2, 3 }) svc.RecordPriceFromReceipt("Eggs", store, 10.00, "each"); // avg 10, n=3

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
        svc.RecordPriceFromReceipt("Bread", store, 2.00, "each");
        svc.RecordPriceFromReceipt("Bread", store, 4.00, "each"); // n=2

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
        foreach (var _ in new[] { 1, 2, 3 }) svc.RecordPriceFromReceipt("Eggs", store, 10.00, "each"); // adj avg 10 (all today)

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
        svc.RecordPriceFromReceipt("Butter", store, 5.00, "each", "2025-07-01");
        svc.RecordPriceFromReceipt("Butter", store, 5.00, "each", "2025-07-01");

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
        svc.RecordPriceFromReceipt("Flour", store, 6.00, "each", "2025-01-01");
        svc.RecordPriceFromReceipt("Flour", store, 6.00, "each", "2026-01-01"); // one point per year

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
        var item = svc.GetOrCreateItem("Cheese");
        PricesRepo.AddPricePoint(db.Conn, item.Id, store, 8.00, "each"); // dated today
        PricesRepo.AddPricePoint(db.Conn, item.Id, store, 8.00, "each");
        PricesRepo.AddPricePoint(db.Conn, item.Id, store, 8.00, "each");
        PricesRepo.AddPricePoint(db.Conn, item.Id, store, 999.00, "each", date: "garbage-date"); // unparseable

        var c = svc.ClassifyDeal("Cheese", 6.00);
        Assert.Equal(3, c.SampleCount);   // the garbage-dated point is excluded
        Assert.Equal(8.00, c.MaxUnitPrice); // and never pollutes the nominal range
    }
}
