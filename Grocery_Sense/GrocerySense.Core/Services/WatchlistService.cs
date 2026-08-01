using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Savings watchlist: user picks items (with an optional target price) and this reports which are currently a
// deal. Reuses the existing price signals — active flyer, most-recent store price, global fallback, and the
// receipt "usual" median — rather than duplicating any price plumbing. A hit fires when the best current price
// meets the target, or (no target) is at least MinItemSavingPct below usual.
public sealed class WatchlistService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigStore _config;

    public WatchlistService(SqliteConnectionFactory factory, ConfigStore config)
    {
        _factory = factory;
        _config = config;
    }

    // Items available to add to the watchlist (id + canonical name), for the picker.
    public IReadOnlyList<(int Id, string Name)> ListWatchableItems()
    {
        using var conn = _factory.Open();
        return ItemsRepo.ListAllItemNames(conn);
    }

    public IReadOnlyList<SavingsWatchItem> ListWatches()
    {
        using var conn = _factory.Open();
        return WatchlistRepo.ListActive(conn);
    }

    public int AddWatch(int itemId, double? targetPrice = null)
    {
        using var conn = _factory.Open();
        return WatchlistRepo.AddWatch(conn, itemId, targetPrice is > 0 ? targetPrice : null);
    }

    public void RemoveWatch(int watchId)
    {
        using var conn = _factory.Open();
        WatchlistRepo.RemoveWatch(conn, watchId);
    }

    // Current deals across the active watchlist, strongest saving first.
    public IReadOnlyList<WatchlistHit> ComputeHits()
    {
        using var conn = _factory.Open();
        var watches = WatchlistRepo.ListActive(conn);
        if (watches.Count == 0) return Array.Empty<WatchlistHit>();

        var stores = StoresRepo.ListStores(conn).Where(s => s.ShopHere && s.IsActive).ToList();
        if (stores.Count == 0) return Array.Empty<WatchlistHit>();
        var storeIds = stores.Select(s => s.Id).ToList();
        var storeNames = stores.ToDictionary(s => s.Id, s => s.Name);

        var itemIds = watches.Select(w => w.ItemId).Distinct().ToList();
        var flyerQuotes = PricesRepo.GetActiveFlyerPricesBatch(conn, itemIds, storeIds);
        var storeQuotes = PricesRepo.GetMostRecentPricesByStoreBatch(conn, itemIds, storeIds);
        var globalQuotes = PricesRepo.GetMostRecentPricesGlobalBatch(conn, itemIds);
        var usualMap = PricesRepo.GetUsualUnitPriceBatch(conn, itemIds, receiptOnly: true, sinceDays: 180);

        var pct = _config.Load().MinItemSavingPct;
        var hits = new List<WatchlistHit>();

        foreach (var w in watches)
        {
            // Cheapest current price across shop-here stores: active flyer first, else most-recent store price;
            // else the global most-recent fallback.
            var quote = PriceQuoteLadder.BestStoreQuote(w.ItemId, stores, flyerQuotes, storeQuotes)
                        ?? PriceQuoteLadder.GlobalFallback(w.ItemId, globalQuotes);
            if (quote is not { UnitPrice: > 0 } q) continue;
            var (bestUnit, bestStoreId, bestSource) = ((double?)q.UnitPrice, q.StoreId, q.Source);

            var (usual, _, _) = usualMap.GetValueOrDefault(w.ItemId, (null, 0, "unknown"));
            double? pctBelow = usual is > 0 ? (usual.Value - bestUnit.Value) / usual.Value * 100.0 : null;

            string? reason = null;
            if (w.TargetPrice is > 0 && bestUnit.Value <= w.TargetPrice.Value) reason = "target";
            else if (w.TargetPrice is null && usual is > 0 && bestUnit.Value <= usual.Value * (1 - pct))
                reason = "below_usual";
            if (reason is null) continue;

            hits.Add(new WatchlistHit(w.Id, w.ItemId, w.ItemName, w.TargetPrice, bestUnit.Value, bestStoreId,
                storeNames.GetValueOrDefault(bestStoreId, "Unknown"), bestSource, usual, pctBelow, reason));
        }

        hits.Sort((a, b) => (-(a.PctBelowUsual ?? -1.0)).CompareTo(-(b.PctBelowUsual ?? -1.0)));
        return hits;
    }
}
