using GrocerySense.Core;
using GrocerySense.Domain;

namespace GrocerySense.Tests;

// Pins the one "best current price" ladder every classifying surface climbs (PriceDropAlert /
// Watchlist / ShoppingInsights). Defining semantics: at a store an active flyer quote WINS over the
// most-recent store price even when the store price is lower (if/else-if precedence, not min-of-both),
// non-positive prices are skipped, and the global fallback only matters when no candidate store
// yields a quote. Pure dictionaries in — no DB involved.
public sealed class PriceQuoteLadderTests
{
    private static Store St(int id) => new(id, $"Store {id}");

    private static PricePoint Pp(int itemId, int storeId, double price, string source = "") =>
        new(0, itemId, storeId, source, "2026-01-01", price, "each");

    [Fact]
    public void Flyer_quote_beats_more_recent_store_price_even_when_store_price_is_lower()
    {
        var flyer = new Dictionary<(int, int), PriceQuote> { [(1, 10)] = new(5.00, "", "each") };
        var store = new Dictionary<(int, int), PricePoint> { [(1, 10)] = Pp(1, 10, 3.00) };

        var q = PriceQuoteLadder.BestStoreQuote(1, new[] { St(10) }, flyer, store);

        Assert.NotNull(q);
        Assert.Equal(5.00, q!.Value.UnitPrice); // flyer precedence, NOT min(flyer, latest)
        Assert.Equal(10, q.Value.StoreId);
        Assert.Equal("flyer", q.Value.Source);  // empty Source -> "flyer" label
    }

    [Fact]
    public void Cheapest_candidate_store_wins_and_empty_store_source_labels_latest()
    {
        var flyer = new Dictionary<(int, int), PriceQuote> { [(1, 10)] = new(4.00, "flyer_sync", "each") };
        var store = new Dictionary<(int, int), PricePoint> { [(1, 20)] = Pp(1, 20, 3.50) };

        var q = PriceQuoteLadder.BestStoreQuote(1, new[] { St(10), St(20) }, flyer, store);

        Assert.Equal(3.50, q!.Value.UnitPrice);
        Assert.Equal(20, q.Value.StoreId);
        Assert.Equal("latest", q.Value.Source); // empty Source -> "latest" label
    }

    [Fact]
    public void Non_empty_sources_pass_through_unrelabeled()
    {
        var flyer = new Dictionary<(int, int), PriceQuote> { [(1, 10)] = new(4.00, "flyer_sync", "each") };
        var store = new Dictionary<(int, int), PricePoint> { [(1, 20)] = Pp(1, 20, 6.00, "receipt") };

        Assert.Equal("flyer_sync", PriceQuoteLadder.BestStoreQuote(1, new[] { St(10) }, flyer, store)!.Value.Source);
        Assert.Equal("receipt", PriceQuoteLadder.BestStoreQuote(1, new[] { St(20) }, flyer, store)!.Value.Source);
    }

    [Fact]
    public void Non_positive_prices_are_skipped_and_a_dead_flyer_quote_shadows_the_store_price()
    {
        // Store 10: flyer entry exists but is non-positive — the else-if means the (valid) store
        // price is never consulted at that store. Store 20: non-positive latest. Nothing usable.
        var flyer = new Dictionary<(int, int), PriceQuote> { [(1, 10)] = new(0.0, "", "each") };
        var store = new Dictionary<(int, int), PricePoint>
        {
            [(1, 10)] = Pp(1, 10, 2.00),
            [(1, 20)] = Pp(1, 20, 0.0),
        };

        Assert.Null(PriceQuoteLadder.BestStoreQuote(1, new[] { St(10), St(20) }, flyer, store));
    }

    [Fact]
    public void Global_fallback_fires_only_when_no_candidate_store_yields_a_quote()
    {
        var flyer = new Dictionary<(int, int), PriceQuote>();
        var store = new Dictionary<(int, int), PricePoint> { [(1, 10)] = Pp(1, 10, 5.00) };
        var global = new Dictionary<int, PricePoint> { [1] = Pp(1, 99, 1.00) };

        // Caller composition (BestStoreQuote ?? GlobalFallback): a candidate quote wins even
        // though the global price is cheaper.
        var q = PriceQuoteLadder.BestStoreQuote(1, new[] { St(10) }, flyer, store)
                ?? PriceQuoteLadder.GlobalFallback(1, global);
        Assert.Equal(5.00, q!.Value.UnitPrice);
        Assert.Equal(10, q.Value.StoreId);

        // No candidate store has a quote -> global fallback, with its own store id and label.
        var fb = PriceQuoteLadder.BestStoreQuote(1, new[] { St(30) }, flyer, store)
                 ?? PriceQuoteLadder.GlobalFallback(1, global);
        Assert.Equal(1.00, fb!.Value.UnitPrice);
        Assert.Equal(99, fb.Value.StoreId);
        Assert.Equal("global_latest", fb.Value.Source); // empty Source -> "global_latest" label
    }

    [Fact]
    public void Global_fallback_is_null_when_missing_or_non_positive_and_keeps_a_real_source()
    {
        Assert.Null(PriceQuoteLadder.GlobalFallback(1, new Dictionary<int, PricePoint>()));
        Assert.Null(PriceQuoteLadder.GlobalFallback(1, new Dictionary<int, PricePoint> { [1] = Pp(1, 99, 0.0) }));
        Assert.Equal("manual", PriceQuoteLadder.GlobalFallback(1,
            new Dictionary<int, PricePoint> { [1] = Pp(1, 99, 2.00, "manual") })!.Value.Source);
    }
}
