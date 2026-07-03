using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GrocerySense.Data;
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

    public MealSuggestionService(RecipeEngine engine, PriceHistoryService? priceHistory = null,
        SqliteConnectionFactory? factory = null)
    {
        _engine = engine;
        _priceHistory = priceHistory;
        _factory = factory;
    }

    public IReadOnlyList<SuggestedMeal> SuggestMealsForWeek(MealProfile? profile = null,
        IEnumerable<string>? targetIngredients = null, int maxRecipes = 6,
        IReadOnlySet<int>? recentlyUsedRecipeIds = null)
    {
        profile ??= new MealProfile();

        var targets = targetIngredients?.ToList();
        var candidates = targets is { Count: > 0 }
            ? _engine.FilterByIngredientsAndProfile(targets, profile, maxResults: 200)
            : _engine.LoadAllRecipes();

        // Safety net: re-check hard constraints even if the candidate came through the filter.
        var filtered = candidates.Where(r => !HasDisallowedIngredients(r, profile)).ToList();
        if (filtered.Count == 0) return Array.Empty<SuggestedMeal>();

        var allIngredients = CollectAllIngredients(filtered);
        var dealsByIngredient = FetchDealsForIngredients(allIngredients);

        IReadOnlyDictionary<string, double?> baselineMap =
            _priceHistory?.GetBaselinePrices(allIngredients, windowDays: 90)
            ?? new Dictionary<string, double?>();

        var suggestions = new List<SuggestedMeal>(filtered.Count);
        foreach (var r in filtered)
        {
            var reasons = new List<string>();
            var priceScore = PriceScoreForRecipe(r, baselineMap, dealsByIngredient, reasons);
            var preferenceScore = PreferenceScore(r, profile);
            var varietyScore = VarietyScore(r, recentlyUsedRecipeIds);
            var (costTotal, costPerServing, costRatio) = CostEstimate(r, baselineMap);

            var total = (0.5 * priceScore) + (0.3 * preferenceScore) + (0.2 * varietyScore);

            if (preferenceScore > 0.5) reasons.Add("Matches your meat or tag preferences.");
            if (varietyScore < 0) reasons.Add("You cooked this recently, slightly deprioritized.");

            suggestions.Add(new SuggestedMeal(r, total, preferenceScore, 0.0, priceScore, varietyScore,
                reasons, costTotal, costPerServing, costRatio));
        }

        // Stable sort by descending score (matches Python's stable sort).
        return suggestions.OrderByDescending(s => s.TotalScore).Take(maxRecipes).ToList();
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

    private static double VarietyScore(Recipe recipe, IReadOnlySet<int>? recentlyUsedRecipeIds) =>
        recipe.Id is int id && recentlyUsedRecipeIds is not null && recentlyUsedRecipeIds.Contains(id) ? -0.2 : 0.0;

    // Hard filter (safety net): allergies / avoid_ingredients / no_<x> restrictions, whole-word.
    internal static bool HasDisallowedIngredients(Recipe recipe, MealProfile profile)
    {
        var text = string.Join(" ", recipe.Ingredients).ToLowerInvariant();
        foreach (var term in Lower(profile.Allergies).Concat(Lower(profile.AvoidIngredients)))
            if (WholeWord(term, text)) return true;

        foreach (var r in Lower(profile.Restrictions))
            if (r.StartsWith("no_"))
            {
                var term = r[3..].Trim();
                if (term.Length > 0 && term is not ("meat" or "fish") && WholeWord(term, text)) return true;
            }
        return false;
    }

    // ---- flyer-deal lookup ----

    internal sealed record Deal(string Name, string Store, double? Price, string Unit);

    private IReadOnlyDictionary<string, List<Deal>> FetchDealsForIngredients(IReadOnlyList<string> ingredients)
    {
        var empty = new Dictionary<string, List<Deal>>();
        if (_factory is null || ingredients.Count == 0) return empty;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var deals = new List<Deal>();
        try
        {
            using var conn = _factory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT d.title, d.description,
                       CAST(COALESCE(d.unit_price, d.norm_unit_price, d.deal_total) AS REAL) AS price,
                       d.unit, s.name AS store_name
                FROM flyer_deals d
                JOIN flyer_batches b ON b.id = d.flyer_id
                LEFT JOIN stores s ON s.id = d.store_id
                WHERE b.status = 'active'
                  AND b.valid_from IS NOT NULL AND b.valid_to IS NOT NULL
                  AND TRIM(b.valid_from) <> '' AND TRIM(b.valid_to) <> ''
                  AND date(b.valid_from) <= date($today) AND date(b.valid_to) >= date($today)
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
                var unit = (r.IsDBNull(3) ? null : r.GetString(3))?.Trim() ?? "each";
                var store = (r.IsDBNull(4) ? null : r.GetString(4)) ?? "";
                deals.Add(new Deal(title, store, price, unit.Length == 0 ? "each" : unit));
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

    private static List<string> CollectAllIngredients(IEnumerable<Recipe> recipes)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var r in recipes)
            foreach (var ing in r.Ingredients)
            {
                var low = ing.ToLowerInvariant();
                if (seen.Add(low)) result.Add(low);
            }
        return result;
    }

    // ---- explanation (static; ported verbatim) ----

    public static string FormatMealExplanation(string recipeName, double preferenceScore, double dealScore,
        double priceScore, double varietyScore, IReadOnlyList<string> reasons, int maxReasons = 4)
    {
        var lines = new List<string> { $"Why we suggested '{recipeName}':" };

        var bits = new List<string>();
        if (preferenceScore > 0.3) bits.Add("matches your eating preferences");
        if (dealScore > 0.2) bits.Add("uses ingredients that are on sale this week");
        if (priceScore > 0.2) bits.Add("is cheaper than your usual prices");
        if (varietyScore > 0.2) bits.Add("adds variety compared to your recent meals");

        lines.Add(bits.Count > 0
            ? " • " + string.Join("; ", bits) + "."
            : " • Overall a reasonable match based on your profile and history.");

        if (reasons.Count > 0)
        {
            lines.Add("");
            lines.Add("Details:");
            foreach (var r in reasons.Take(maxReasons)) lines.Add($" • {r}");
        }
        return string.Join("\n", lines);
    }

    public static string ExplainSuggestedMeal(SuggestedMeal meal)
    {
        var name = meal.Recipe.Name.Length > 0 ? meal.Recipe.Name : "Unknown recipe";
        var sb = new StringBuilder(FormatMealExplanation(name, meal.PreferenceScore, meal.DealScore,
            meal.PriceScore, meal.VarietyScore, meal.Reasons));

        if (meal.CostPerServing is not null)
        {
            var pct = (int)(meal.CostKnownRatio * 100);
            var servings = meal.Recipe.Servings?.ToString() ?? "?";
            sb.Append(CultureInfo.InvariantCulture,
                $"\n\nEst. cost: ≈ ${meal.CostPerServing:0.00}/serving (${meal.CostTotal:0.00} total, {servings} servings)" +
                $" — {pct}% of ingredients priced from your receipt history.");
            if (meal.CostKnownRatio < 1.0)
                sb.Append("\n(Partial estimate — some ingredients have no price history.)");
        }
        else
        {
            sb.Append("\n\nEst. cost: unknown — no receipt history for these ingredients.");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Lower(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim().ToLowerInvariant());

    private static bool WholeWord(string term, string text) =>
        term.Length > 0 && Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);
}
