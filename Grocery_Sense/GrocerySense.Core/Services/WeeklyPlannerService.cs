using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// Port of reference-python/.../services/weekly_planner_service.py — picks a week of recipes via
// MealSuggestionService, aggregates their ingredients (deduped, best-effort item-mapped), and optionally
// writes them to the shopping list. Persist runs in one transaction.
public sealed class WeeklyPlannerService
{
    private readonly MealSuggestionService _meals;
    private readonly IngredientMappingService _mapper;
    private readonly SqliteConnectionFactory _factory;
    private readonly PlanCostService? _planCost;   // null only in legacy test construction
    private readonly SmartWeekService? _smartWeek; // supplies recentlyUsedRecipeIds (variety)

    public WeeklyPlannerService(MealSuggestionService meals, IngredientMappingService mapper,
        SqliteConnectionFactory factory, PlanCostService? planCost = null, SmartWeekService? smartWeek = null)
    {
        _meals = meals;
        _mapper = mapper;
        _factory = factory;
        _planCost = planCost;
        _smartWeek = smartWeek;
    }

    public WeeklyPlan BuildWeeklyPlan(int numRecipes = 6) =>
        FinishPlan(_meals.SuggestMealsForWeek(maxRecipes: numRecipes));

    // ---- V3 Smart Week build (Phase 3): quantity-aware, cap on the INCREMENTAL basis (grill Q8) ----

    // Candidate scores already include the protein/whole-food soft bonuses; the previous confirmed plan's
    // recipes feed the variety score. With a cap: recipes priced below the 0.7 coverage bar are budget-
    // ineligible (counted, disclosed — grill Q9); the pick is count-first greedy + score-improving swaps
    // (the SelectUnderBudget pattern) over SOLO incremental costs. Solo costs are an additive
    // approximation for the cap check — shared ingredients only make the real total CHEAPER, so the
    // check never overshoots the cap; the returned estimate is the exact plan-level number with sharing.
    // targetIngredients: Cook-This-Deal entry (V3 F2) — candidates are filtered to recipes actually using
    // them (engine ranks by overlap); allergies/exclusions still apply before ranking.
    public SmartWeekBuild BuildSmartWeek(int numRecipes = 6, int householdServings = 0, double? groceryCap = null,
        IEnumerable<string>? targetIngredients = null)
    {
        if (_planCost is null)
            throw new InvalidOperationException("BuildSmartWeek requires PlanCostService (DI-constructed).");

        var recent = _smartWeek?.RecentRecipeIds();
        var candidates = _meals.SuggestMealsForWeek(targetIngredients: targetIngredients,
            maxRecipes: int.MaxValue, recentlyUsedRecipeIds: recent);

        List<SuggestedMeal> picked;
        int skippedOverBudget = 0, skippedLowCoverage = 0;
        if (groceryCap is not { } cap)
        {
            picked = candidates.Take(numRecipes).ToList();
        }
        else
        {
            var solo = candidates.ToDictionary(c => c,
                c => _planCost.EstimatePlanCost([c.Recipe], householdServings));
            var usable = candidates
                .Where(c => solo[c].CoverageRatio >= PlanCostService.MinCoverageForBudget).ToList();
            skippedLowCoverage = candidates.Count - usable.Count;

            picked = new List<SuggestedMeal>();
            var total = 0.0;
            foreach (var c in usable.OrderBy(c => solo[c].IncrementalTotal).ThenByDescending(c => c.TotalScore))
            {
                if (picked.Count >= numRecipes) break;
                if (total + solo[c].IncrementalTotal > cap) continue;
                picked.Add(c);
                total += solo[c].IncrementalTotal;
            }
            foreach (var cand in usable.Except(picked).OrderByDescending(c => c.TotalScore).ToList())
                foreach (var worst in picked.Where(p => p.TotalScore < cand.TotalScore)
                             .OrderBy(p => p.TotalScore).ToList())
                {
                    var newTotal = total - solo[worst].IncrementalTotal + solo[cand].IncrementalTotal;
                    if (newTotal > cap) continue;
                    picked[picked.IndexOf(worst)] = cand;
                    total = newTotal;
                    break;
                }

            // Budget only "rejected" leftovers when the CAP stopped us, not the requested meal count.
            skippedOverBudget = picked.Count < numRecipes ? usable.Count - picked.Count : 0;
            picked = picked.OrderByDescending(p => p.TotalScore).ToList();
        }

        var estimate = _planCost.EstimatePlanCost(picked.Select(p => p.Recipe).ToList(), householdServings);
        var alternatives = candidates.Where(c => !picked.Contains(c)).Take(10).ToList();
        return new SmartWeekBuild(picked, alternatives, estimate, skippedOverBudget, skippedLowCoverage);
    }

