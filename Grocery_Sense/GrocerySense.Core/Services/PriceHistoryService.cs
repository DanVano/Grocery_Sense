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
    private readonly ConfigStore? _configStore;

    // ponytail: ConfigStore optional — DI injects the real one (rate table lives in user_config.json); legacy
    // callers / tests pass null and fall back to InflationRates.Seed, the same StatCan defaults a fresh config seeds.
    public PriceHistoryService(SqliteConnectionFactory factory, ConfigStore? configStore = null)
    {
        _factory = factory;
        _configStore = configStore;
    }

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

    // Classify a candidate unit price vs an inflation-adjusted, recency-weighted baseline over a ~730-day
    // window (Stage 4 I1). The baseline lifts each past price to today's dollars; min/max stay NOMINAL for the
    // historical-range line (adjusting them would fabricate prices that never existed). PriceDropAlert and
    // sixMonthLow are deliberately untouched (V2_FOLLOWUPS §4 landmine).
    public DealClassification ClassifyDeal(string itemName, double candidateUnitPrice, int windowDays = 730)
    {
        using var conn = _factory.Open();
        var item = ItemsRepo.GetItemByName(conn, itemName.Trim());
        if (item is null)
            return new DealClassification(null, false, "no_data", null, null, null, null, 0,
                $"No price history for '{itemName}'. You can start building history by scanning receipts or entering prices.");

        var rates = _configStore?.Load().FoodInflationByYear ?? InflationRates.Seed;
        var points = PricesRepo.GetPricesForItem(conn, item.Id, storeId: null, sinceDays: windowDays);

        // Dated points only — inflation adjustment needs a real date; never fabricate one for an undated row.
        var dated = new List<(DateOnly Date, double Price)>();
        foreach (var p in points)
            if (DateOnly.TryParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                dated.Add((d, p.UnitPrice));

        if (dated.Count == 0)
            return new DealClassification(item, false, "no_data", null, null, null, null, 0,
                $"No dated price history for '{item.CanonicalName}' in the last {windowDays} days.");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var (baseline, count) = InflationRates.WeightedAdjustedAverage(dated, today, rates);
        var min = dated.Min(x => x.Price);
        var max = dated.Max(x => x.Price);

        if (baseline is null or <= 0)
            return new DealClassification(item, false, "no_data", null, baseline, min, max, count,
                "Price data is invalid or incomplete.");

        // Positive percent = cheaper than the adjusted baseline; negative = more expensive.
        var percent = (baseline.Value - candidateUnitPrice) / baseline.Value * 100.0;

        string classification;
        string message;
        if (count < 3)
        {
            classification = "weak_data";
            message = $"Limited data for '{item.CanonicalName}' (n={count}). Current price {F2(candidateUnitPrice)} " +
                      $"vs inflation-adjusted avg {F2(baseline.Value)} ({Pct(percent)} vs avg).";
        }
        else
        {
            classification = percent switch
            {
                >= 15.0 => "great",
                >= 7.0 => "good",
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
            message = $"{prefix} for '{item.CanonicalName}': {F2(candidateUnitPrice)} vs your inflation-adjusted avg {F2(baseline.Value)} " +
                      $"({Pct(percent)} vs avg). Historical range: {F2(min)}–{F2(max)} from {count} data points.";
        }

        return new DealClassification(item, true, classification, percent, baseline, min, max, count, message);
    }

    private static string Today() => DateTime.Today.ToString("yyyy-MM-dd");
    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Pct(double v) => v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
}
