using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class PriceHistoryServiceTests
{
    private static int Store(TempDb db, string name = "Loblaws") => StoresRepo.CreateStore(db.Conn, name).Id;

    [Fact]
    public void Record_then_GetItemStats_aggregates()
    {
        using var db = new TempDb();
        var svc = new PriceHistoryService(db.Factory);
        var store = Store(db);

        svc.RecordPriceFromReceipt("Milk", store, 3.00, "each");
        svc.RecordPriceFromReceipt("Milk", store, 5.00, "each");

        var stats = svc.GetItemStats("milk")!; // case-insensitive lookup
        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(3.00, stats.MinUnitPrice);
        Assert.Equal(5.00, stats.MaxUnitPrice);
        Assert.Equal(4.00, stats.AvgUnitPrice);
        Assert.Equal(4.00, svc.GetBaselinePrice("Milk"));
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
}