    // Re-price an explicit selection (after the user swaps/removes meals in the UI).
    public PlanCostEstimate EstimateSelection(IReadOnlyList<Recipe> recipes, int householdServings = 0)
    {
        if (_planCost is null)
            throw new InvalidOperationException("EstimateSelection requires PlanCostService (DI-constructed).");
        return _planCost.EstimatePlanCost(recipes, householdServings);
    }

    // Aggregate + map/annotate — shared by the plain and budget-capped builders.
    private WeeklyPlan FinishPlan(IReadOnlyList<SuggestedMeal> suggestions)
    {
        _mapper.InvalidateChoices(); // pick up items added since the last build
        var planned = AggregateIngredients(suggestions).Select(ing =>
        {
            var res = _mapper.MapToItem(ing.Name);
            return res.ItemId is not null
                ? ing with { ItemId = res.ItemId, CanonicalName = res.CanonicalName,
                             MatchConfidence = res.Confidence, MatchMethod = res.Method }
                : ing with { MatchConfidence = res.Confidence, MatchMethod = res.Method };
        }).ToList();
        _mapper.FlushLearnedAliases();
        return new WeeklyPlan(suggestions, AnnotateLikelyHave(planned));
    }

    // Below this fraction of priced ingredients, a cost estimate understates the real total badly enough
    // to make a budget promise dishonest — such recipes count as unpriced for budgeting.
    internal const double MinKnownRatioForBudget = 0.5;

    // "N dinners around $cap": count-first selection over cost estimates. Recipes with no (or too-partial)
    // estimate are excluded and counted — a budget plan must never include a meal it can't price.
    public BudgetedWeeklyPlan BuildWeeklyPlanUnderBudget(double budgetCap, int numRecipes = 6)
    {
        var candidates = _meals.SuggestMealsForWeek(maxRecipes: int.MaxValue);
        var (picked, over, noCost, total) = SelectUnderBudget(candidates, budgetCap, numRecipes);
        var plan = FinishPlan(picked);
        var avgKnown = picked.Count > 0 ? picked.Average(p => p.CostKnownRatio) : 0.0;
        return new BudgetedWeeklyPlan(plan, budgetCap, total, over, noCost, avgKnown);
    }

    // Count-first selection (internal for direct test coverage).
    // Pass 1: cheapest-first greedy — optimal for meal count under a sum cap (guaranteed).
    // Pass 2: swap in higher-score unpicked meals where the cap still holds (count preserved). BEST-EFFORT
    // heuristic only — a single swap can miss a better multi-item combination; bounded knapsack DP is the
    // upgrade if a proven score optimum ever matters.
    internal static (List<SuggestedMeal> Picked, int SkippedOverBudget, int SkippedNoEstimate, double Total)
        SelectUnderBudget(IReadOnlyList<SuggestedMeal> candidates, double budgetCap, int maxRecipes)
    {
        var usable = new List<SuggestedMeal>();
        var noEstimate = 0;
        foreach (var s in candidates)
        {
            if (s.CostTotal is double && s.CostKnownRatio >= MinKnownRatioForBudget) usable.Add(s);
            else noEstimate++;
        }

        var picked = new List<SuggestedMeal>();
        var total = 0.0;
        foreach (var s in usable.OrderBy(s => s.CostTotal!.Value).ThenByDescending(s => s.TotalScore))
        {
            if (picked.Count >= maxRecipes) break;
            if (total + s.CostTotal!.Value > budgetCap) continue;
            picked.Add(s);
            total += s.CostTotal.Value;
        }

        foreach (var candidate in usable.Except(picked).OrderByDescending(s => s.TotalScore).ToList())
        {
            foreach (var worst in picked.Where(p => p.TotalScore < candidate.TotalScore)
                         .OrderBy(p => p.TotalScore).ToList())
            {
                var newTotal = total - worst.CostTotal!.Value + candidate.CostTotal!.Value;
                if (newTotal > budgetCap) continue;
                picked[picked.IndexOf(worst)] = candidate;
                total = newTotal;
                break;
            }
        }

        // Leftovers count as "over budget" only when the BUDGET stopped us — if the maxRecipes cap was
        // reached, the remaining affordable recipes were not rejected by price.
        var overBudget = picked.Count < maxRecipes ? usable.Count - picked.Count : 0;
        return (picked.OrderByDescending(p => p.TotalScore).ToList(), overBudget, noEstimate, total);
    }

