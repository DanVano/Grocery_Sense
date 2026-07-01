using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/planning_service.py — greedy store selection for the active
// list. Each item's cheapest store (by recent avg unit price) wins it; stores are scored per item won
// (+0.5 favorite, +0.1 x priority) and the top maxStores are chosen. Costs come from the same batched
// avg maps: store-specific average, falling back to the item's overall (all-store) average.
public sealed class PlanningService
{
    private readonly SqliteConnectionFactory _factory;

    public PlanningService(SqliteConnectionFactory factory) => _factory = factory;

    private sealed record PerStoreCost(double? EstimatedSubtotal, int EstimatedItems, int MissingItems);

    private sealed record CostResults(
        Dictionary<int, PerStoreCost> PerStore, double? BasketTotalEstimate,
        double? BaselineTotalEstimate, double? EstimatedSavings, PlanCoverage Coverage);

    public StorePlanResult BuildPlanForActiveList(int maxStores = 3, int daysBack = 180, int historyLimit = 12)
    {
        using var conn = _factory.Open();

        var items = ShoppingListRepo.ListActiveItems(conn);
        var stores = StoresRepo.ListStores(conn);

        if (items.Count == 0 || stores.Count == 0)
            return new StorePlanResult(
                new Dictionary<int, PlanStoreGroup>(), items,
                "No plan possible (no items or no stores configured).",
                new PlanCosts(null, null, null, null, new PlanCoverage(items.Count, 0, items.Count)));

        var storeById = stores.ToDictionary(s => s.Id);

        // Resolve every row to a canonical Item ONCE, and batch ALL price aggregation up front, so the
        // per-item/per-store loops below are in-memory lookups instead of O(items x stores) queries.
        var resolved = ResolveItemsBulk(conn, items);
        var itemIds = resolved.Values.OfType<Item>().Select(i => i.Id).Distinct().OrderBy(i => i).ToList();
        var storeIds = stores.Select(s => s.Id).ToList();
        var storeAvg = PricesRepo.GetRecentAvgUnitPriceByStoreBatch(conn, itemIds, storeIds,
            sinceDays: daysBack, limit: historyLimit);
        var overallAvg = PricesRepo.GetRecentAvgUnitPriceGlobalBatch(conn, itemIds,
            sinceDays: daysBack, limit: Math.Max(historyLimit, 20));

        // Cheapest store per row (first store in priority order wins ties).
        var bestStoreByRow = new Dictionary<int, int?>();
        foreach (var row in items)
            bestStoreByRow[row.Id] = FindBestStoreForItem(row, stores, storeAvg, resolved);

        // Score stores by items won, biased toward favorites/priority.
        var storeScores = new Dictionary<int, double>();
        foreach (var row in items)
        {
            if (bestStoreByRow[row.Id] is not int sid || !storeById.TryGetValue(sid, out var store)) continue;
            var score = 1.0 + (store.IsFavorite ? 0.5 : 0.0) + store.Priority * 0.1;
            storeScores[sid] = storeScores.GetValueOrDefault(sid) + score;
        }

        var chosen = storeScores.Count == 0
            ? FallbackStores(stores, maxStores) // no price history at all
            : storeScores.OrderByDescending(kv => kv.Value).Take(maxStores).Select(kv => kv.Key).ToList();

        // Assign rows: best store when chosen, else the generic fallback store, else unassigned.
        var planByStore = new Dictionary<int, List<ShoppingListRow>>();
        foreach (var sid in chosen) planByStore[sid] = new List<ShoppingListRow>();
        var unassigned = new List<ShoppingListRow>();
        var fallbackStoreId = ChooseGenericFallbackStore(stores, chosen);
        foreach (var row in items)
        {
            if (bestStoreByRow[row.Id] is int best && planByStore.TryGetValue(best, out var list)) list.Add(row);
            else if (fallbackStoreId is int fb)
            {
                if (!planByStore.TryGetValue(fb, out var fbList)) planByStore[fb] = fbList = new List<ShoppingListRow>();
                fbList.Add(row);
            }
            else unassigned.Add(row);
        }

        var baselineStore = ChooseBaselineStore(stores);
        var costs = ComputeCosts(planByStore, unassigned, baselineStore, resolved, storeAvg, overallAvg);
        var summary = BuildSummary(planByStore, unassigned, storeById, costs);

        var storesStruct = new Dictionary<int, PlanStoreGroup>();
        foreach (var (sid, rows) in planByStore)
        {
            if (!storeById.TryGetValue(sid, out var st)) continue;
            var per = costs.PerStore.GetValueOrDefault(sid);
            storesStruct[sid] = new PlanStoreGroup(st, rows, per?.EstimatedSubtotal,
                per?.EstimatedItems ?? 0, per?.MissingItems ?? 0);
        }

        return new StorePlanResult(storesStruct, unassigned, summary,
            new PlanCosts(costs.BasketTotalEstimate, baselineStore, costs.BaselineTotalEstimate,
                costs.EstimatedSavings, costs.Coverage));
    }

