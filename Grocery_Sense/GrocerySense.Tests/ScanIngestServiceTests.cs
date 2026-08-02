using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.OcrFixtures;

namespace GrocerySense.Tests;

// The single-scan coordinator: ingest then the price-alert pass, with partial success. The key case is that
// an alert-step failure (a DB throw before the notifier guard) leaves the receipt imported — the App must
// keep the image, not delete it.
public sealed class ScanIngestServiceTests : TempDirTestBase
{

    private ScanIngestService BuildSeq(TempDb db, params Dictionary<string, object?>?[] raws)
    {
        var ingest = new ReceiptIngestionService(new SeqOcr(new Queue<Dictionary<string, object?>?>(raws)),
            new OcrGate(), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());
        var scanAlerts = new ScanAlertNotificationService(new PriceDropAlertService(db.Factory), new FakeLocalNotifier());
        return new ScanIngestService(ingest, scanAlerts);
    }

    // A Pending batch holding the given copied files (the state MainActivity leaves behind).
    private static PendingSharedReceiptsService PendingBatch(params string[] paths)
    {
        var pending = new PendingSharedReceiptsService();
        Assert.True(pending.TryBeginCopy());
        pending.CompleteCopy(paths, []);
        return pending;
    }

    private sealed class FakeLocalNotifier : ILocalNotifier
    {
        public Task<bool> ShowAsync(string title, string body, CancellationToken ct = default) => Task.FromResult(true);
    }

    private ScanIngestService Build(TempDb db, Dictionary<string, object?> raw)
    {
        var ingest = new ReceiptIngestionService(new FakeOcr(raw), new OcrGate(), db.Factory,
            new IngredientMappingService(db.Factory), new UnitNormalizationService(), new MultiBuyDealService());
        var scanAlerts = new ScanAlertNotificationService(new PriceDropAlertService(db.Factory), new FakeLocalNotifier());
        return new ScanIngestService(ingest, scanAlerts);
    }


    [Fact]
    public async Task Imports_the_receipt_and_runs_the_alert_pass()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));

        var outcome = await svc.IngestScannedFileAsync(WriteFile("receipt-a"), replaceExisting: false);

        Assert.NotNull(outcome.Ingest.ReceiptId);
        Assert.False(outcome.Ingest.WasDuplicate);
        Assert.Null(outcome.AlertError);           // the alert pass ran clean (no drop history → 0 opened)
        Assert.Equal(0, outcome.AlertsOpened);
    }

    [Fact]
    public async Task A_duplicate_skips_the_alert_pass()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));
        var f = WriteFile("same-bytes");

        await svc.IngestScannedFileAsync(f, replaceExisting: false);
        var second = await svc.IngestScannedFileAsync(f, replaceExisting: false);

        Assert.True(second.Ingest.WasDuplicate);
        Assert.Equal("file_hash", second.Ingest.DuplicateReason);
        Assert.Equal(0, second.AlertsOpened);
        Assert.Null(second.AlertError);
    }

    // ---- shared-batch workflow (the orchestration that used to live untestable in Receipts.razor) ----

    [Fact]
    public async Task Shared_batch_imports_every_file_releases_the_batch_and_deletes_nothing()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db,
            Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)),
            Raw("Metro", "2026-06-02", 3.50, ("Bread", 1, 3.50, 3.50)));
        var pending = PendingBatch(WriteFile("a"), WriteFile("b"));
        var deleted = new List<string>();

        var summary = await svc.ImportSharedBatchAsync(pending, replaceExisting: false, deleted.Add);

        Assert.NotNull(summary);
        Assert.Equal(2, summary!.Imported);
        Assert.Equal(0, summary.Duplicates);
        Assert.Empty(deleted);                                  // imported copies keep their images
        Assert.Equal(ShareIntakeState.Idle, pending.State);     // batch released
        Assert.Equal(2, ReceiptsRepo.ListRecentReceipts(db.Conn).Count);
    }

    [Fact]
    public async Task Shared_batch_deletes_duplicate_copies_and_counts_them()
    {
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var svc = BuildSeq(db, raw, raw); // same signature → second is a duplicate
        var dupPath = WriteFile("b-different-bytes");
        var pending = PendingBatch(WriteFile("a"), dupPath);
        var deleted = new List<string>();

        var summary = await svc.ImportSharedBatchAsync(pending, replaceExisting: false, deleted.Add);

        Assert.Equal(1, summary!.Imported);
        Assert.Equal(1, summary.Duplicates);
        Assert.Equal([dupPath], deleted);                       // only the duplicate's copy dies
        Assert.Equal(ShareIntakeState.Idle, pending.State);
    }

    [Fact]
    public async Task Mid_batch_failure_deletes_failing_and_remaining_copies_keeps_the_imported_and_releases()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db,
            Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)),
            null, // second file: OCR throws
            Raw("Metro", "2026-06-03", 2.00, ("Eggs", 1, 2.00, 2.00)));
        var (ok, failing, never) = (WriteFile("ok"), WriteFile("failing"), WriteFile("never-reached"));
        var pending = PendingBatch(ok, failing, never);
        var deleted = new List<string>();

        var summary = await svc.ImportSharedBatchAsync(pending, replaceExisting: false, deleted.Add);

        Assert.Equal(1, summary!.Imported);                     // the pre-failure import stands
        Assert.Equal("OCR unavailable", summary.FailureMessage);
        Assert.False(summary.Cancelled);
        Assert.Equal([failing, never], deleted);                // failing + remaining die; ok is kept
        Assert.Equal(ShareIntakeState.Idle, pending.State);     // released even on failure
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    [Fact]
    public async Task Cancelled_batch_reports_cancelled_and_cleans_the_remaining_copies()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));
        var (a, b) = (WriteFile("a"), WriteFile("b"));
        var pending = PendingBatch(a, b);
        var deleted = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled before the first file

        var summary = await svc.ImportSharedBatchAsync(pending, false, deleted.Add, cts.Token);

        Assert.True(summary!.Cancelled);
        Assert.Equal(0, summary.Imported);
        Assert.Equal([a, b], deleted);
        Assert.Equal(ShareIntakeState.Idle, pending.State);
    }

    [Fact]
    public async Task Unclaimable_batch_returns_null_and_touches_nothing()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db);
        var pending = new PendingSharedReceiptsService(); // Idle — nothing to claim

        Assert.Null(await svc.ImportSharedBatchAsync(pending, false, _ => throw new Exception("must not delete")));
    }

    // The point of C2: AfterSingleScanAsync does DB work (ScanReceipt) that can throw BEFORE its notifier
    // guard. Force that throw (drop the price_drop_alerts table the alert pass always reads, which ingest
    // never writes) and prove the receipt stays imported — the App will keep the image, not delete it.
    [Fact]
    public async Task An_alert_pass_failure_leaves_the_receipt_imported()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));
        using (var cmd = db.Conn.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE price_drop_alerts"; // ingest doesn't touch it; the alert scan does
            cmd.ExecuteNonQuery();
        }

        var outcome = await svc.IngestScannedFileAsync(WriteFile("receipt-b"), replaceExisting: false);

        Assert.NotNull(outcome.Ingest.ReceiptId);                       // committed despite the alert failure
        Assert.False(outcome.Ingest.WasDuplicate);
        Assert.NotNull(outcome.AlertError);                            // failure surfaced, not swallowed silently
        Assert.Equal(0, outcome.AlertsOpened);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));       // the receipt row is really there
    }
}
