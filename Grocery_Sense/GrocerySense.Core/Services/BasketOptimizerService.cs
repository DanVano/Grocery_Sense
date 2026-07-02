using System.Text.RegularExpressions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// REDESIGN of reference-python/.../services/basket_optimizer_service.py (NOT a port). No trip penalty.
// Goal: fewest stores that still capture meaningful savings. Primary = cheapest single store; then a hybrid
// add-a-store gate — an item "wants" another store only if it's >= minItemSavingPct cheaper there, and a
// store joins only if its qualifying items save >= minStoreSaving (qty-weighted) combined; greedy to maxStores.
// Modes: fewest_stops (force one store) | best_savings (hybrid). Hard/allergy excludes are pulled OUT (safety
// net via PhraseSafeHit); soft excludes have NO optimizer effect (deal-feed only). Unknown price -> assigned
// to primary, flagged, excluded from totals, pulls no store. Thresholds are single-profile ConfigStore settings.
public sealed class BasketOptimizerService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigStore _config;
    private readonly PreferencesService _prefs;

    public BasketOptimizerService(SqliteConnectionFactory factory, ConfigStore config, PreferencesService prefs)
    {
        _factory = factory;
        _config = config;
        _prefs = prefs;
    }

    private sealed class Assignment
    {
        public int ItemId;
        public string Name = "";
        public double Qty = 1.0;
        public int? StoreId;
        public double? UnitPrice;
        public string Unit = "each";
        public string Source = "";
        public bool Unknown;
    }

    public BasketOptimizationResult Optimize(string mode = "best_savings")
    {
        var cfg = _config.Load();
        var maxStores = cfg.MaxStores;
        var pct = cfg.MinItemSavingPct;
        var minSave = cfg.MinStoreSaving;
        var hybrid = mode == "best_savings" && maxStores > 1;
        var effMode = hybrid ? "best_savings" : "fewest_stops";

        var eff = _prefs.ComputeEffectivePreferences();

        using var conn = _factory.Open();
        var stores = StoresRepo.ListStores(conn).Where(s => s.ShopHere && s.IsActive).ToList();

        // Basket = distinct canonical item_ids on the active list, with summed quantities.
        var rows = ShoppingListRepo.ListActiveItems(conn).Where(r => r.ItemId is not null).ToList();
        var qtyByItem = rows.GroupBy(r => r.ItemId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity <= 0 ? 1.0 : r.Quantity));
        // Per-item priority: must_have wins, then normal; wait_for_sale only when every row for the item is
        // wait_for_sale (a normal need for the same item overrides "wait").
        var priorityByItem = rows.GroupBy(r => r.ItemId!.Value)
            .ToDictionary(g => g.Key, g => AggregatePriority(g.Select(r => r.Priority)));
        var itemIds = qtyByItem.Keys.ToList();
        var itemsMap = ItemsRepo.GetItemsByIds(conn, itemIds);

        if (stores.Count == 0 || itemIds.Count == 0)
            return new BasketOptimizationResult(effMode, Array.Empty<StorePlan>(), 0, null, null, Array.Empty<string>());

        // Partition hard-excluded items OUT (safety net). Soft excludes have no optimizer effect.
        var hardIds = itemIds.Where(id => itemsMap.TryGetValue(id, out var it) && IsHardExcluded(eff, it.CanonicalName)).ToHashSet();
        var basketIds = itemIds.Where(id => !hardIds.Contains(id) && itemsMap.ContainsKey(id)).ToList();

        var storeIds = stores.Select(s => s.Id).ToList();
        var flyerQuotes = PricesRepo.GetActiveFlyerPricesBatch(conn, basketIds, storeIds);
        var storeQuotes = PricesRepo.GetMostRecentPricesByStoreBatch(conn, basketIds, storeIds);
        var globalQuotes = PricesRepo.GetMostRecentPricesGlobalBatch(conn, basketIds);
        var usualAvg = PricesRepo.GetRecentAvgUnitPriceGlobalBatch(conn, basketIds, sinceDays: 180);
        var sixLow = PricesRepo.GetSixMonthLowBatch(conn, basketIds, sinceDays: 183);

        // Price each basket item per store (flyer -> recent store price), plus its cheapest-anywhere fallback.
        var priceByStore = new Dictionary<int, Dictionary<int, (double Price, string Unit, string Source)>>();
        var anyBest = new Dictionary<int, (double Price, int StoreId, string Unit, string Source)?>();
        foreach (var id in basketIds)
        {
            var per = new Dictionary<int, (double, string, string)>();
            foreach (var s in stores)
            {
                if (flyerQuotes.TryGetValue((id, s.Id), out var fq))
                    per[s.Id] = (fq.UnitPrice, fq.Unit ?? "each", "flyer");
                else if (storeQuotes.TryGetValue((id, s.Id), out var pp) && pp.UnitPrice > 0)
                    per[s.Id] = (pp.UnitPrice, pp.Unit, string.IsNullOrEmpty(pp.Source) ? "latest" : pp.Source);
            }
            priceByStore[id] = per;

            if (per.Count > 0)
            {
                var best = per.OrderBy(kv => kv.Value.Item1).First();
                anyBest[id] = (best.Value.Item1, best.Key, best.Value.Item2, best.Value.Item3);
            }
            else if (globalQuotes.TryGetValue(id, out var gl) && gl.UnitPrice > 0)
                anyBest[id] = (gl.UnitPrice, gl.StoreId, gl.Unit, string.IsNullOrEmpty(gl.Source) ? "global_latest" : gl.Source);
            else
                anyBest[id] = null;
        }

        // "Wait for sale" items are left unplanned unless their cheapest current price beats usual by the
        // same margin the optimizer uses to justify a store hop. Unknown price or unknown usual => not a
        // confirmed sale => keep waiting (drop from the plan). must_have/normal are unaffected.
        var waitIds = basketIds.Where(id =>
            priorityByItem.GetValueOrDefault(id, "normal") == "wait_for_sale"
            && !IsOnSale(anyBest, usualAvg, id, pct)).ToHashSet();
        if (waitIds.Count > 0)
            basketIds = basketIds.Where(id => !waitIds.Contains(id)).ToList();

        var priceable = basketIds.Where(id => anyBest[id] is not null).ToList();
        var primary = PickPrimary(stores, priceable, priceByStore, anyBest);

        // Initialize the plan at the primary store.
        var plan = new Dictionary<int, Assignment>();
        var plannedStores = new HashSet<int> { primary.Id };
        foreach (var id in basketIds)
        {
            var a = new Assignment { ItemId = id, Name = itemsMap[id].CanonicalName, Qty = qtyByItem[id] };
            if (anyBest[id] is null)
            {
                a.Unknown = true; a.StoreId = primary.Id; // pulled into primary, flagged, no price
            }
            else if (priceByStore[id].TryGetValue(primary.Id, out var pp))
            {
                a.StoreId = primary.Id; a.UnitPrice = pp.Price; a.Unit = pp.Unit; a.Source = pp.Source;
            }
            else if (hybrid)
            {
                var ab = anyBest[id]!.Value; // primary doesn't carry it -> must add its cheapest store
                a.StoreId = ab.StoreId; a.UnitPrice = ab.Price; a.Unit = ab.Unit; a.Source = ab.Source;
                plannedStores.Add(ab.StoreId);
            }
            else { a.Unknown = true; a.StoreId = primary.Id; } // fewest_stops: not at the single store
            plan[id] = a;
        }

        if (plannedStores.Count > maxStores)
            TrimPlannedStores(plan, plannedStores, primary.Id, maxStores);
        if (hybrid) GreedyAddStores(stores, priceable, priceByStore, plan, plannedStores, maxStores, pct, minSave);

        var result = BuildResult(effMode, stores, primary.Id, plan, hardIds, itemsMap, qtyByItem, usualAvg, sixLow, plannedStores);
        if (waitIds.Count > 0)
        {
            var warnings = result.Warnings.ToList();
            warnings.Add($"{waitIds.Count} item(s) marked 'wait for sale' aren't on sale now and were left unplanned.");
            result = result with { Warnings = warnings };
        }
        return result;
    }

    // A "wait for sale" item counts as on sale only if its cheapest current price is at least `pct` below the
    // recent global average. No current price or no usual average => cannot confirm => not on sale.
    private static bool IsOnSale(Dictionary<int, (double Price, int StoreId, string Unit, string Source)?> anyBest,
        IReadOnlyDictionary<int, double> usualAvg, int itemId, double pct)
    {
        if (anyBest.GetValueOrDefault(itemId) is not { } best) return false;
        if (!usualAvg.TryGetValue(itemId, out var usual) || usual <= 0) return false;
        return best.Price <= usual * (1 - pct);
    }

    private static string AggregatePriority(IEnumerable<string> priorities)
    {
        var list = priorities.Select(p => string.IsNullOrEmpty(p) ? "normal" : p).ToList();
        if (list.Contains("must_have")) return "must_have";
        if (list.Contains("normal")) return "normal";
        return "wait_for_sale";
    }

    private static Store PickPrimary(IReadOnlyList<Store> stores, List<int> priceable,
        Dictionary<int, Dictionary<int, (double Price, string Unit, string Source)>> priceByStore,
        Dictionary<int, (double Price, int StoreId, string Unit, string Source)?> anyBest)
    {
        // Cheapest single store for the basket (missing items fall back to their cheapest-anywhere price so
        // every store is comparable). Tie-break: favorite, then priority, then id.
        double Total(Store s) => priceable.Sum(id =>
            priceByStore[id].TryGetValue(s.Id, out var p) ? p.Price : anyBest[id]!.Value.Price);

        return stores
            .OrderBy(Total)
            .ThenByDescending(s => s.IsFavorite)
            .ThenByDescending(s => s.Priority)
            .ThenBy(s => s.Id)
            .First();
    }

    private static void GreedyAddStores(IReadOnlyList<Store> stores, List<int> priceable,
        Dictionary<int, Dictionary<int, (double Price, string Unit, string Source)>> priceByStore,
        Dictionary<int, Assignment> plan, HashSet<int> plannedStores, int maxStores, double pct, double minSave)
    {
        while (plannedStores.Count < maxStores)
        {
            int? bestStore = null;
            double bestSaving = 0;
            List<int> bestWanting = new();

            foreach (var s in stores.Where(s => !plannedStores.Contains(s.Id)))
            {
                double saving = 0;
                var wanting = new List<int>();
                foreach (var id in priceable)
                {
                    var a = plan[id];
                    if (a.Unknown || a.UnitPrice is null) continue;
                    if (priceByStore[id].TryGetValue(s.Id, out var cand)
                        && cand.Price <= a.UnitPrice.Value * (1 - pct)) // item wants this store (unit-price gate)
                    {
                        saving += (a.UnitPrice.Value - cand.Price) * a.Qty; // qty-weighted dollar saving
                        wanting.Add(id);
                    }
                }
                if (saving >= minSave && saving > bestSaving) { bestSaving = saving; bestStore = s.Id; bestWanting = wanting; }
            }

            if (bestStore is null) break;
            plannedStores.Add(bestStore.Value);
            foreach (var id in bestWanting)
            {
                var cand = priceByStore[id][bestStore.Value];
                var a = plan[id];
                a.StoreId = bestStore.Value; a.UnitPrice = cand.Price; a.Unit = cand.Unit; a.Source = cand.Source;
            }
        }
    }

    private static void TrimPlannedStores(Dictionary<int, Assignment> plan, HashSet<int> plannedStores, int primaryId,
        int maxStores)
    {
        var keep = plannedStores.Where(s => s != primaryId)
            .OrderByDescending(s => plan.Values.Where(a => a.StoreId == s).Sum(a => (a.UnitPrice ?? 0) * a.Qty))
            .ThenBy(s => s)
            .Take(Math.Max(0, maxStores - 1))
            .Append(primaryId)
            .ToHashSet();

        foreach (var a in plan.Values.Where(a => a.StoreId is { } s && !keep.Contains(s)))
        {
            a.Unknown = true;
            a.StoreId = primaryId;
            a.UnitPrice = null;
            a.Source = "";
        }

        plannedStores.Clear();
        foreach (var s in keep) plannedStores.Add(s);
    }

    private static BasketOptimizationResult BuildResult(string mode, IReadOnlyList<Store> stores, int primaryId,
        Dictionary<int, Assignment> plan, HashSet<int> hardIds, IReadOnlyDictionary<int, Item> itemsMap,
        Dictionary<int, double> qtyByItem, IReadOnlyDictionary<int, double> usualAvg,
        IReadOnlyDictionary<int, (double? Price, string? When)> sixLow, HashSet<int> plannedStores)
    {
        var nameById = stores.ToDictionary(s => s.Id, s => s.Name);

        BasketItemPlan ToPlan(Assignment a)
        {
            double? saveVsUsual = !a.Unknown && a.UnitPrice is not null && usualAvg.TryGetValue(a.ItemId, out var u)
                ? u - a.UnitPrice.Value : null;
            var low = sixLow.GetValueOrDefault(a.ItemId).Price;
            double? saveVsLow = !a.Unknown && a.UnitPrice is not null && low is not null
                ? low.Value - a.UnitPrice.Value : null;
            return new BasketItemPlan(a.ItemId, a.Name, a.StoreId, a.Unknown ? null : a.UnitPrice, a.Unit, a.Source,
                HardExcluded: false, a.Unknown, saveVsUsual, saveVsLow);
        }

        // Hard-excluded items are surfaced in the primary store plan (so a write-back pass can null them out).
        var hardPlans = hardIds.Select(id => new BasketItemPlan(
            id, itemsMap[id].CanonicalName, null, null, "each", "", true, false, null, null)).ToList();

        var plans = new List<StorePlan>();
        // Primary first, then the other planned stores in ascending id.
        var ordered = new List<int> { primaryId };
        ordered.AddRange(plannedStores.Where(s => s != primaryId).OrderBy(s => s));

        foreach (var storeId in ordered)
        {
            var items = plan.Values.Where(a => a.StoreId == storeId).Select(ToPlan).ToList();
            if (storeId == primaryId) items.AddRange(hardPlans);
            if (items.Count == 0) continue;
            var total = plan.Values
                .Where(a => a.StoreId == storeId && !a.Unknown && a.UnitPrice is not null)
                .Sum(a => a.UnitPrice!.Value * a.Qty);
            var unknownCount = items.Count(i => i.PriceUnknown);
            plans.Add(new StorePlan(storeId, nameById.GetValueOrDefault(storeId, "Unknown"), items, total, unknownCount));
        }

        var basketTotal = plans.Sum(p => p.TotalEstimated);
        var priced = plan.Values.Where(a => !a.Unknown && a.UnitPrice is not null).ToList();
        double? saveUsual = SumOrNull(priced.Select(a =>
            usualAvg.TryGetValue(a.ItemId, out var u) ? (u - a.UnitPrice!.Value) * a.Qty : (double?)null));
        double? saveLow = SumOrNull(priced.Select(a =>
            sixLow.GetValueOrDefault(a.ItemId).Price is { } low ? (low - a.UnitPrice!.Value) * a.Qty : (double?)null));

        var warnings = new List<string>();
        if (hardIds.Count > 0)
            warnings.Add($"{hardIds.Count} item(s) were hard-excluded by household preferences and were left unplanned.");
        var unknown = plan.Values.Count(a => a.Unknown);
        if (unknown > 0)
            warnings.Add($"{unknown} item(s) have no recent price data and were excluded from the estimate (partial estimate).");

        return new BasketOptimizationResult(mode, plans, basketTotal, saveUsual, saveLow, warnings);
    }

    private static double? SumOrNull(IEnumerable<double?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Sum();
    }

    private static bool IsHardExcluded(EffectivePreferences eff, string itemName) =>
        eff.HardExcludes.Any(tok => PhraseSafeHit(itemName, tok));

    // Whole-word match so "olive" doesn't hit "olive oil". safePhrases (if given) are contexts where the
    // term is acceptable — a hit inside only a safe phrase is suppressed.
    public static bool PhraseSafeHit(string text, string term, IReadOnlyList<string>? safePhrases = null)
    {
        var t = (text ?? "").ToLowerInvariant();
        var tm = (term ?? "").Trim().ToLowerInvariant();
        if (tm.Length == 0) return false;
        if (safePhrases is not null)
            foreach (var sp in safePhrases)
            {
                var s = sp.ToLowerInvariant();
                if (s.Contains(tm) && t.Contains(s)) return false;
            }
        return Regex.IsMatch(t, $@"\b{Regex.Escape(tm)}\b");
    }
}
