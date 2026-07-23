using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using Xunit;

namespace GrocerySense.Tests;

// P0-3: no user action can trigger unbounded paid OCR. Caps live at the Core service boundary (the tests
// project has no App reference, so UI checks are unprovable); the singleton OcrGate serializes paid calls
// and maps its deadline to TimeoutException while caller cancellation stays OperationCanceledException.
public sealed class OcrSpendBoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_bounds_{Guid.NewGuid():N}");
    public OcrSpendBoundsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private sealed class CountingOcr(Dictionary<string, object?> raw) : IReceiptOcrClient
    {
        public int Calls;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(("op", raw));
        }
    }

    private sealed class CountingLayout(Dictionary<string, object?> raw) : IFlyerLayoutClient
    {
        public int Calls;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(("op", raw));
        }
    }

    private static ReceiptIngestionService BuildReceipts(TempDb db, IReceiptOcrClient ocr) =>
        new(ocr, new OcrGate(), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());

    private static FlyerIngestService BuildFlyers(TempDb db, IFlyerLayoutClient layout) =>
        new(layout, new OcrGate(), db.Factory, new IngredientMappingService(db.Factory),
            new UnitNormalizationService(), new MultiBuyDealService());

    private string WriteFile(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteSized(long bytes)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.jpg");
        using var fs = File.Create(path);
        fs.SetLength(bytes);
        return path;
    }

    private static Dictionary<string, object?> ReceiptRaw(string merchant, int lineCount)
    {
        var items = Enumerable.Range(0, lineCount).Select(i => (object?)new Dictionary<string, object?>
        {
            ["valueObject"] = new Dictionary<string, object?>
            {
                ["Description"] = new Dictionary<string, object?> { ["valueString"] = $"Item {i}", ["confidence"] = 0.9 },
            },
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["documents"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["fields"] = new Dictionary<string, object?>
                    {
                        ["MerchantName"] = new Dictionary<string, object?> { ["valueString"] = merchant, ["confidence"] = 0.9 },
                        ["Items"] = new Dictionary<string, object?> { ["valueArray"] = items },
                    },
                },
            },
        };
    }

    // ---------------- service-boundary caps: zero client calls when over limit ----------------

    [Fact]
    public async Task Eleventh_receipt_in_a_batch_triggers_zero_ocr_calls()
    {
        using var db = new TempDb();
        var ocr = new CountingOcr(ReceiptRaw("Loblaws", 1));
        var files = Enumerable.Range(0, 11).Select(i => WriteFile($"f{i}")).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReceipts(db, ocr).ImportBatchAsync(files, (_, _) => Task.FromResult<string?>("2026-01-01")));

        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Receipt_batch_aggregate_overflow_triggers_zero_ocr_calls()
    {
        using var db = new TempDb();
        var ocr = new CountingOcr(ReceiptRaw("Loblaws", 1));
        var files = new[] { WriteSized(60L * 1024 * 1024), WriteSized(60L * 1024 * 1024) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReceipts(db, ocr).ImportBatchAsync(files, (_, _) => Task.FromResult<string?>("2026-01-01")));

        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Eleventh_flyer_file_triggers_zero_layout_calls()
    {
        using var db = new TempDb();
        var layout = new CountingLayout(new Dictionary<string, object?>());
        var files = Enumerable.Range(0, 11).Select(i => WriteFile($"p{i}")).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildFlyers(db, layout).IngestAssetsAsync(1, "2026-01-01", "2026-01-08", files));

        Assert.Equal(0, layout.Calls);
    }

    [Fact]
    public async Task Flyer_aggregate_overflow_triggers_zero_layout_calls()
    {
        using var db = new TempDb();
        var layout = new CountingLayout(new Dictionary<string, object?>());
        var files = new[] { WriteSized(60L * 1024 * 1024), WriteSized(60L * 1024 * 1024) };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildFlyers(db, layout).IngestAssetsAsync(1, "2026-01-01", "2026-01-08", files));

        Assert.Equal(0, layout.Calls);
    }

    // ---------------- response + field guards, enforced before any DB write ----------------

    [Fact]
    public async Task Oversized_ocr_response_is_rejected_before_persistence()
    {
        using var db = new TempDb();
        // Round-trip so the value is a JsonElement — the only shape the Azure clients ever produce
        // (RawJson.ToJsonString serializes non-JsonElement values as null by design).
        var huge = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["blob"] = new string('a', ReceiptIngestionService.MaxRawJsonChars + 16),
            }))!;
        var svc = BuildReceipts(db, new CountingOcr(huge));

        await Assert.ThrowsAsync<InvalidDataException>(() => svc.PrepareReceiptFileAsync(WriteFile("f")));

        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM receipt_raw_json";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Over_300_line_receipt_is_rejected_before_any_catalog_write()
    {
        using var db = new TempDb();
        var svc = BuildReceipts(db, new CountingOcr(ReceiptRaw("Loblaws", ReceiptIngestionService.MaxReceiptLines + 1)));

        await Assert.ThrowsAsync<InvalidDataException>(() => svc.PrepareReceiptFileAsync(WriteFile("f")));

        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM items) + (SELECT COUNT(*) FROM stores)";
        Assert.Equal(0L, cmd.ExecuteScalar()); // no store, no items — rejected before the catalog
    }

    [Fact]
    public async Task Merchant_name_is_truncated_to_the_field_cap()
    {
        using var db = new TempDb();
        var svc = BuildReceipts(db, new CountingOcr(ReceiptRaw(new string('M', 400), 1)));

        var prepared = await svc.PrepareReceiptFileAsync(WriteFile("f"));

        Assert.NotNull(prepared.Ingest);
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(LENGTH(name)) FROM stores";
        Assert.Equal((long)ReceiptIngestionService.MaxMerchantChars, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Flyer_deal_count_over_the_cap_is_rejected_before_the_db()
    {
        using var db = new TempDb();
        var lines = Enumerable.Range(0, FlyerIngestService.MaxDealsPerAsset + 1)
            .Select(i => (object?)new Dictionary<string, object?> { ["content"] = $"$2.99 item {i}" })
            .ToList();
        var layout = new Dictionary<string, object?>
        {
            ["pages"] = new List<object?> { new Dictionary<string, object?> { ["lines"] = lines } },
        };
        var svc = BuildFlyers(db, new CountingLayout(layout));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            svc.IngestAssetsAsync(1, "2026-01-01", "2026-01-08", new[] { WriteFile("page") }));

        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM flyer_batches";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    // ---------------- the gate: serialization, deadline vs cancel ----------------

    [Fact]
    public async Task Gate_serializes_two_concurrent_paid_calls()
    {
        var gate = new OcrGate();
        var concurrent = 0;
        var maxConcurrent = 0;

        async Task<int> PaidCall(CancellationToken _)
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref concurrent);
            return 0;
        }

        await Task.WhenAll(
            gate.RunAsync(PaidCall, CancellationToken.None),
            gate.RunAsync(PaidCall, CancellationToken.None));

        Assert.Equal(1, maxConcurrent);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
                return;
    }

    [Fact]
    public async Task Deadline_surfaces_TimeoutException()
    {
        var gate = new OcrGate(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            gate.RunAsync<int>(async tok => { await Task.Delay(Timeout.Infinite, tok); return 0; },
                CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_stays_OperationCanceledException()
    {
        var gate = new OcrGate();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync<int>(async tok => { await Task.Delay(Timeout.Infinite, tok); return 0; }, cts.Token));
    }
}
