using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// The aisle view: turns the active shopping list into per-store groups with a one-glance verdict per row.
// Reuses the existing price signals (active flyer -> most-recent store price; usual = receipt median;
// 6-month low; purchase cadence) and PriceDropAlertService's public thresholds — no duplicated numbers.
// Badge: stock_up (within NearSixMonthLowThresholdPct of the 6-mo low) > buy (>= DropBelowUsualThresholdPct
// below usual) > wait (above usual) > none (no data / nothing notable — never guess).
public sealed class ShoppingInsightsService
{
    // Below this fraction of categorized list items, swap suggestions would be mostly noise — disclose
    // "not enough category data" instead of suggesting from thin air.
    internal const double MinCategoryCoverage = 0.30;

    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigStore _config;

    public ShoppingInsightsService(SqliteConnectionFactory factory, ConfigStore config)
    {
        _factory = factory;
        _config = config;
    }

    // Store groups ordered like the Plan page reads (priority, then name); "Unassigned" (StoreId null) last.
    // Checked-off rows are included so the in-aisle view keeps showing what's already in the cart.
    public IReadOnlyList<ShopModeGroup> BuildShopModeView()
    {
        using var conn = _factory.Open();
        var rows = ShoppingListRepo.ListActiveItems(conn, storeId: null, includeCheckedOff: true);
        if (rows.Count == 0) return Array.Empty<ShopModeGroup>();

        var allStores = StoresRepo.ListStores(conn);
        var shopHere = allStores.Where(s => s.ShopHere && s.IsActive).ToList();
        var storeNames = allStores.ToDictionary(s => s.Id, s => s.Name);
        var shopHereIds = shopHere.Select(s => s.Id).ToList();

        var itemIds = rows.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).Distinct().ToList();
        var flyerQuotes = PricesRepo.GetActiveFlyerPricesBatch(conn, itemIds, shopHereIds);
        var storeQuotes = PricesRepo.GetMostRecentPricesByStoreBatch(conn, itemIds, shopHereIds);
        var usualMap = PricesRepo.GetUsualUnitPriceBatch(conn, itemIds, receiptOnly: true,
            minSamples: PriceDropAlertService.MinReceiptSamplesForUsual,
            sinceDays: PriceDropAlertService.UsualLookbackDays);
        var sixLowMap = PricesRepo.GetSixMonthLowBatch(conn, itemIds, PriceDropAlertService.LowLookbackDays);
        var cadenceMap = itemIds.Count > 0
            ? PricesRepo.GetPurchaseCadenceBatch(conn, itemIds, PriceDropAlertService.UsualLookbackDays)
            : new Dictionary<int, (double?, double?)>();

        var insights = rows.Select(row => BuildInsight(
            row, shopHere, flyerQuotes, storeQuotes, usualMap, sixLowMap, cadenceMap)).ToList();

