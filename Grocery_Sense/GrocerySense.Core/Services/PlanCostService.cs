using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// V3 Phase 2: the quantity-aware plan cost engine behind Smart Week's "fits your cap" claims. Replaces the
// 1-unit-per-ingredient, per-recipe estimate for PLAN-level costing:
//   - shared ingredients are counted ONCE across the plan (per (name, unit), quantities summed),
//   - quantities scale by requested household servings vs each recipe's servings,
//   - ingredient names resolve to items READ-ONLY (no alias-learn side effects — grill Q6),
//   - prices come from the shared PriceQuoteLadder (flyer > recent, shop-here stores, global fallback),
//     converted into the recipe's unit via UnitNormalizationService (cross-dimension = honest unknown),
//   - the incremental total discounts likely-have pantry items (same cadence rule as the planners).
// Estimates stay double (disclosed estimates, not ledger money); coverage is disclosed on every result.
public sealed class PlanCostService
{
    // Grill Q9: below this coverage a budget-fit claim understates the real total badly enough to be
    // dishonest. Applies to THIS quantity-aware basis (the legacy name-based estimate keeps its own 0.5 —
    // it is not used for Smart Week budget claims).
    public const double MinCoverageForBudget = 0.7;

    // Statuses on PlanIngredientCost.Status.
    public const string StatusPriced = "priced";
    public const string StatusUnmapped = "unmapped";
    public const string StatusUnpriced = "unpriced";
    public const string StatusUnitMismatch = "unit_mismatch";
    public const string StatusNoQuantity = "no_quantity";

    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;
    private readonly UnitNormalizationService _units = new();

    public PlanCostService(SqliteConnectionFactory factory, IngredientMappingService mapper)
    {
        _factory = factory;
        _mapper = mapper;
    }

    // householdServings <= 0 means "as written" (scale 1 per recipe).
    public PlanCostEstimate EstimatePlanCost(IReadOnlyList<Recipe> recipes, int householdServings = 0)
    {
        var aggregated = AggregateIngredients(recipes, householdServings);
        if (aggregated.Count == 0)
            return new PlanCostEstimate([], 0.0, 0.0, 0.0, 0, 0);

        using var conn = _factory.Open();

        // Read-only name -> item resolution.
        foreach (var a in aggregated)
        {
            var res = _mapper.MapToItemReadOnly(conn, a.Name);
            a.ItemId = res.ItemId;
            a.CanonicalName = res.CanonicalName;
            a.Confidence = res.Confidence;
        }

        var itemIds = aggregated.Where(a => a.ItemId is not null).Select(a => a.ItemId!.Value).Distinct().ToList();
        var stores = StoresRepo.ListStores(conn).Where(s => s.ShopHere && s.IsActive).ToList();
        var storeIds = stores.Select(s => s.Id).ToList();

        var flyer = itemIds.Count > 0 && storeIds.Count > 0
            ? PricesRepo.GetActiveFlyerPricesBatch(conn, itemIds, storeIds)
            : new Dictionary<(int, int), PriceQuote>();
        var recent = itemIds.Count > 0 && storeIds.Count > 0
            ? PricesRepo.GetMostRecentPricesByStoreBatch(conn, itemIds, storeIds)
            : new Dictionary<(int, int), PricePoint>();
        var global = itemIds.Count > 0
            ? PricesRepo.GetMostRecentPricesGlobalBatch(conn, itemIds)
            : new Dictionary<int, PricePoint>();

        // Pantry inference — same rule as WeeklyPlannerService.AnnotateLikelyHave (hint-only, never guesses).
        var lastMap = itemIds.Count > 0
            ? PricesRepo.GetLastReceiptPurchaseBatch(conn, itemIds, PriceDropAlertService.UsualLookbackDays)
            : new Dictionary<int, string>();
        var cadence = itemIds.Count > 0
            ? PricesRepo.GetPurchaseCadenceBatch(conn, itemIds, PriceDropAlertService.UsualLookbackDays)
            : new Dictionary<int, (double?, double?)>();
        var today = DateOnly.FromDateTime(DateTime.Now); // local calendar date (V3 local-date convention)

        var results = new List<PlanIngredientCost>(aggregated.Count);
        foreach (var a in aggregated)
        {
            string status;
            double? unitPrice = null, cost = null;
            string? priceUnit = null, priceSource = null;
            int? priceStore = null;
            var likelyHave = false;
            string? likelyWhy = null;

            if (a.ItemId is not { } itemId)
            {
                status = StatusUnmapped;
            }
            else
            {
                (likelyHave, likelyWhy) = LikelyHave(itemId, lastMap, cadence, today);

                var quote = PriceQuoteLadder.BestStoreQuote(itemId, stores, flyer, recent)
                    ?? PriceQuoteLadder.GlobalFallback(itemId, global);
                if (quote is null)
                {
                    status = StatusUnpriced;
                }
                else if (a.NoQuantity)
                {
                    status = StatusNoQuantity; // recipe without structured details — cannot honestly price
                }
                else
                {
                    // Convert the PRICE into the recipe's unit, then cost = needed qty x price-per-recipe-unit.
                    // An unknown observed price unit is treated as "each" (same default Normalize() applies).
                    var fromUnit = _units.NormalizeUnit(quote.Value.Unit) is var nu && nu != "unknown" ? nu : "each";
                    var perRecipeUnit = _units.Convert(quote.Value.UnitPrice, fromUnit, a.Unit);
                    if (perRecipeUnit is null)
                    {
                        status = StatusUnitMismatch;
                        unitPrice = quote.Value.UnitPrice;
                        priceUnit = fromUnit;
                        priceSource = quote.Value.Source;
                        priceStore = quote.Value.StoreId;
                    }
                    else
                    {
                        status = StatusPriced;
                        unitPrice = quote.Value.UnitPrice;
                        priceUnit = fromUnit;
                        priceSource = quote.Value.Source;
                        priceStore = quote.Value.StoreId;
                        cost = a.Quantity * perRecipeUnit.Value;
                    }
                }
            }

            results.Add(new PlanIngredientCost(a.Name, a.Quantity, a.Unit, a.RecipeNames.ToList(),
                a.ItemId, a.CanonicalName, a.Confidence, unitPrice, priceUnit, priceSource, priceStore,
                cost, status, likelyHave, likelyWhy));
        }

        var priced = results.Where(r => r.Cost is not null).ToList();
        var pricedTotal = priced.Sum(r => r.Cost!.Value);
        var haveTotal = priced.Where(r => r.LikelyHave).Sum(r => r.Cost!.Value);
        var coverage = results.Count > 0 ? (double)priced.Count / results.Count : 0.0;

        return new PlanCostEstimate(results, pricedTotal, Math.Max(0.0, pricedTotal - haveTotal),
            coverage, priced.Count, results.Count);
    }

