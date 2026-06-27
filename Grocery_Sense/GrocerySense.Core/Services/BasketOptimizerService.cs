namespace GrocerySense.Core;

// Port of reference-python/.../services/basket_optimizer_service.py — the product's core value.
// mode "one_store" (fast) | "two_store" (savings). Uses active flyer prices, else recent history.
// Respects preferences: hard-excluded kept OUT (surfaced separately), soft-excluded starred.
// Trip penalty from distance_km * gas_cost_per_km. Port AFTER prices_repo batch readers + preferences.
public sealed class BasketOptimizerService
{
    public BasketOptimizationResult Optimize(string mode = "two_store") => throw new NotImplementedException();

    // Whole-word match so "olive" doesn't hit "olive oil" — port the phrase_safe_hit helper.
    public static bool PhraseSafeHit(string text, string term, IReadOnlyList<string>? safePhrases = null)
        => throw new NotImplementedException();
}