        var groups = new List<ShopModeGroup>();
        foreach (var g in insights.GroupBy(i => i.Row.PlannedStoreId))
        {
            var items = g.OrderBy(i => i.Row.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            var subtotal = items.Where(i => i.CurrentPrice is not null)
                .Sum(i => i.CurrentPrice!.Value * (i.Row.Quantity > 0 ? i.Row.Quantity : 1.0));
            var unpriced = items.Count(i => i.CurrentPrice is null);
            var name = g.Key is { } sid ? storeNames.GetValueOrDefault(sid, "Unknown store") : "Unassigned";
            groups.Add(new ShopModeGroup(g.Key, name, items, subtotal, unpriced));
        }

        var storeOrder = shopHere.OrderByDescending(s => s.Priority).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select((s, idx) => (s.Id, idx)).ToDictionary(x => x.Id, x => x.idx);
        return groups
            .OrderBy(g => g.StoreId is null ? 1 : 0)
            .ThenBy(g => g.StoreId is { } id ? storeOrder.GetValueOrDefault(id, int.MaxValue) : int.MaxValue)
            .ThenBy(g => g.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Cheaper same-category alternatives at each row's planned store, using the groups BuildShopModeView
    // already priced. A swap must beat the row's current price by >= MinItemSavingPct (the optimizer's own
    // store-hop threshold). Rows without a planned store or price are skipped — no cross-store swaps.
    public SwapResult BuildSwapSuggestions(IReadOnlyList<ShopModeGroup> groups)
    {
        var rows = groups.SelectMany(g => g.Items).ToList();
        var mapped = rows.Where(i => i.Row.ItemId is not null).ToList();
        if (mapped.Count == 0) return new SwapResult(Array.Empty<SwapSuggestion>(), null);

        using var conn = _factory.Open();
        var allItems = ItemsRepo.ListItems(conn);
        var byId = allItems.ToDictionary(i => i.Id);

        var categorized = mapped.Count(i =>
            byId.TryGetValue(i.Row.ItemId!.Value, out var it) && !string.IsNullOrWhiteSpace(it.Category));
        var coverage = (double)categorized / mapped.Count;
        if (coverage < MinCategoryCoverage)
            return new SwapResult(Array.Empty<SwapSuggestion>(),
                $"Not enough category data for swap suggestions ({categorized}/{mapped.Count} list items " +
                "categorized) — set categories on the Items page.");

        // Candidate pool: every categorized item, priced at each planned store the list touches.
        var candidateIds = allItems.Where(i => !string.IsNullOrWhiteSpace(i.Category)).Select(i => i.Id).ToList();
        var storeIds = groups.Where(g => g.StoreId is not null).Select(g => g.StoreId!.Value).Distinct().ToList();
        if (candidateIds.Count == 0 || storeIds.Count == 0)
            return new SwapResult(Array.Empty<SwapSuggestion>(), null);

        var flyer = PricesRepo.GetActiveFlyerPricesBatch(conn, candidateIds, storeIds);
        var recent = PricesRepo.GetMostRecentPricesByStoreBatch(conn, candidateIds, storeIds);

        var pct = _config.Load().MinItemSavingPct;
        var suggestions = new List<SwapSuggestion>();
        foreach (var insight in mapped)
        {
            if (insight.Row.PlannedStoreId is not { } storeId || insight.CurrentPrice is not { } current) continue;
            if (!byId.TryGetValue(insight.Row.ItemId!.Value, out var item)
                || string.IsNullOrWhiteSpace(item.Category)) continue;

            SwapSuggestion? best = null;
            foreach (var cand in allItems)
            {
                if (cand.Id == item.Id || cand.Category != item.Category) continue;
                double? price = flyer.TryGetValue((cand.Id, storeId), out var fq) ? fq.UnitPrice
                    : recent.TryGetValue((cand.Id, storeId), out var pp) && pp.UnitPrice > 0 ? pp.UnitPrice
                    : null;
                if (price is not { } p || p <= 0 || p >= current * (1 - pct)) continue;

                var savePct = (current - p) / current * 100.0;
                if (best is null || p < best.SwapPrice)
                    best = new SwapSuggestion(insight.Row.Id, insight.Row.DisplayName, cand.CanonicalName,
                        p, current, savePct);
            }
            if (best is not null) suggestions.Add(best);
        }
        return new SwapResult(suggestions, null);
    }

    private static ListItemInsight BuildInsight(
        ShoppingListRow row,
        IReadOnlyList<Store> shopHere,
        IReadOnlyDictionary<(int, int), PriceQuote> flyerQuotes,
        IReadOnlyDictionary<(int, int), PricePoint> storeQuotes,
        IReadOnlyDictionary<int, (double? Price, int Samples, string Basis)> usualMap,
        IReadOnlyDictionary<int, (double? Price, string? WhenIso)> sixLowMap,
        IReadOnlyDictionary<int, (double? AvgIntervalDays, double? TypicalQty)> cadenceMap)
    {
        if (row.ItemId is not { } itemId)
            return new ListItemInsight(row, null, null, null, null, null, null, Badge: "none");

        // Quote at the planned store only; without a planned store, the cheapest shop-here quote.
        double? current = null;
        string? source = null;
        string? unit = null;
        var candidates = row.PlannedStoreId is { } planned
            ? shopHere.Where(s => s.Id == planned)
            : shopHere;
        foreach (var s in candidates)
        {
            double price;
            string src, un;
            if (flyerQuotes.TryGetValue((itemId, s.Id), out var fq))
                (price, src, un) = (fq.UnitPrice, string.IsNullOrEmpty(fq.Source) ? "flyer" : fq.Source, fq.Unit ?? "each");
            else if (storeQuotes.TryGetValue((itemId, s.Id), out var pp) && pp.UnitPrice > 0)
                (price, src, un) = (pp.UnitPrice, string.IsNullOrEmpty(pp.Source) ? "latest" : pp.Source, pp.Unit);
            else continue;
            if (price <= 0) continue;
            if (current is null || price < current) (current, source, unit) = (price, src, un);
        }

        var (usual, _, _) = usualMap.GetValueOrDefault(itemId, (null, 0, "unknown"));
        var (sixLow, _) = sixLowMap.GetValueOrDefault(itemId, (null, null));
        double? pctBelowUsual = usual is > 0 && current is { } c ? (usual.Value - c) / usual.Value * 100.0 : null;

        var badge = "none";
        double? suggestedQty = null;
        string? suggestedQtyNote = null;
        if (current is { } cur)
        {
            var nearLow = sixLow >= PriceDropAlertService.MinLowPriceFloor
                && cur <= sixLow!.Value * (1.0 + PriceDropAlertService.NearSixMonthLowThresholdPct / 100.0);
            if (nearLow)
            {
                badge = "stock_up";
                if (PriceDropAlertService.SuggestStockUpQty(cadenceMap, itemId) is { } sq)
                    (suggestedQty, suggestedQtyNote) = (sq.Qty, sq.Note);
            }
            else if (pctBelowUsual >= PriceDropAlertService.DropBelowUsualThresholdPct)
                badge = "buy";
            else if (usual is > 0 && cur > usual.Value)
                badge = "wait";
        }

        return new ListItemInsight(row, current, source, unit, usual, pctBelowUsual, sixLow, badge,
            suggestedQty, suggestedQtyNote);
    }
}
