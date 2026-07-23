using System.Text.Json;
using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ReceiptIngestionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_ingest_{Guid.NewGuid():N}");
    public ReceiptIngestionServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    // Returns a fixed canned AnalyzeResult regardless of file (so two different files dedupe by signature).
    private sealed class FakeOcr(Dictionary<string, object?> raw, string op = "op-1") : IReceiptOcrClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default) => Task.FromResult((op, raw));
    }

    // Dequeues one canned AnalyzeResult per call so a multi-file batch gets distinct receipts.
    private sealed class SeqOcr(Queue<Dictionary<string, object?>> raws) : IReceiptOcrClient
    {
        private int _n;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default) =>
            Task.FromResult(($"op-{++_n}", raws.Dequeue()));
    }

    private ReceiptIngestionService Build(TempDb db, Dictionary<string, object?> raw) =>
        new(new FakeOcr(raw), new OcrGate(), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());

    private ReceiptIngestionService BuildSeq(TempDb db, params Dictionary<string, object?>[] raws) =>
        new(new SeqOcr(new Queue<Dictionary<string, object?>>(raws)), new OcrGate(), db.Factory,
            new IngredientMappingService(db.Factory), new UnitNormalizationService(), new MultiBuyDealService());

    private static string? PriceDate(TempDb db, int itemId)
    {
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT date FROM prices WHERE item_id = $i LIMIT 1";
        cmd.Parameters.AddWithValue("$i", itemId);
        return cmd.ExecuteScalar() as string;
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, content);
        return path;
    }

    private static Dictionary<string, object?> Str(string v) => new() { ["valueString"] = v, ["confidence"] = 0.9 };
    private static Dictionary<string, object?> Num(double v) => new() { ["valueNumber"] = v, ["confidence"] = 0.9 };
    private static Dictionary<string, object?> Money(double amount) =>
        new() { ["valueCurrency"] = new Dictionary<string, object?> { ["amount"] = amount }, ["confidence"] = 0.9 };

    private static Dictionary<string, object?> Raw(string merchant, string date, double total,
        params (string Desc, double Qty, double Unit, double Line)[] items)
    {
        var arr = items.Select(it => (object?)new Dictionary<string, object?>
        {
            ["valueObject"] = new Dictionary<string, object?>
            {
                ["Description"] = Str(it.Desc),
                ["Quantity"] = Num(it.Qty),
                ["UnitPrice"] = Money(it.Unit),
                ["TotalPrice"] = Money(it.Line),
            },
        }).ToList();

        var raw = new Dictionary<string, object?>
        {
            ["documents"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["fields"] = new Dictionary<string, object?>
                    {
                        ["MerchantName"] = Str(merchant),
                        ["TransactionDate"] = new Dictionary<string, object?> { ["valueDate"] = date, ["confidence"] = 0.9 },
                        ["Total"] = Money(total),
                        ["Items"] = new Dictionary<string, object?> { ["valueArray"] = arr },
                    },
                },
            },
        };
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(raw))!;
    }

    [Fact]
    public async Task Ingest_writes_receipt_line_items_and_prices()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 9.98, ("Milk 2L", 1, 4.99, 4.99), ("Eggs", 1, 4.99, 4.99)));

        var outcome = await svc.IngestReceiptFileAsync(WriteFile("receipt-a"));

        Assert.False(outcome.WasDuplicate);
        Assert.NotNull(outcome.ReceiptId);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
        var lines = ReceiptsRepo.ListReceiptLineItems(db.Conn, outcome.ReceiptId!.Value);
        Assert.Equal(2, lines.Count);
        var milk = lines.Single(l => l.Description == "Milk 2L");
        var prices = PricesRepo.GetPricesForItem(db.Conn, milk.ItemId!.Value);
        Assert.Equal(4.99, Assert.Single(prices).UnitPrice, 2);
    }

    [Fact]
    public async Task Same_file_is_deduped_by_file_hash_without_a_second_ocr_call()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));
        var f = WriteFile("same-bytes");

        await svc.IngestReceiptFileAsync(f);
        var second = await svc.IngestReceiptFileAsync(f);

        Assert.True(second.WasDuplicate);
        Assert.Equal("file_hash", second.DuplicateReason);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    [Fact]
    public async Task Different_file_same_merchant_date_total_is_deduped_by_signature()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));

        await svc.IngestReceiptFileAsync(WriteFile("photo-1"));
        var second = await svc.IngestReceiptFileAsync(WriteFile("photo-2-different-bytes"));

        Assert.True(second.WasDuplicate);
        Assert.Equal("signature", second.DuplicateReason);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    [Fact]
    public async Task ReplaceExisting_deletes_old_and_ingests_new()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));
        var f = WriteFile("x");

        var first = await svc.IngestReceiptFileAsync(f);
        var replaced = await svc.IngestReceiptFileAsync(f, replaceExisting: true);

        Assert.False(replaced.WasDuplicate);
        Assert.True(replaced.ReplacedExisting);
        Assert.NotEqual(first.ReceiptId, replaced.ReceiptId);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    [Fact]
    public void IngestReceipt_leaves_zero_rows_when_a_write_fails()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "A").Id;
        // item_id 999999 doesn't exist -> the line_items/prices FK insert fails mid-transaction.
        var ingest = new ReceiptIngest(store, "2026-06-01", null, null, 4.99, "f.jpg", 4, "op", null, "{}",
            "hash1", "sig1",
            new[] { new ReceiptIngestLine(0, 999999, "milk", 1, 4.99, 4.99, null, 3, "each", null, null, null) });

        using (var tx = db.Conn.BeginTransaction())
        {
            Assert.ThrowsAny<SqliteException>(() => ReceiptsRepo.IngestReceipt(db.Conn, ingest, tx));
            tx.Rollback();
        }

        Assert.Empty(ReceiptsRepo.ListRecentReceipts(db.Conn));
        Assert.Null(ReceiptsRepo.FindReceiptIdByFileHash(db.Conn, "hash1"));
    }

    [Fact]
    public void IngestReceipt_keeps_unmapped_line_but_skips_price_row()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "A").Id;
        var ingest = new ReceiptIngest(store, "2026-06-01", null, null, 4.99, "f.jpg", 4, "op", null, "{}",
            "hash1", "sig1",
            new[] { new ReceiptIngestLine(0, null, "unknown item", 1, 4.99, 4.99, null, 3, "each", null, null, null) });

        int receiptId;
        using (var tx = db.Conn.BeginTransaction())
        {
            receiptId = ReceiptsRepo.IngestReceipt(db.Conn, ingest, tx);
            tx.Commit();
        }

        Assert.Null(ReceiptsRepo.ListReceiptLineItems(db.Conn, receiptId).Single().ItemId);
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prices WHERE receipt_id = $rid";
        cmd.Parameters.AddWithValue("$rid", receiptId);
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void IngestReceipt_rejects_duplicate_dedupe_links_without_repointing_them()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "A").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "milk").Id;
        var first = new ReceiptIngest(store, "2026-06-01", null, null, 4.99, "a.jpg", 4, "op1", null, "{}",
            "same-hash", "sig1",
            new[] { new ReceiptIngestLine(0, item, "milk", 1, 4.99, 4.99, null, 3, "each", null, null, null) });

        int firstId;
        using (var tx = db.Conn.BeginTransaction())
        {
            firstId = ReceiptsRepo.IngestReceipt(db.Conn, first, tx);
            tx.Commit();
        }

        var second = first with { FilePath = "b.jpg", OperationId = "op2", Signature = "sig2" };
        using (var tx = db.Conn.BeginTransaction())
        {
            Assert.ThrowsAny<SqliteException>(() => ReceiptsRepo.IngestReceipt(db.Conn, second, tx));
            tx.Rollback();
        }

        Assert.Equal(firstId, ReceiptsRepo.FindReceiptIdByFileHash(db.Conn, "same-hash"));
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    // ---------- Phase 1: prepare/commit split + backfill batch ----------

    [Fact]
    public async Task Commit_with_confirmed_date_overrides_receipt_and_price_rows()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));

        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("a"));
        Assert.Equal("2026-06-01", prepared.OcrDate); // OCR found a date
        var outcome = svc.CommitPreparedReceipt(prepared, confirmedDate: "2026-01-15");

        var receipt = Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
        Assert.Equal("2026-01-15", receipt.PurchaseDate);
        var milk = ReceiptsRepo.ListReceiptLineItems(db.Conn, outcome.ReceiptId!.Value).Single();
        Assert.Equal("2026-01-15", PriceDate(db, milk.ItemId!.Value)); // override reaches the price row too
    }

    [Fact]
    public async Task Commit_without_override_uses_the_ocr_date()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)));

        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("a"));
        svc.CommitPreparedReceipt(prepared); // single-scan path: no override

        Assert.Equal("2026-06-01", Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn)).PurchaseDate);
    }

    [Fact]
    public async Task Prepare_flags_when_ocr_found_no_date()
    {
        using var db = new TempDb();
        var svc = Build(db, Raw("Loblaws", "", 4.99, ("Milk", 1, 4.99, 4.99))); // no transaction date

        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("a"));

        Assert.Null(prepared.OcrDate);
        Assert.False(prepared.OcrFoundDate);
        Assert.NotNull(prepared.Ingest); // still committable, but the caller must supply a date
    }

    [Fact]
    public async Task ImportBatch_applies_confirmed_dates_and_reports_each_outcome()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db,
            Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99)),
            Raw("Metro", "2026-06-02", 3.50, ("Bread", 1, 3.50, 3.50)),
            Raw("Sobeys", "2026-06-03", 2.00, ("Eggs", 1, 2.00, 2.00)));
        var files = new[] { WriteFile("f1"), WriteFile("f2"), WriteFile("f3") };

        // Confirm the OCR date for the first two; skip the third (resolver returns null).
        var summary = await svc.ImportBatchAsync(files,
            (p, _) => Task.FromResult<string?>(p.Merchant == "Sobeys" ? null : p.OcrDate));

        Assert.Equal(2, summary.Imported);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(2, ReceiptsRepo.ListRecentReceipts(db.Conn).Count);
        Assert.Equal(BatchImportStatus.Skipped, summary.Items[2].Status);
    }

    [Fact]
    public async Task ImportBatch_reports_a_signature_duplicate_within_the_run()
    {
        using var db = new TempDb();
        var dup = Raw("Loblaws", "2026-06-01", 4.99, ("Milk", 1, 4.99, 4.99));
        var svc = BuildSeq(db, dup, Clone(dup)); // identical merchant/date/total => same signature
        var files = new[] { WriteFile("f1"), WriteFile("f2-different-bytes") };

        var summary = await svc.ImportBatchAsync(files, (p, _) => Task.FromResult<string?>(p.OcrDate));

        Assert.Equal(1, summary.Imported);
        Assert.Equal(1, summary.Duplicates);
        Assert.Equal(BatchImportStatus.DuplicateSignature, summary.Items[1].Status);
        Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
    }

    [Fact]
    public async Task ImportBatch_never_defaults_an_undated_receipt_to_today()
    {
        using var db = new TempDb();
        var svc = BuildSeq(db,
            Raw("Loblaws", "", 4.99, ("Milk", 1, 4.99, 4.99)),   // undated -> caller supplies the date
            Raw("Metro", "", 3.50, ("Bread", 1, 3.50, 3.50)));   // undated -> caller declines (skip)
        var files = new[] { WriteFile("f1"), WriteFile("f2") };
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var summary = await svc.ImportBatchAsync(files,
            (p, _) => Task.FromResult<string?>(p.Merchant == "Loblaws" ? "2026-01-15" : null));

        Assert.Equal(1, summary.Imported);
        Assert.Equal(1, summary.Skipped);
        var receipt = Assert.Single(ReceiptsRepo.ListRecentReceipts(db.Conn));
        Assert.Equal("2026-01-15", receipt.PurchaseDate);      // the confirmed backfill date...
        Assert.NotEqual(today, receipt.PurchaseDate);          // ...never silently "today"
    }

    // The load-bearing suppression property: correctly-dated old backfill sits outside the recent-receipt
    // scan window, so a bulk import fires no alerts; a fresh scan afterwards still does.
    [Fact]
    public async Task Backfilled_old_receipts_do_not_fire_the_recent_scan_but_a_fresh_one_does()
    {
        using var db = new TempDb();
        // Four "usual price" receipts dated 40-55 days ago: within the 180-day usual window, outside the
        // 21-day recent-scan window.
        var old = Enumerable.Range(0, 4)
            .Select(i => Raw($"Loblaws", DateTime.UtcNow.AddDays(-40 - i * 5).ToString("yyyy-MM-dd"), 10.0,
                ("Milk", 1, 10.0, 10.0)))
            .ToArray();
        var svc = BuildSeq(db, old);
        var files = old.Select((_, i) => WriteFile($"old-{i}")).ToArray();

        await svc.ImportBatchAsync(files, (p, _) => Task.FromResult<string?>(p.OcrDate));

        var alerts = new PriceDropAlertService(db.Factory);
        Assert.Equal(0, alerts.ScanRecentReceipts()); // nothing recent -> backfill fires no alerts

        // A fresh receipt today at 20% under the established usual price -> a real alert.
        var fresh = Build(db, Raw("Loblaws", DateTime.UtcNow.ToString("yyyy-MM-dd"), 8.0, ("Milk", 1, 8.0, 8.0)));
        await fresh.IngestReceiptFileAsync(WriteFile("fresh"));
        Assert.True(alerts.ScanRecentReceipts() >= 1);
    }

    private static Dictionary<string, object?> Clone(Dictionary<string, object?> raw) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(raw))!;
}
