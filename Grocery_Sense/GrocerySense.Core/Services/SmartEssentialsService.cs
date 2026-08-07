using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// V3 F4 (Smart Essentials): one view over the household's staples — purchase cadence (due soon), the
// shared price ladder (current vs usual vs 6-mo low), watchlist targets, persisted open alerts, and
// whether the item supports the confirmed Smart Week plan. Pure composition of existing readers —
// no new tables, no new thresholds; every number rides logic that already ships.
public sealed class SmartEssentialsService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly WatchlistService _watchlist;
    private readonly PriceDropAlertService _alerts;
    private readonly SmartWeekService _smartWeek;

    public SmartEssentialsService(SqliteConnectionFactory factory, WatchlistService watchlist,
        PriceDropAlertService alerts, SmartWeekService smartWeek)
    {
        _factory = factory;
        _watchlist = watchlist;
        _alerts = alerts;
        _smartWeek = smartWeek;
    }

    public IReadOnlyList<EssentialRow> BuildEssentials(int limit = 30)
    {
        using var conn = _factory.Open();

        // Staples by receipt frequency — ListStapleItemIds' defaults ARE the staple gate the alert engine uses.
        var staples = PricesRepo.ListStapleItemIds(conn);
        var ids = staples.Select(s => s.ItemId).Take(limit).ToList();
        if (ids.Count == 0) return [];

        var items = ItemsRepo.GetItemsByIds(conn, ids);
        var stores = StoresRepo.ListStores(conn).Where(s => s.ShopHere && s.IsActive).ToList();
        var storeIds = stores.Select(s => s.Id).ToList();

        var flyer = storeIds.Count > 0
            ? PricesRepo.GetActiveFlyerPricesBatch(conn, ids, storeIds)
            : new Dictionary<(int, int), PriceQuote>();
        var recent = storeIds.Count > 0
            ? PricesRepo.GetMostRecentPricesByStoreBatch(conn, ids, storeIds)
            : new Dictionary<(int, int), PricePoint>();
        var global = PricesRepo.GetMostRecentPricesGlobalBatch(conn, ids);
        var usual = PricesRepo.GetUsualUnitPriceBatch(conn, ids);
        var sixLow = PricesRepo.GetSixMonthLowBatch(conn, ids);
        var lastMap = PricesRepo.GetLastReceiptPurchaseBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);
        var cadence = PricesRepo.GetPurchaseCadenceBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);

        var watches = _watchlist.ListWatches().Where(w => w.IsActive)
            .GroupBy(w => w.ItemId).ToDictionary(g => g.Key, g => g.First());
        var openAlertItemIds = _alerts.GetAlerts(limit: 200).Select(a => a.ItemId).ToHashSet();

        // Plan support: confirmed snapshot ingredients by item id AND normalized name (stale-id fallback
        // already applied by LoadCurrent).
        var snapshot = _smartWeek.LoadCurrent();
        var planByItem = new Dictionary<int, List<string>>();
        var planByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var ing in snapshot?.Ingredients ?? [])
        {
            if (ing.ItemId is { } iid)
                (planByItem.TryGetValue(iid, out var l) ? l : planByItem[iid] = new()).AddRange(ing.RecipeNames);
            var key = SmartWeekService.NormName(ing.Name);
            if (key.Length > 0)
                (planByName.TryGetValue(key, out var nl) ? nl : planByName[key] = new()).AddRange(ing.RecipeNames);
        }

        var today = DateOnly.FromDateTime(DateTime.Now); // local calendar date (V3 convention)
        var rows = new List<EssentialRow>();
        foreach (var id in ids)
        {
            if (!items.TryGetValue(id, out var item)) continue;

            int? daysSince = null, intervalDays = null;
            var due = false;
            if (lastMap.TryGetValue(id, out var lastIso) && DateOnly.TryParse(lastIso, out var last))
            {
                daysSince = today.DayNumber - last.DayNumber;
                var (interval, _) = cadence.GetValueOrDefault(id, (null, null));
                if (interval is > 0)
                {
                    intervalDays = (int)Math.Round(interval.Value);
                    due = daysSince >= interval.Value; // same overdue rule as StapleRestockService
                }
            }

            var quote = PriceQuoteLadder.BestStoreQuote(id, stores, flyer, recent)
                ?? PriceQuoteLadder.GlobalFallback(id, global);
            var (usualPrice, _, usualBasis) = usual.GetValueOrDefault(id, (null, 0, "unknown"));
            var (lowPrice, _) = sixLow.GetValueOrDefault(id, (null, null));

            var supports = planByItem.GetValueOrDefault(id)
                ?? planByName.GetValueOrDefault(SmartWeekService.NormName(item.CanonicalName));

            // Suggested stock-up qty rides the alert engine's existing cadence math (threshold is in PERCENT).
            double? suggestedQty = null;
            if (quote is { } q && lowPrice is { } low && low > 0
                && q.UnitPrice <= low * (1 + PriceDropAlertService.NearSixMonthLowThresholdPct / 100.0))
                suggestedQty = PriceDropAlertService.SuggestStockUpQty(cadence, id)?.Qty;

            rows.Add(new EssentialRow(id, item.CanonicalName, due, daysSince, intervalDays,
                quote?.UnitPrice, quote?.Source, usualPrice, usualBasis, lowPrice,
                watches.TryGetValue(id, out var w) ? w.TargetPrice : null, watches.ContainsKey(id),
                suggestedQty, supports?.Distinct().ToList() ?? [], openAlertItemIds.Contains(id)));
        }

        // Most-overdue first (relative to own cadence), then alphabetical — mirrors the restock ordering.
        return rows
            .OrderByDescending(r => r.DueSoon)
            .ThenByDescending(r => r.DaysSinceLast is { } d && r.IntervalDays is { } i and > 0 ? (double)d / i : 0.0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// One essential: cadence state + price context + watch/plan/alert links. Nulls mean "no honest data",
// never zero.
public sealed record EssentialRow(
    int ItemId, string Name,
    bool DueSoon, int? DaysSinceLast, int? IntervalDays,
    double? CurrentPrice, string? PriceSource,
    double? UsualPrice, string UsualBasis, double? SixMonthLow,
    double? TargetPrice, bool Watched,
    double? SuggestedStockUpQty,
    IReadOnlyList<string> SupportsRecipes, bool HasOpenAlert);
