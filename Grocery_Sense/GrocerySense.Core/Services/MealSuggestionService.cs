using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/meal_suggestion_service.py — value-focused meal suggestions.
// Score = 0.5*price + 0.3*preference + 0.2*variety. price blends current flyer deals (flyer_deals table)
// against receipt-history baselines; both degrade gracefully to 0 when data is absent.
//
// Deviations from Python: uses its injected RecipeEngine (Python used a module singleton and ignored the
// injected one); a null profile means "empty profile", not a ConfigStore read — the /meals route resolves
// the real profile and passes it in (keeps this service free of the config layer).
public sealed class MealSuggestionService
{
    private readonly RecipeEngine _engine;
    private readonly PriceHistoryService? _priceHistory;
    private readonly SqliteConnectionFactory? _factory; // null => no flyer-deal lookups
    private readonly Func<MealProfile>? _defaultProfile; // resolves the household profile when none is passed

    public MealSuggestionService(RecipeEngine engine, PriceHistoryService? priceHistory = null,
        SqliteConnectionFactory? factory = null, Func<MealProfile>? defaultProfile = null)
    {
        _engine = engine;
        _priceHistory = priceHistory;
        _factory = factory;
        _defaultProfile = defaultProfile;
    }

    public IReadOnlyList<SuggestedMeal> SuggestMealsForWeek(MealProfile? profile = null,
        IEnumerable<string>? targetIngredients = null, int maxRecipes = 6,
        IReadOnlySet<int>? recentlyUsedRecipeIds = null)
    {
        profile ??= _defaultProfile?.Invoke() ?? new MealProfile();

        var targets = targetIngredients?.ToList();
        var candidates = targets is { Count: > 0 }
            ? _engine.FilterByIngredientsAndProfile(targets, profile, maxResults: 200)
            : _engine.LoadAllRecipes();

        // Safety net: re-check hard constraints (allergies / avoid_ingredients / no_<x>) even if the
        // candidate came through the filter — same shared ProfileFilter the RecipeEngine's own filter uses.
        var filtered = candidates.Where(r => !ProfileFilter.Violates(r.Ingredients, profile)).ToList();
        if (filtered.Count == 0) return Array.Empty<SuggestedMeal>();

        var allIngredients = CollectAllIngredients(filtered);
        var dealsByIngredient = FetchDealsForIngredients(allIngredients);

        IReadOnlyDictionary<string, double?> baselineMap =
            _priceHistory?.GetBaselinePrices(allIngredients, windowDays: 90)
            ?? new Dictionary<string, double?>();

        var likelyHave = ComputeLikelyHaveSet(allIngredients);

        var suggestions = new List<SuggestedMeal>(filtered.Count);
        foreach (var r in filtered)
        {
            var reasons = new List<string>();
            var priceScore = PriceScoreForRecipe(r, baselineMap, dealsByIngredient, reasons);
            var preferenceScore = PreferenceScore(r, profile);
            var varietyScore = VarietyScore(r, recentlyUsedRecipeIds);
            var dealScore = DealScoreForRecipe(r, dealsByIngredient);
            var (costTotal, costPerServing, costRatio) = CostEstimate(r, baselineMap);

            // total intentionally excludes dealScore: flyer value already flows through priceScore. dealScore
            // is explanatory only (drives the Family page's OnSaleThisWeek flag at DealScore > 0.2).
            var total = (0.5 * priceScore) + (0.3 * preferenceScore) + (0.2 * varietyScore);

            if (preferenceScore > 0.5) reasons.Add("Matches your meat or tag preferences.");
            if (varietyScore < 0) reasons.Add("You cooked this recently, slightly deprioritized.");

            var haveNames = r.Ingredients.Where(i => likelyHave.Contains(i.ToLowerInvariant())).ToList();
            int? newCount = _factory is null ? null : r.Ingredients.Count - haveNames.Count;
            double? marginal = null;
            if (_factory is not null && costTotal is not null)
            {
                var haveCost = haveNames.Sum(i => baselineMap.GetValueOrDefault(i.ToLowerInvariant()) ?? 0.0);
                marginal = Math.Max(0.0, costTotal.Value - haveCost);
            }
            if (haveNames.Count > 0)
                reasons.Add($"You likely already have {haveNames.Count} ingredient(s): {string.Join(", ", haveNames)}.");

            suggestions.Add(new SuggestedMeal(r, total, preferenceScore, dealScore, priceScore, varietyScore,
                reasons, costTotal, costPerServing, costRatio,
                MarginalCostTotal: marginal, NewIngredientCount: newCount,
                LikelyHaveIngredients: _factory is null ? null : haveNames));
        }

        // Stable sort by descending score (matches Python's stable sort).
        return suggestions.OrderByDescending(s => s.TotalScore).Take(maxRecipes).ToList();
    }

