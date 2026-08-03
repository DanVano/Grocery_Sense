using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class ScanAlertNotificationServiceTests
{
    private sealed class FakeLocalNotifier : ILocalNotifier
    {
        public bool Result = true;
        public bool Throw; // platform notifier faulted outright (vs Result=false: shown attempt denied)
        public int Calls;
        public string? LastBody;
        public Task<bool> ShowAsync(string title, string body, CancellationToken ct = default)
        {
            Calls++;
            LastBody = body;
            if (Throw) throw new InvalidOperationException("notifier down");
            return Task.FromResult(Result);
        }
    }


    // Establish a receipt-median `usual` (4 receipts) for an item; returns the item id.
    private static int SeedUsual(TempDb db, int store, string name, double usual)
    {
        var item = ItemsRepo.CreateItem(db.Conn, name).Id;
        foreach (var d in new[] { 40, 30, 20, 10 })
        {
            var rid = AddReceipt(db.Conn, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, store, usual, "each", source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        return item;
    }

    // Add one receipt with a single line for `item` at `price`; returns the receipt id (the "scanned" one).
    private static int AddScanReceipt(TempDb db, int store, int item, double price, int daysAgo = 0)
    {
        var rid = AddReceipt(db.Conn, store, DaysAgo(daysAgo));
        PricesRepo.AddPricePoint(db.Conn, item, store, price, "each", source: "receipt", date: DaysAgo(daysAgo), receiptId: rid);
        return rid;
    }

    private static ScanAlertNotificationService Svc(TempDb db, FakeLocalNotifier notifier) =>
        new(new PriceDropAlertService(db.Factory), notifier);

    [Fact]
    public async Task Opens_receipt_scoped_alert_and_notifies_once()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var scanRid = AddScanReceipt(db, store, eggs, 7.0); // 30% below usual
        var notifier = new FakeLocalNotifier();

        var result = await Svc(db, notifier).AfterSingleScanAsync(scanRid);

        Assert.Equal(1, result.Opened);
        Assert.True(result.Notified);
        Assert.Equal(1, notifier.Calls);
        Assert.Contains("1 new price alert", notifier.LastBody);
    }

    [Fact]
    public async Task Repeat_scan_opens_nothing()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var scanRid = AddScanReceipt(db, store, eggs, 7.0);
        var svc = Svc(db, new FakeLocalNotifier());

        Assert.Equal(1, (await svc.AfterSingleScanAsync(scanRid)).Opened);
        var second = await svc.AfterSingleScanAsync(scanRid);
        Assert.Equal(0, second.Opened);      // already open
        Assert.False(second.Notified);
    }

    [Fact]
    public async Task Notifier_false_still_reports_opened()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var scanRid = AddScanReceipt(db, store, eggs, 7.0);
        var notifier = new FakeLocalNotifier { Result = false }; // notifications denied/disabled

        var result = await Svc(db, notifier).AfterSingleScanAsync(scanRid);

        Assert.Equal(1, result.Opened);  // in-app line still shows
        Assert.False(result.Notified);
        Assert.Equal(1, notifier.Calls); // it WAS attempted
    }

    [Fact]
    public async Task Notifier_throw_is_isolated_scan_result_still_returned()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var scanRid = AddScanReceipt(db, store, eggs, 7.0);
        var notifier = new FakeLocalNotifier { Throw = true };

        var result = await Svc(db, notifier).AfterSingleScanAsync(scanRid); // must not rethrow

        Assert.Equal(1, result.Opened);  // the alert still opened; the in-app line still shows it
        Assert.False(result.Notified);
        Assert.Equal(1, notifier.Calls); // it WAS attempted before faulting
    }

    [Fact]
    public async Task Excludes_other_receipts_alerts_no_misattribution()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var milk = SeedUsual(db, store, "Milk", 10.0);
        AddScanReceipt(db, store, milk, 7.0, daysAgo: 2); // recent BACKFILLED drop, NOT the scanned receipt
        var scanEggsRid = AddScanReceipt(db, store, eggs, 7.0, daysAgo: 0);

        var result = await Svc(db, new FakeLocalNotifier()).AfterSingleScanAsync(scanEggsRid);

        Assert.Equal(1, result.Opened); // ONLY the scanned receipt's egg drop (a global scan would open 2)
        var open = new PriceDropAlertService(db.Factory).GetAlerts(0);
        Assert.Equal("Eggs", Assert.Single(open).ItemName);
    }

    [Fact]
    public async Task No_drop_opens_zero_and_does_not_notify()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var eggs = SeedUsual(db, store, "Eggs", 10.0);
        var scanRid = AddScanReceipt(db, store, eggs, 10.0); // priced at usual — no alert
        var notifier = new FakeLocalNotifier();

        var result = await Svc(db, notifier).AfterSingleScanAsync(scanRid);

        Assert.Equal(0, result.Opened);
        Assert.False(result.Notified);
        Assert.Equal(0, notifier.Calls); // notifier untouched when nothing opened
    }
}
