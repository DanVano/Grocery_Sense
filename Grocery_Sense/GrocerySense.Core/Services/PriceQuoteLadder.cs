using GrocerySense.Domain;

namespace GrocerySense.Core;

// The one "best current price" ladder every price-classifying surface climbs: an active flyer quote
// beats the most-recent store price at each candidate store, cheapest candidate wins; callers that
// accept any-store history fall back to the global most-recent price. Was hand-mirrored in
// PriceDropAlertService / WatchlistService / ShoppingInsightsService — the same drift risk the
// DealEnricher consolidation killed (V2_FOLLOWUPS §4.22).
public static class PriceQuoteLadder
{
    public readonly record struct Quote(double UnitPrice, int StoreId, string Source, string? Unit);

    // Cheapest (flyer -> most-recent) quote across candidateStores; null when no store has a usable price.
    public static Quote? BestStoreQuote(int itemId, IEnumerable<Store> candidateStores,
        IReadOnlyDictionary<(int ItemId, int StoreId), PriceQuote> flyerQuotes,
        IReadOnlyDictionary<(int ItemId, int StoreId), PricePoint> storeQuotes)
    {
        Quote? best = null;
        foreach (var s in candidateStores)
        {
            double unitPrice;
            string source;
            string? unit;
            if (flyerQuotes.TryGetValue((itemId, s.Id), out var fq))
                (unitPrice, source, unit) = (fq.UnitPrice, string.IsNullOrEmpty(fq.Source) ? "flyer" : fq.Source, fq.Unit);
            else if (storeQuotes.TryGetValue((itemId, s.Id), out var pp) && pp.UnitPrice > 0)
                (unitPrice, source, unit) = (pp.UnitPrice, string.IsNullOrEmpty(pp.Source) ? "latest" : pp.Source, pp.Unit);
            else continue;

            if (unitPrice <= 0) continue;
            if (best is null || unitPrice < best.Value.UnitPrice)
                best = new Quote(unitPrice, s.Id, source, unit);
        }
        return best;
    }

    // Any-store most-recent fallback for when no candidate store has a quote.
    public static Quote? GlobalFallback(int itemId, IReadOnlyDictionary<int, PricePoint> globalQuotes) =>
        globalQuotes.TryGetValue(itemId, out var gl) && gl.UnitPrice > 0
            ? new Quote(gl.UnitPrice, gl.StoreId, string.IsNullOrEmpty(gl.Source) ? "global_latest" : gl.Source, gl.Unit)
            : null;
}