    // ---- aggregation (internal for direct test coverage) ----

    internal sealed class Aggregated
    {
        public required string Name;
        public double Quantity;
        public required string Unit;
        public bool NoQuantity;
        public readonly List<string> RecipeNames = new();
        public int? ItemId;
        public string? CanonicalName;
        public double? Confidence;
    }

    // Shared ingredients collapse by (lowercased name, unit) with quantities summed — counted once across
    // the plan, never per recipe. Recipes lacking structured details contribute name-only rows (NoQuantity)
    // so coverage honestly reflects them instead of quietly ignoring them.
    internal static List<Aggregated> AggregateIngredients(IReadOnlyList<Recipe> recipes, int householdServings)
    {
        var map = new Dictionary<(string Name, string Unit), Aggregated>();
        foreach (var r in recipes)
        {
            var scale = householdServings > 0 && r.Servings is > 0
                ? (double)householdServings / r.Servings.Value
                : 1.0;

            if (r.Details is { StructuredIngredients.Count: > 0 })
            {
                foreach (var si in r.Details.StructuredIngredients)
                {
                    var key = (si.Name.ToLowerInvariant(), si.Unit);
                    if (!map.TryGetValue(key, out var agg))
                        map[key] = agg = new Aggregated { Name = si.Name.ToLowerInvariant(), Unit = si.Unit };
                    agg.Quantity += si.Quantity * scale;
                    if (!agg.RecipeNames.Contains(r.Name)) agg.RecipeNames.Add(r.Name);
                }
            }
            else
            {
                foreach (var ing in r.Ingredients)
                {
                    var key = (ing.ToLowerInvariant(), "each");
                    if (!map.TryGetValue(key, out var agg))
                        map[key] = agg = new Aggregated
                            { Name = ing.ToLowerInvariant(), Unit = "each", NoQuantity = true };
                    if (!agg.RecipeNames.Contains(r.Name)) agg.RecipeNames.Add(r.Name);
                }
            }
        }
        return map.Values.ToList();
    }

    private static (bool Have, string? Why) LikelyHave(int itemId,
        IReadOnlyDictionary<int, string> lastMap,
        IReadOnlyDictionary<int, (double? AvgIntervalDays, double? TypicalQty)> cadence, DateOnly today)
    {
        if (!lastMap.TryGetValue(itemId, out var lastIso) || !DateOnly.TryParse(lastIso, out var last))
            return (false, null);
        var (interval, _) = cadence.GetValueOrDefault(itemId, (null, null));
        if (interval is not > 0) return (false, null); // no cadence -> no inference (never guess)
        var daysSince = today.DayNumber - last.DayNumber;
        if (daysSince >= 0 && daysSince < interval.Value * WeeklyPlannerService.LikelyHaveCadenceFraction)
            return (true, $"bought {daysSince}d ago, typical interval {interval.Value:0}d");
        return (false, null);
    }
}

// One aggregated plan ingredient with its quantity-aware price resolution. Status: priced | unmapped |
// unpriced | unit_mismatch | no_quantity — anything but "priced" is disclosed, never silently padded.
public sealed record PlanIngredientCost(
    string Name, double NeededQty, string Unit, IReadOnlyList<string> RecipeNames,
    int? ItemId, string? CanonicalName, double? MatchConfidence,
    double? UnitPrice, string? PriceUnit, string? PriceSource, int? PriceStoreId,
    double? Cost, string Status, bool LikelyHave, string? LikelyHaveReason);

// PricedTotal = every priced ingredient; IncrementalTotal discounts likely-have pantry items (grill Q8:
// the Smart Week cap constrains THIS number). CoverageRatio = priced fraction by ingredient count.
public sealed record PlanCostEstimate(
    IReadOnlyList<PlanIngredientCost> Ingredients,
    double PricedTotal, double IncrementalTotal,
    double CoverageRatio, int PricedCount, int TotalCount);
