using System.Text.Json;
using GrocerySense.Core.Abstractions;

namespace GrocerySense.Tests;

// Shared canned-OCR test doubles + builders for the Azure prebuilt-receipt raw-JSON shape the
// ingestion pipeline consumes. Consumers `using static GrocerySense.Tests.OcrFixtures;` so the
// call sites read the same as the per-file privates they replaced.
internal static class OcrFixtures
{
    // Returns a fixed canned AnalyzeResult regardless of file (so two different files dedupe by signature).
    // Calls counts invocations — the spend-bounds tests assert a rejected request burns zero paid calls.
    internal sealed class FakeOcr(Dictionary<string, object?> raw, string op = "op-1") : IReceiptOcrClient
    {
        public int Calls;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult((op, raw));
        }
    }

    // The layout-client twin of FakeOcr.
    internal sealed class FakeLayout(Dictionary<string, object?> raw, string op = "op-1") : IFlyerLayoutClient
    {
        public int Calls;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult((op, raw));
        }
    }

    // Dequeues one canned result per call; a null entry throws (mid-batch OCR failure).
    internal sealed class SeqOcr(Queue<Dictionary<string, object?>?> raws) : IReceiptOcrClient
    {
        private int _n;
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
        {
            var raw = raws.Dequeue() ?? throw new IOException("OCR unavailable");
            return Task.FromResult(($"op-{++_n}", raw));
        }
    }

    internal sealed class ThrowingOcr : IReceiptOcrClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default) => throw new IOException("OCR unavailable");
    }

    public static Dictionary<string, object?> Str(string v) => new() { ["valueString"] = v, ["confidence"] = 0.9 };
    public static Dictionary<string, object?> Num(double v) => new() { ["valueNumber"] = v, ["confidence"] = 0.9 };
    public static Dictionary<string, object?> Money(double amount) =>
        new() { ["valueCurrency"] = new Dictionary<string, object?> { ["amount"] = amount }, ["confidence"] = 0.9 };

    public static Dictionary<string, object?> Raw(string merchant, string date, double total,
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
        // Round-trip through JSON so the fixture arrives as JsonElement values, matching the real client.
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(raw))!;
    }
}
