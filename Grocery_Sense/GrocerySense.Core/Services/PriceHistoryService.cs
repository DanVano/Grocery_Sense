using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/price_history_service.py — item history, baselines, deal classify.
// Wraps ItemsRepo + PricesRepo with higher-level operations. Opens its own connection per call via the
// factory (mirrors Python's connection_scope). The Python dict returns are replaced with typed records.
// Divergence: no ensure_schema() — items.default_unit / prices.norm_* come from the migration ledger.
public sealed class PriceHistoryService
{
    private readonly SqliteConnectionFactory _factory;

    public PriceHistoryService(SqliteConnectionFactory factory) => _factory = factory;

    // ---------- item helpers ----------

    public Item GetOrCreateItem(string canonicalName, string? category = null, string? defaultUnit = null)
    {
        using var conn = _factory.Open();
        return GetOrCreateItem(conn, canonicalName, category, defaultUnit);
    }

    private static Item GetOrCreateItem(SqliteConnection conn, string canonicalName, string? category = null,
        string? defaultUnit = null)
    {
        var clean = canonicalName.Trim();
        return ItemsRepo.GetItemByName(conn, clean)
               ?? ItemsRepo.CreateItem(conn, clean, category: category, defaultUnit: defaultUnit);
    }

    public Item EnsureItemExists(string canonicalName)
    {
        using var conn = _factory.Open();
        return GetOrCreateItem(conn, canonicalName);
    }

    // ---------- recording prices ----------

    public int RecordPriceFromReceipt(string itemName, int storeId, double unitPrice, string unit,
        string? dateStr = null, double? quantity = null, double? totalPrice = null, int? receiptId = null,
        string? rawName = null, int? confidence = null)
    {
        using var conn = _factory.Open();
        var item = GetOrCreateItem(conn, itemName);
        return PricesRepo.AddPricePoint(conn, item.Id, storeId, unitPrice, unit, quantity, totalPrice, rawName,
            confidence, source: "receipt", date: dateStr ?? Today(), receiptId: receiptId);
    }

    public int RecordPriceFromFlyer(string itemName, int storeId, double unitPrice, string unit,
        string? dateStr = null, int? flyerSourceId = null, string? rawName = null, int? confidence = null)
    {
        using var conn = _factory.Open();
        var item = GetOrCreateItem(conn, itemName);
        return PricesRepo.AddPricePoint(conn, item.Id, storeId, unitPrice, unit, rawName: rawName,
            confidence: confidence, source: "flyer", date: dateStr ?? Today(), flyerSourceId: flyerSourceId);
    }

    public int RecordManualPrice(string itemName, int storeId, double unitPrice, string unit,
        string? dateStr = null, double? quantity = null, double? totalPrice = null, string? rawName = null)
    {
        using var conn = _factory.Open();
        var item = GetOrCreateItem(conn, itemName);
        return PricesRepo.AddPricePoint(conn, item.Id, storeId, unitPrice, unit, quantity, totalPrice, rawName,
            source: "manual", date: dateStr ?? Today());
    }

    // ---------- stats & comparison ----------

    public ItemStats? GetItemStats(string itemName, int windowDays = 180)
    {
        using var conn = _factory.Open();
        var item = ItemsRepo.GetItemByName(conn, itemName.Trim());
        if (item is null) return null;
        var stats = PricesRepo.GetPriceStatsForItem(conn, item.Id, sinceDays: windowDays);
        if (stats.Count == 0) return null;
        return new ItemStats(item, stats.AvgPrice, stats.MinPrice, stats.MaxPrice, stats.Count);
    }

    // Baseline = trailing-window average unit price, or null with no usable history.
    public double? GetBaselinePrice(string itemName, int windowDays = 90)
    {
        using var conn = _factory.Open();
        var item = ItemsRepo.GetItemByName(conn, itemName.Trim());
        if (item is null) return null;
        var stats = PricesRepo.GetPriceStatsForItem(conn, item.Id, sinceDays: windowDays);
        return stats.Count == 0 ? null : stats.AvgPrice;
    }

    // Batched GetBaselinePrice: {trimmed_input_name -> avg_or_null}. Case-insensitive; dedupes by lower key.
    public IReadOnlyDictionary<string, double?> GetBaselinePrices(IReadOnlyList<string> itemNames, int windowDays = 90)
    {
        var keyForLower = new Dictionary<string, string>();
        foreach (var n in itemNames)
        {
            var t = (n ?? "").Trim();
            if (t.Length > 0) keyForLower.TryAdd(t.ToLowerInvariant(), t);
        }
        var outMap = new Dictionary<string, double?>();
        if (keyForLower.Count == 0) return outMap;

        using var conn = _factory.Open();
        var itemsByName = ItemsRepo.GetItemsByNames(conn, keyForLower.Keys.ToList());
        var statsMap = PricesRepo.GetPriceStatsBatch(conn, itemsByName.Values.Select(i => i.Id).ToList(), windowDays);

        foreach (var (low, trimmed) in keyForLower)
        {
            double? baseline = null;
            if (itemsByName.TryGetValue(low, out var item) && statsMap.TryGetValue(item.Id, out var st)
                && st.Count > 0)
                baseline = st.AvgPrice;
            outMap[trimmed] = baseline;
        }
        return outMap;
    }