    // Public so the UI can persist the exact plan the user reviewed, rather than rebuilding it at click time
    // (a rebuild re-reads _numRecipes + live DB/deal state and can diverge from what was shown).
    public void PersistToShoppingList(WeeklyPlan plan, string? addedBy = null)
    {
        var by = string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim();

        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        foreach (var ing in plan.PlannedIngredients)
        {
            var notes = new List<string>();
            if (ing.RecipeNames.Count > 0) notes.Add("Used in: " + string.Join(", ", ing.RecipeNames));
            if (ing.LikelyHave && ing.LikelyHaveReason is { } why) notes.Add($"May already have: {why}");
            if (ing.ItemId is not null && ing.MatchConfidence is not null)
            {
                var label = ing.CanonicalName ?? $"item_id={ing.ItemId}";
                notes.Add($"Mapped: {label} ({ing.MatchConfidence.Value.ToString("0.00", CultureInfo.InvariantCulture)}, {ing.MatchMethod})");
            }
            ShoppingListRepo.AddItem(conn, (ing.Name ?? "").Trim(), Math.Max(1.0, ing.ApproximateCount),
                unit: "each", notes: notes.Count > 0 ? string.Join(" | ", notes) : "",
                addedBy: by, itemId: ing.ItemId, tx: tx);
        }
        tx.Commit();
    }

    // Zero-effort pantry hint: an item bought (per receipts) more recently than this fraction of its usual
    // purchase interval is probably still in the pantry. Locked decision 2026-07-09: hint only, never blocks.
    internal const double LikelyHaveCadenceFraction = 0.75;

    private List<PlannedIngredient> AnnotateLikelyHave(List<PlannedIngredient> planned)
    {
        var ids = planned.Where(p => p.ItemId is not null).Select(p => p.ItemId!.Value).Distinct().ToList();
        if (ids.Count == 0) return planned;

        using var conn = _factory.Open();
        var lastMap = PricesRepo.GetLastReceiptPurchaseBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);
        var cadence = PricesRepo.GetPurchaseCadenceBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);

        var today = DateOnly.FromDateTime(DateTime.Now); // local calendar date (V3 local-date convention)
        return planned.Select(p =>
        {
            if (p.ItemId is not { } id) return p;
            if (!lastMap.TryGetValue(id, out var lastIso) || !DateOnly.TryParse(lastIso, out var last)) return p;
            var (avgInterval, _) = cadence.GetValueOrDefault(id, (null, null));
            if (avgInterval is not > 0) return p; // no cadence -> no inference (never guess)

            var daysSince = today.DayNumber - last.DayNumber;
            if (daysSince < 0 || daysSince >= avgInterval.Value * LikelyHaveCadenceFraction) return p;

            var intervalDays = (int)Math.Round(avgInterval.Value, MidpointRounding.AwayFromZero);
            return p with
            {
                LikelyHave = true,
                LikelyHaveReason = $"bought {daysSince} day(s) ago; you buy this every ~{intervalDays} day(s)",
            };
        }).ToList();
    }

    // Dedup ingredients across the week's recipes by normalized name; track the recipes using each; sort by
    // count desc then name. Pure helper (no DB) — unit-tested directly.
    internal static List<PlannedIngredient> AggregateIngredients(IEnumerable<SuggestedMeal> suggestions)
    {
        var agg = new Dictionary<string, (string Display, SortedSet<string> Recipes, int Count)>();

        foreach (var s in suggestions)
        {
            var recipeName = s.Recipe.Name.Length > 0 ? s.Recipe.Name : "Unnamed Recipe";
            foreach (var ing in s.Recipe.Ingredients)
            {
                var norm = NormalizeName(ing);
                if (norm.Length == 0) continue;
                if (!agg.TryGetValue(norm, out var e))
                    e = (ing.Trim(), new SortedSet<string>(StringComparer.Ordinal), 0);
                e.Recipes.Add(recipeName);
                agg[norm] = (e.Display, e.Recipes, e.Count + 1);
            }
        }

        return agg.Values
            .Select(e => new PlannedIngredient(e.Display, e.Recipes.ToList(), e.Count))
            .OrderByDescending(p => p.ApproximateCount)
            .ThenBy(p => p.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeName(string name) =>
        string.Join(" ", name.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