    // Same likely-have rule as WeeklyPlannerService.AnnotateLikelyHave (receipt recency vs cadence, shared
    // LikelyHaveCadenceFraction), but resolved by EXACT canonical item name — the same keying the baseline
    // cost estimate uses, and deliberately NOT IngredientMappingService: MapToItem buffers alias learns
    // that a later flush (planner/ingest on the same singleton) would persist. This path must be read-only.
    // ponytail: exact-name matching misses fuzzy variants; add a read-only mapper mode if that ever matters.
    private HashSet<string> ComputeLikelyHaveSet(IReadOnlyList<string> ingredients)
    {
        var have = new HashSet<string>();
        if (_factory is null || ingredients.Count == 0) return have;

        using var conn = _factory.Open();
        var itemsByName = ItemsRepo.GetItemsByNames(conn, ingredients); // keys are the lowercased names
        if (itemsByName.Count == 0) return have;

        var ids = itemsByName.Values.Select(i => i.Id).Distinct().ToList();
        var lastMap = PricesRepo.GetLastReceiptPurchaseBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);
        var cadence = PricesRepo.GetPurchaseCadenceBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var (name, item) in itemsByName)
        {
            if (!lastMap.TryGetValue(item.Id, out var lastIso) || !DateOnly.TryParse(lastIso, out var last)) continue;
            var (interval, _) = cadence.GetValueOrDefault(item.Id, (null, null));
            if (interval is not > 0) continue; // no cadence -> no inference (never guess)
            var daysSince = today.DayNumber - last.DayNumber;
            if (daysSince >= 0 && daysSince < interval.Value * WeeklyPlannerService.LikelyHaveCadenceFraction)
                have.Add(name); // ingredients arrive lowercased from CollectAllIngredients
        }
        return have;
    }

    // ---- scoring helpers (internal for direct test coverage) ----

    // (cost_total, cost_per_serving, known_ratio). Sums baseline prices for priced ingredients (1 unit each —
    // recipes carry names, not quantities; this is a disclosed estimate). ratio < 1 => partial, must disclose.
    internal static (double? Total, double? PerServing, double KnownRatio) CostEstimate(
        Recipe recipe, IReadOnlyDictionary<string, double?> baseline)
    {
        var ingredients = recipe.Ingredients;
        if (ingredients.Count == 0) return (null, null, 0.0);

        var total = 0.0;
        var known = 0;
        foreach (var ing in ingredients)
            if (baseline.TryGetValue(ing.ToLowerInvariant(), out var price) && price is not null)
            {
                total += price.Value;
                known++;
            }

        if (known == 0) return (null, null, 0.0);
        var ratio = (double)known / ingredients.Count;
        var perServing = recipe.Servings is > 0 ? total / recipe.Servings.Value : (double?)null;
        return (total, perServing, ratio);
    }

    // Preference score in [0,1], recentred so neutral = 0.5 (avoid-only lands below neutral, not at 0).
    internal static double PreferenceScore(Recipe recipe, MealProfile profile)
    {
        var text = string.Join(" ", recipe.Ingredients).ToLowerInvariant();
        var tags = recipe.Tags.ToHashSet();

        var score = 0.0;
        foreach (var meat in Lower(profile.PreferMeats)) if (text.Contains(meat)) score += 0.3;
        foreach (var meat in Lower(profile.AvoidMeats)) if (text.Contains(meat)) score -= 0.5;
        foreach (var tag in Lower(profile.FavoriteTags)) if (tags.Contains(tag)) score += 0.2;

        score = 0.5 + (score * 0.5);
        return Math.Clamp(score, 0.0, 1.0);
    }

    private double PriceScoreForRecipe(Recipe recipe, IReadOnlyDictionary<string, double?> baselineMap,
        IReadOnlyDictionary<string, List<Deal>> dealsByIngredient, List<string> reasonsOut)
    {
        if (recipe.Ingredients.Count == 0) return 0.0;

        var contributions = recipe.Ingredients.Select(ing =>
        {
            var low = ing.ToLowerInvariant();
            baselineMap.TryGetValue(low, out var baseline);
            var deals = dealsByIngredient.GetValueOrDefault(low, new List<Deal>());
            return PriceContributionForIngredient(ing, baseline, deals, reasonsOut);
        }).ToList();

        return Math.Clamp(contributions.Average(), 0.0, 1.0);
    }

    // Contribution in [0,1] for one ingredient; appends a human reason when a real discount is found.
    private static double PriceContributionForIngredient(string name, double? baseline, IReadOnlyList<Deal> deals,
        List<string> reasonsOut)
    {
        var low = name.ToLowerInvariant();
        var relevant = deals.Where(d => d.Name.ToLowerInvariant().Contains(low)).ToList();
        if (relevant.Count == 0 && baseline is null) return 0.0;

        Deal? bestDeal = null;
        double? dealPrice = null;
        foreach (var d in relevant)
        {
            if (d.Price is null) continue;
            if (dealPrice is null || d.Price < dealPrice) { dealPrice = d.Price; bestDeal = d; }
        }

        if (baseline is > 0 && dealPrice is not null)
        {
            var discount = Math.Clamp((baseline.Value - dealPrice.Value) / baseline.Value, 0.0, 1.0);
            if (discount >= 0.15 && bestDeal is not null)
                reasonsOut.Add($"{name} is about {(int)(discount * 100)}% below your usual price at {bestDeal.Store}.");
            return discount;
        }

        if (dealPrice is not null && baseline is null)
        {
            if (bestDeal is not null)
                reasonsOut.Add($"{name} is on sale at {bestDeal.Store} (price {bestDeal.Price}).");
            return 0.15;
        }

        return 0.0;
    }

    // Fraction of the recipe's ingredients that have at least one priced, name-relevant active deal. Explanatory
    // only (not in the total). Uses the same name-contains relevance test as PriceContributionForIngredient.
    private static double DealScoreForRecipe(Recipe recipe, IReadOnlyDictionary<string, List<Deal>> dealsByIngredient)
    {
        if (recipe.Ingredients.Count == 0) return 0.0;
        var withDeal = recipe.Ingredients.Count(ing =>
        {
            var low = ing.ToLowerInvariant();
            return dealsByIngredient.TryGetValue(low, out var deals)
                && deals.Any(d => d.Price is not null && d.Name.Contains(low));
        });
        return (double)withDeal / recipe.Ingredients.Count;
    }

    private static double VarietyScore(Recipe recipe, IReadOnlySet<int>? recentlyUsedRecipeIds) =>
        recipe.Id is int id && recentlyUsedRecipeIds is not null && recentlyUsedRecipeIds.Contains(id) ? -0.2 : 0.0;

    // ---- flyer-deal lookup ----

    internal sealed record Deal(string Name, string Store, double? Price);

    private IReadOnlyDictionary<string, List<Deal>> FetchDealsForIngredients(IReadOnlyList<string> ingredients)
    {
        var empty = new Dictionary<string, List<Deal>>();
        if (_factory is null || ingredients.Count == 0) return empty;

        // InvariantCulture: a device on a non-Gregorian calendar (e.g. th-TH Buddhist) would otherwise stamp
        // the year as 2569, and every SQL date() comparison against Gregorian batches would silently fail.
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var deals = new List<Deal>();
        try
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            // NULL/blank validity dates are treated as open-ended (matches FlyersRepo.ListActiveDeals / the
            // Deals page); requiring both dates would silently drop deals from date-less manual ingests.
            cmd.CommandText = """
                SELECT d.title, d.description,
                       CAST(COALESCE(d.unit_price, d.norm_unit_price, d.deal_total) AS REAL) AS price,
                       s.name AS store_name
                FROM flyer_deals d
                JOIN flyer_batches b ON b.id = d.flyer_id
                LEFT JOIN stores s ON s.id = d.store_id
                WHERE b.status = 'active'
                  AND (b.valid_from IS NULL OR TRIM(b.valid_from) = '' OR date(b.valid_from) <= date($today))
                  AND (b.valid_to   IS NULL OR TRIM(b.valid_to)   = '' OR date(b.valid_to)   >= date($today))
                LIMIT 5000
                """;
            cmd.Parameters.AddWithValue("$today", today);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var title = (r.IsDBNull(0) ? null : r.GetString(0))
                            ?? (r.IsDBNull(1) ? null : r.GetString(1)) ?? "";
                title = title.ToLowerInvariant().Trim();
                if (title.Length == 0) continue;
                var price = r.IsDBNull(2) ? (double?)null : r.GetDouble(2);
                var store = (r.IsDBNull(3) ? null : r.GetString(3)) ?? "";
                deals.Add(new Deal(title, store, price));
            }
        }
        catch (SqliteException) { return empty; } // no flyer tables / transient read issue -> no deals

        // Token-index once, then gather per ingredient by shared tokens (matches Python).
        var index = new Dictionary<string, List<Deal>>();
        foreach (var d in deals)
            foreach (var tok in d.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                (index.TryGetValue(tok, out var list) ? list : index[tok] = new List<Deal>()).Add(d);

        var outMap = new Dictionary<string, List<Deal>>();
        foreach (var ing in ingredients)
        {
            var low = ing.ToLowerInvariant().Trim();
            if (low.Length == 0) continue;
            var seen = new HashSet<Deal>();
            var hits = new List<Deal>();
            foreach (var tok in low.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (index.TryGetValue(tok, out var list))
                    foreach (var d in list)
                        if (seen.Add(d)) hits.Add(d);
            outMap[low] = hits;
        }
        return outMap;
    }

    // Distinct() yields first-occurrence order, which is what the callers below rely on.
    private static List<string> CollectAllIngredients(IEnumerable<Recipe> recipes) =>
        recipes.SelectMany(r => r.Ingredients).Select(i => i.ToLowerInvariant()).Distinct().ToList();

    private static IEnumerable<string> Lower(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant());
}