    // Per-store aggregation over a trailing window.
    public StoreStats StatsForItemByStore(int itemId, int storeId, int windowDays)
    {
        using var conn = _factory.Open();
        var points = PricesRepo.GetPricesForItem(conn, itemId, storeId, windowDays);

        var prices = new List<double>();
        var units = new List<string>();
        var dates = new List<string>();
        foreach (var p in points)
        {
            prices.Add(p.UnitPrice);
            if (!string.IsNullOrWhiteSpace(p.Unit)) units.Add(p.Unit.Trim());
            if (!string.IsNullOrEmpty(p.Date)) dates.Add(p.Date);
        }

        var unitHint = units.Count > 0
            ? units.GroupBy(u => u).OrderByDescending(g => g.Count()).First().Key   // mode; stable -> first-seen on ties
            : "";
        var mostRecent = dates.Count > 0 ? dates.Max()! : "";

        if (prices.Count == 0)
            return new StoreStats(null, null, null, 0, unitHint, mostRecent);
        return new StoreStats(prices.Average(), prices.Min(), prices.Max(), prices.Count, unitHint, mostRecent);
    }

    // Classify a candidate unit price vs the trailing-window average.
    public DealClassification ClassifyDeal(string itemName, double candidateUnitPrice, int windowDays = 180)
    {
        using var conn = _factory.Open();
        var item = ItemsRepo.GetItemByName(conn, itemName.Trim());
        if (item is null)
            return new DealClassification(null, false, "no_data", null, null, null, null, 0,
                $"No price history for '{itemName}'. You can start building history by scanning receipts or entering prices.");

        var stats = PricesRepo.GetPriceStatsForItem(conn, item.Id, sinceDays: windowDays);
        if (stats.Count == 0)
            return new DealClassification(item, false, "no_data", null, null, null, null, 0,
                $"No price history for '{item.CanonicalName}' in the last {windowDays} days.");

        var avg = stats.AvgPrice;
        var min = stats.MinPrice;
        var max = stats.MaxPrice;
        var count = stats.Count;
        if (avg is null or <= 0)
            return new DealClassification(item, false, "no_data", null, avg, min, max, count,
                "Price data is invalid or incomplete.");

        // Positive percent = cheaper than avg; negative = more expensive.
        var percent = (avg.Value - candidateUnitPrice) / avg.Value * 100.0;

        string classification;
        string message;
        if (count < 3)
        {
            classification = "weak_data";
            message = $"Limited data for '{item.CanonicalName}' (n={count}). Current price {F2(candidateUnitPrice)} " +
                      $"vs avg {F2(avg.Value)} ({Pct(percent)} vs avg).";
        }
        else
        {
            classification = percent switch
            {
                >= 20.0 => "great",
                >= 10.0 => "good",
                > -10.0 => "typical",
                _ => "expensive",
            };
            var prefix = classification switch
            {
                "great" => "🔥 Great deal",
                "good" => "✅ Good deal",
                "typical" => "➖ Typical price",
                _ => "⚠️ More expensive than usual",
            };
            message = $"{prefix} for '{item.CanonicalName}': {F2(candidateUnitPrice)} vs your average {F2(avg.Value)} " +
                      $"({Pct(percent)} vs avg). Historical range: {F2(min!.Value)}–{F2(max!.Value)} from {count} data points.";
        }

        return new DealClassification(item, true, classification, percent, avg, min, max, count, message);
    }

    public string DescribeItemHistory(string itemName, int windowDays = 365)
    {
        var stats = GetItemStats(itemName, windowDays);
        if (stats is null)
            return $"No price history found for '{itemName}' in the last {windowDays} days.";

        return $"Price history for '{stats.Item.CanonicalName}' (last {windowDays} days):\n" +
               $"  • Average: {F2(stats.AvgUnitPrice!.Value)} per unit\n" +
               $"  • Range:   {F2(stats.MinUnitPrice!.Value)} – {F2(stats.MaxUnitPrice!.Value)}\n" +
               $"  • Samples: {stats.SampleCount} data points";
    }

    private static string Today() => DateTime.Today.ToString("yyyy-MM-dd");
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Pct(double v) => v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
}