    // Resolve every ShoppingListRow to a canonical Item, keyed by the row id. Rows carrying an item_id
    // resolve in one batched query; only name-only stragglers fall back to a name lookup.
    private static Dictionary<int, Item?> ResolveItemsBulk(SqliteConnection conn, IReadOnlyList<ShoppingListRow> rows)
    {
        var idBased = rows.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).ToList();
        var itemsById = idBased.Count > 0
            ? ItemsRepo.GetItemsByIds(conn, idBased)
            : new Dictionary<int, Item>();

        var result = new Dictionary<int, Item?>();
        foreach (var row in rows)
        {
            Item? item = null;
            if (row.ItemId is int id) itemsById.TryGetValue(id, out item);
            if (item is null)
            {
                var name = row.DisplayName.Trim();
                if (name.Length > 0) item = ItemsRepo.GetItemByName(conn, name);
            }
            result[row.Id] = item;
        }
        return result;
    }

    // Store-specific average, else the item's overall (all-store) average, else null.
    private static double? EstimateUnitPrice(int itemId, int storeId,
        IReadOnlyDictionary<(int ItemId, int StoreId), double> storeAvg,
        IReadOnlyDictionary<int, double> overallAvg)
    {
        if (storeAvg.TryGetValue((itemId, storeId), out var v)) return v;
        return overallAvg.TryGetValue(itemId, out var o) ? o : null;
    }

    private static CostResults ComputeCosts(
        Dictionary<int, List<ShoppingListRow>> planByStore, List<ShoppingListRow> unassigned,
        Store? baselineStore, Dictionary<int, Item?> resolved,
        IReadOnlyDictionary<(int ItemId, int StoreId), double> storeAvg,
        IReadOnlyDictionary<int, double> overallAvg)
    {
        var totalItems = planByStore.Values.Sum(v => v.Count) + unassigned.Count;

        var perStore = new Dictionary<int, PerStoreCost>();
        var basketTotal = 0.0;
        var basketHasAny = false;
        var estimatedItems = 0;

        foreach (var (storeId, rows) in planByStore)
        {
            var subtotal = 0.0;
            var storeHasAny = false;
            int storeEstimated = 0, storeMissing = 0;

            foreach (var row in rows)
            {
                var item = resolved.GetValueOrDefault(row.Id);
                if (item is null) { storeMissing++; continue; }

                var unitPrice = EstimateUnitPrice(item.Id, storeId, storeAvg, overallAvg);
                if (unitPrice is null) { storeMissing++; continue; }

                subtotal += unitPrice.Value * row.Quantity;
                storeHasAny = true;
                basketHasAny = true;
                estimatedItems++;
                storeEstimated++;
            }

            perStore[storeId] = new PerStoreCost(
                storeHasAny ? Math.Round(subtotal, 2) : null, storeEstimated, storeMissing);
            basketTotal += subtotal;
        }

        double? basketTotalEstimate = basketHasAny ? Math.Round(basketTotal, 2) : null;

        // Baseline: every planned item bought at the baseline store instead.
        var baselineTotal = 0.0;
        var baselineHasAny = false;
        if (baselineStore is not null)
        {
            foreach (var rows in planByStore.Values)
                foreach (var row in rows)
                {
                    var item = resolved.GetValueOrDefault(row.Id);
                    if (item is null) continue;
                    var unitPrice = EstimateUnitPrice(item.Id, baselineStore.Id, storeAvg, overallAvg);
                    if (unitPrice is null) continue;
                    baselineTotal += unitPrice.Value * row.Quantity;
                    baselineHasAny = true;
                }
        }

        double? baselineTotalEstimate = baselineHasAny ? Math.Round(baselineTotal, 2) : null;
        double? savings = baselineTotalEstimate is double bl && basketTotalEstimate is double bt
            ? Math.Round(bl - bt, 2) : null;

        return new CostResults(perStore, basketTotalEstimate, baselineTotalEstimate, savings,
            new PlanCoverage(totalItems, estimatedItems, Math.Max(0, totalItems - estimatedItems)));
    }

    private static int? FindBestStoreForItem(ShoppingListRow row, IReadOnlyList<Store> stores,
        IReadOnlyDictionary<(int ItemId, int StoreId), double> storeAvg, Dictionary<int, Item?> resolved)
    {
        var item = resolved.GetValueOrDefault(row.Id);
        if (item is null) return null;

        int? bestStoreId = null;
        double? bestPrice = null;
        foreach (var store in stores)
        {
            if (!storeAvg.TryGetValue((item.Id, store.Id), out var avg)) continue;
            if (bestPrice is null || avg < bestPrice) { bestPrice = avg; bestStoreId = store.Id; }
        }
        return bestStoreId;
    }

    // No price history at all: favorites first, then priority, then name.
    private static List<int> FallbackStores(IReadOnlyList<Store> stores, int maxStores) =>
        stores.OrderBy(s => s.IsFavorite ? 0 : 1)
            .ThenByDescending(s => s.Priority)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxStores).Select(s => s.Id).ToList();

    // Single store that picks up items whose best store wasn't chosen (or that have no history).
    private static int? ChooseGenericFallbackStore(IReadOnlyList<Store> stores, IReadOnlyList<int> chosenStoreIds)
    {
        if (stores.Count == 0) return null;

        var chosenStores = stores.Where(s => chosenStoreIds.Contains(s.Id)).ToList();

        var favs = chosenStores.Where(s => s.IsFavorite).OrderByDescending(s => s.Priority).ToList();
        if (favs.Count > 0) return favs[0].Id;

        return chosenStores.Count > 0 ? chosenStores[0].Id : stores[0].Id;
    }

    // Baseline store for the "all at one store" comparison: favorite with highest priority, else
    // highest priority, else first by name.
    private static Store? ChooseBaselineStore(IReadOnlyList<Store> stores)
    {
        if (stores.Count == 0) return null;

        var favs = stores.Where(s => s.IsFavorite).ToList();
        if (favs.Count > 0) return favs.OrderByDescending(s => s.Priority).First();

        return stores.OrderByDescending(s => s.Priority)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string BuildSummary(Dictionary<int, List<ShoppingListRow>> planByStore,
        List<ShoppingListRow> unassigned, Dictionary<int, Store> storeById, CostResults costs)
    {
        var parts = new List<string>();

        var totalItems = planByStore.Values.Sum(l => l.Count) + unassigned.Count;
        parts.Add($"Planned {totalItems} item(s) across {planByStore.Count} store(s).");

        foreach (var (sid, rows) in planByStore)
        {
            if (!storeById.TryGetValue(sid, out var st)) continue;
            var favFlag = st.IsFavorite ? " (favorite)" : "";
            parts.Add($"- {st.Name}{favFlag}: {rows.Count} item(s)");

            var per = costs.PerStore.GetValueOrDefault(sid);
            var est = per?.EstimatedItems ?? 0;
            var miss = per?.MissingItems ?? 0;
            parts.Add(per?.EstimatedSubtotal is double subtotal
                ? $"    est subtotal: ${F2(subtotal)}  (estimated {est}, missing {miss})"
                : $"    est subtotal: n/a  (estimated {est}, missing {miss})");

            var preview = string.Join(", ", rows.Take(5).Select(r => r.DisplayName));
            if (preview.Length > 0) parts.Add($"    e.g. {preview}");
        }

        if (costs.BasketTotalEstimate is double basket)
            parts.Add($"Basket estimate (plan split): ${F2(basket)}");
        if (costs.BaselineTotalEstimate is double baseline)
            parts.Add($"Baseline estimate (all at one favorite store): ${F2(baseline)}");
        if (costs.EstimatedSavings is double savings)
            parts.Add($"Estimated savings vs baseline: ${F2(savings)}");

        parts.Add($"Coverage: {costs.Coverage.EstimatedItems}/{costs.Coverage.TotalItems} items estimated " +
                  $"({costs.Coverage.MissingItems} missing).");

        if (unassigned.Count > 0)
            parts.Add("Unassigned items (no stores configured): " +
                      string.Join(", ", unassigned.Take(5).Select(r => r.DisplayName)));

        return string.Join("\n", parts);
    }

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}
