using GrocerySense.Domain;

namespace GrocerySense.Core;

// Service result types — ports of the @dataclass results defined across reference-python/.../services/.
// Grouped here to keep one file; split out if any grows real behavior.

public record NormalizedPrice(double NormUnitPrice, string NormUnit, string Note);

// PriceHistoryService results — typed replacements for the Python dict returns.
public record ItemStats(Item Item, double? AvgUnitPrice, double? MinUnitPrice, double? MaxUnitPrice, int SampleCount);

public record StoreStats(
    double? AvgPrice, double? MinPrice, double? MaxPrice, int SampleCount, string UnitHint, string MostRecentDate);

public record DealClassification(
    Item? Item, bool HasHistory, string Classification, double? PercentVsAvg,
    double? AvgUnitPrice, double? MinUnitPrice, double? MaxUnitPrice, int SampleCount, string Message);

// BudgetService — this month's spend vs the configured budget. Status: unset | ok | warning | over.
public record BudgetStatus(
    string Month, decimal Spent, int ReceiptCount, decimal? Budget, decimal? Remaining,
    double? PctUsed, bool? OverBudget, string Status);

public record DealAdjusted(double Quantity, double? UnitPrice, double? LineTotal, string DealNote);

public record Deal(string Name, string Store, double? Price, string? Unit, Dictionary<string, object?>? Raw);

public record SuggestedMeal(
    Dictionary<string, object?> Recipe,
    double TotalScore,
    IReadOnlyList<string> Reasons,
    double? CostTotal,
    double? CostPerServing);

public record PlannedIngredient(
    string Name,
    IReadOnlyList<string> RecipeNames,
    int ApproximateCount,
    int? ItemId,
    string? CanonicalName,
    double? MatchConfidence);

public record WeeklyPlan(IReadOnlyList<SuggestedMeal> Suggestions, IReadOnlyList<PlannedIngredient> PlannedIngredients);

public record MappingResult(int? ItemId, string? CanonicalName, double Confidence, string Method, string NormalizedInput);

// One basket line in the plan. ChosenStoreId is null when the item is pulled out (hard-excluded). UnitPrice
// is null when no price is known (PriceUnknown) — such lines are excluded from the store/basket totals.
public record BasketItemPlan(
    int ItemId, string Name, int? ChosenStoreId, double? UnitPrice, string Unit, string Source,
    bool HardExcluded, bool PriceUnknown, double? SaveVsUsual, double? SaveVsLowest);

public record StorePlan(
    int StoreId, string StoreName, IReadOnlyList<BasketItemPlan> Items, double TotalEstimated, int UnknownCount);

public record BasketOptimizationResult(
    string Mode,
    IReadOnlyList<StorePlan> Stores,
    double BasketTotalEstimated,
    double? SaveVsUsualAvg,
    double? SaveVsLowest,
    IReadOnlyList<string> Warnings);

// Result of writing an optimizer plan back onto the active list (planned_store_id per item).
public record ApplyPlanResult(
    bool Ok, string Mode, string? PlanLabel, int Cleared, int Attempted, int Updated,
    int Assigned, int Unassigned, int UnassignedHardExcluded, IReadOnlyList<string> Warnings, string? Error);

public record FlyerIngestResult(int BatchesCreated, int DealsCreated, IReadOnlyList<string> SkippedUrls, IReadOnlyList<string> Errors);

public record FlyerSyncResult(int StoresSynced, int DealsInserted, string? SkippedReason, IReadOnlyList<string> Errors)
{
    public bool Ran => SkippedReason is null;
}

// One price-drop alert (computed by the engine and/or persisted). SuggestedQty/Note are populated on compute
// for stock-up alerts but NOT persisted (Python omits them too), so they're null when read from the table.
// Id/CreatedAt/Status are null on compute and set when read back from price_drop_alerts.
public record PriceDropAlert(
    int ItemId, string ItemName, int StoreId, string StoreName,
    double CurrentPrice, double? UsualPrice, double? PctBelowUsual,
    double? SixMonthLow, double? PctAboveLow, string AlertKind,
    bool IsStaple, int ReceiptSamples, string Basis, string Source,
    string? LastSeenAtOrBelow, string Notes,
    double? SuggestedQty = null, string? SuggestedQtyNote = null,
    int? Id = null, string? CreatedAt = null, string? Status = null);

// Outcome of a receipt ingest. DuplicateReason is "file_hash" | "signature" when WasDuplicate.
public record IngestOutcome(
    int? ReceiptId, bool WasDuplicate, string? OperationId, string? Error,
    string? DuplicateReason = null, bool ReplacedExisting = false);

// Recipe wrapper — port of recipes/recipe_engine.py Recipe.
public sealed record Recipe(Dictionary<string, object?> Raw)
{
    public object? Id => Raw.TryGetValue("id", out var v) ? v : null;
    public string Name => Raw.TryGetValue("name", out var v) ? v?.ToString() ?? "" : "";
}

// Household config — ports of config_store.py dataclasses.
public record HouseholdMember(int Id, string Name, string Role, Dictionary<string, object?> Profile);
public record Household(int PrimaryMemberId, int ActiveMemberId, IReadOnlyList<HouseholdMember> Members);
public record UserConfig(
    int ProfileVersion,
    string PostalCode,
    string City,
    string Country,
    Dictionary<string, int> StorePriority,
    IReadOnlyList<int> FavoriteStoreIds,
    double? MonthlyBudget,
    double GasCostPerKm,
    Household Household,
    // BasketOptimizer settings (single-profile). Defaults are the redesign's tuning starting points.
    int MaxStores = 3,
    double MinItemSavingPct = 0.10,
    double MinStoreSaving = 5.0);
