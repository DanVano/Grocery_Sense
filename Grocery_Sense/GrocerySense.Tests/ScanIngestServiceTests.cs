using System.Text.Json;
using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

// The single-scan coordinator: ingest then the price-alert pass, with partial success. The key case is that
// an alert-step failure (a DB throw before the notifier guard) leaves the receipt imported — the App must
// keep the image, not delete it.
public sealed class ScanIngestServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_scaningest_{Guid.NewGuid():N}");
    public ScanIngestServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private sealed class FakeOcr(Dictionary<string, object?> raw, string op = "op-1") : IReceiptOcrClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default) => Task.FromResult((op, raw));
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
