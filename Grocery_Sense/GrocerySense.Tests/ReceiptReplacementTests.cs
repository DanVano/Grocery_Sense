using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;
using static GrocerySense.Tests.OcrFixtures;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// P0-1 hardening: replacement is atomic. Prepare only observes duplicate owners; the commit transaction
// re-reads them, backup-deletes exactly the observed owner, and inserts — or leaves everything untouched.
public sealed class ReceiptReplacementTests : TempDirTestBase
{

    private static ReceiptIngestionService Build(TempDb db, IReceiptOcrClient ocr) =>
        new(ocr, new OcrGate(), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());



    private static (long Receipts, long Prices, long Hashes, long Sigs, long Backups) Snapshot(TempDb db) =>
        (Count(db.Conn, "receipts"), Count(db.Conn, "prices"), Count(db.Conn, "receipt_file_hashes"),
         Count(db.Conn, "receipt_signatures"), Count(db.Conn, "deleted_receipt_backups"));

    [Fact]
    public async Task Ocr_failure_during_replace_prepare_leaves_the_original_untouched()
    {
        using var db = new TempDb();
        var f = WriteFile("bytes");
        await Build(db, new FakeOcr(Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99))))
            .IngestReceiptFileAsync(f);
        var before = Snapshot(db);

        await Assert.ThrowsAsync<IOException>(() =>
            Build(db, new ThrowingOcr()).PrepareReceiptFileAsync(f, replaceExisting: true));

        Assert.Equal(before, Snapshot(db));
    }

    [Fact]
    public async Task Backfill_skip_after_replace_prepare_leaves_the_original_untouched()
    {
        // The Receipts.razor:419 landmine: backfill passes the replace toggle; a skip after prepare used to
        // have already deleted the original with nothing written.
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var f = WriteFile("bytes");
        await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(f);
        var before = Snapshot(db);

        var summary = await Build(db, new FakeOcr(raw)).ImportBatchAsync(
            new[] { f }, (_, _) => Task.FromResult<string?>(null), replaceExisting: true);

        Assert.Equal(BatchImportStatus.Skipped, summary.Items[0].Status);
        Assert.Equal(before, Snapshot(db));
    }

    [Fact]
    public async Task Split_file_hash_and_signature_owners_fail_closed_as_conflict()
    {
        using var db = new TempDb();
        var rawA = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var rawB = Raw("Metro", "2026-06-02", 3.50, ("Bread", 1, 3.50, 3.50));
        var fileA = WriteFile("bytes-A");
        await Build(db, new FakeOcr(rawA)).IngestReceiptFileAsync(fileA);          // owns hash(A) + sig(A)
        await Build(db, new FakeOcr(rawB)).IngestReceiptFileAsync(WriteFile("bytes-B")); // owns sig(B)
        var before = Snapshot(db);

        // Same bytes as A (file-hash owner = A) but OCR reads B's header (signature owner = B).
        var prepared = await Build(db, new FakeOcr(rawB)).PrepareReceiptFileAsync(fileA, replaceExisting: true);

        Assert.NotNull(prepared.Duplicate);
        Assert.True(prepared.Duplicate!.ReplaceConflict);
        Assert.Equal(before, Snapshot(db)); // both originals intact, no backup written
    }

    [Fact]
    public async Task Owner_appearing_between_prepare_and_commit_is_a_conflict()
    {
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var svc = Build(db, new FakeOcr(raw));

        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("first-bytes"), replaceExisting: true);
        // A concurrent import lands the same receipt (same signature) before the commit.
        var concurrent = await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(WriteFile("other-bytes"));
        var before = Snapshot(db);

        var outcome = svc.CommitPreparedReceipt(prepared, "2026-06-01");

        Assert.True(outcome.ReplaceConflict);
        Assert.False(outcome.WasDuplicate);
        Assert.Equal(before, Snapshot(db)); // the receipt prepare never observed is untouched
        Assert.NotNull(ReceiptsRepo.GetReceipt(db.Conn, concurrent.ReceiptId!.Value));
    }

    [Fact]
    public async Task Cancellation_immediately_before_commit_begins_no_transaction()
    {
        using var db = new TempDb();
        var svc = Build(db, new FakeOcr(Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99))));
        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("bytes"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => svc.CommitPreparedReceipt(prepared, null, cts.Token));

        Assert.Equal(0L, Count(db.Conn, "receipts"));
    }

    [Fact]
    public async Task Constraint_failure_on_a_replace_surfaces_as_failure_not_duplicate()
    {
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var f = WriteFile("bytes");
        var original = await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(f);
        var before = Snapshot(db);

        var svc = Build(db, new FakeOcr(raw));
        var prepared = await svc.PrepareReceiptFileAsync(f, replaceExisting: true);
        // Force the post-delete insert to fail: a line pointing at a nonexistent item breaks the FK.
        var sabotaged = prepared with
        {
            Ingest = prepared.Ingest! with
            {
                Lines = new[] { new ReceiptIngestLine(0, 999999, "milk", 1, 4.99, 4.99, null, 3, "each", null, null, null) },
            },
        };

        Assert.ThrowsAny<SqliteException>(() => svc.CommitPreparedReceipt(sabotaged, "2026-06-01"));

        // Rollback restored the original — and the failure was NOT misreported as "duplicate".
        Assert.Equal(before, Snapshot(db));
        Assert.NotNull(ReceiptsRepo.GetReceipt(db.Conn, original.ReceiptId!.Value));
    }

    [Fact]
    public async Task NonReplace_constraint_race_still_reports_duplicate()
    {
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var f = WriteFile("bytes");

        var svc = Build(db, new FakeOcr(raw));
        var prepared = await svc.PrepareReceiptFileAsync(f); // replace OFF, no owner yet
        // The same file is imported concurrently before this commit.
        var winner = await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(f);

        var outcome = svc.CommitPreparedReceipt(prepared, "2026-06-01");

        Assert.True(outcome.WasDuplicate);
        Assert.False(outcome.ReplaceConflict);
        Assert.Equal(winner.ReceiptId, outcome.ReceiptId);
        Assert.Equal(1L, Count(db.Conn, "receipts"));
    }

    [Fact]
    public async Task Successful_replace_commits_backup_delete_and_insert_atomically()
    {
        using var db = new TempDb();
        var raw = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var f = WriteFile("bytes");
        var first = await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(f);

        var replaced = await Build(db, new FakeOcr(raw)).IngestReceiptFileAsync(f, replaceExisting: true);

        Assert.True(replaced.ReplacedExisting);
        Assert.False(replaced.WasDuplicate);
        Assert.NotEqual(first.ReceiptId, replaced.ReceiptId);
        Assert.Null(ReceiptsRepo.GetReceipt(db.Conn, first.ReceiptId!.Value));
        Assert.NotNull(ReceiptsRepo.GetReceipt(db.Conn, replaced.ReceiptId!.Value));
        Assert.Equal(1L, Count(db.Conn, "receipts"));
        Assert.Equal(1L, Count(db.Conn, "deleted_receipt_backups"));
    }
}
