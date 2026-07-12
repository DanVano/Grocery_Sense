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
// ProjectedSpend linearly extrapolates the current pace to month-end; ProjectedStatus is that projection
// graded against the budget (unset when no budget is configured).
public record BudgetStatus(
    string Month, decimal Spent, int ReceiptCount, decimal? Budget, decimal? Remaining,
    double? PctUsed, bool? OverBudget, string Status,
    decimal ProjectedSpend, string ProjectedStatus);

// Outcome of the single-scan alert hook (A7). Opened is surfaced in-app regardless of Notified so the deny
// path (notifications off) still shows the "N new price alert(s)" line.
public record ScanAlertResult(int Opened, bool Notified);

// Budget year-over-year context (Stage 4 I3). SpendYoyPct is the household's own spend change (NOT a price
// index); FoodInflationPct is the current-year rate from the editable table (null when that year is absent).
// EnoughHistory is false when this month or the same month last year has no receipts — surface an honest
// "not enough history yet" instead of a fabricated number.
public record InflationContext(double? SpendYoyPct, double? FoodInflationPct, bool EnoughHistory);

public record DealAdjusted(double Quantity, double? UnitPrice, double? LineTotal, string DealNote);

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

// PlanningService results — typed port of the dict build_plan_for_active_list returns.
// EstimatedSubtotal is null when no line in the group could be priced.
public record PlanStoreGroup(
    Store Store, IReadOnlyList<ShoppingListRow> Items, double? EstimatedSubtotal,
    int EstimatedItems, int MissingItems);

public record PlanCoverage(int TotalItems, int EstimatedItems, int MissingItems);

public record PlanCosts(
    double? BasketTotalEstimate, Store? BaselineStore, double? BaselineTotalEstimate,
    double? EstimatedSavings, PlanCoverage Coverage);

public record StorePlanResult(
    IReadOnlyDictionary<int, PlanStoreGroup> Stores, IReadOnlyList<ShoppingListRow> Unassigned,
    string Summary, PlanCosts Costs);

// Outcome of a manual flyer ingest (one batch per call). Mirrors the Python FlyerIngestResult.
public record FlyerIngestResult(int FlyerId, int AssetsCount, int DealsCount, int RawJsonCount);

public record FlyerSyncResult(int StoresSynced, int DealsInserted, string? SkippedReason, IReadOnlyList<string> Errors)
{
    public bool Ran => SkippedReason is null;
}

// One price-drop alert (computed by the engine and/or persisted). SuggestedQty/Note are populated on compute
// for stock-up alerts and persisted since migration 6 (rows written earlier read back null).
// Id/CreatedAt/Status are null on compute and set when read back from price_drop_alerts.
public record PriceDropAlert(
    int ItemId, string ItemName, int StoreId, string StoreName,
    double CurrentPrice, double? UsualPrice, double? PctBelowUsual,
    double? SixMonthLow, double? PctAboveLow, string AlertKind,
    bool IsStaple, int ReceiptSamples, string Basis, string Source,
    string? LastSeenAtOrBelow, string Notes,
    double? SuggestedQty = null, string? SuggestedQtyNote = null,
    int? Id = null, string? CreatedAt = null, string? Status = null);

// Aisle-view intel for one active shopping-list row (ShoppingInsightsService). Badge: stock_up | buy | wait |
// none — strongest wins (stock_up > buy > wait); "none" also covers missing price/history (never guess).
// CurrentPrice is the quote at the row's planned store only — a missing price there is disclosed as unpriced,
// not silently swapped for another store's price. Rows without a planned store use the cheapest shop-here quote.
public sealed record ListItemInsight(
    ShoppingListRow Row, double? CurrentPrice, string? PriceSource, string? PriceUnit,
    double? UsualPrice, double? PctBelowUsual, double? SixMonthLow, string Badge,
    double? SuggestedQty = null, string? SuggestedQtyNote = null);

// One Shop Mode store group. StoreId null => rows with no planned store ("Unassigned" — run Plan → Apply).
public sealed record ShopModeGroup(
    int? StoreId, string StoreName, IReadOnlyList<ListItemInsight> Items,
    double SubtotalEstimated, int UnpricedCount);

// A cheaper same-category alternative at the row's planned store (ShoppingInsightsService swaps).
public sealed record SwapSuggestion(
    int RowId, string ForName, string SwapToName, double SwapPrice, double CurrentPrice, double SavePct);

// CoverageNote is the disclosed degrade when too few list items carry a category to suggest honestly.
public sealed record SwapResult(IReadOnlyList<SwapSuggestion> Suggestions, string? CoverageNote);

// A watchlist item whose current best price cleared its trigger. HitReason: "target" (met the user's target
// price) | "below_usual" (no target set, but currently >= MinItemSavingPct below its usual price).
public record WatchlistHit(
    int WatchId, int ItemId, string ItemName, double? TargetPrice, double BestPrice, int StoreId,
    string StoreName, string Source, double? UsualPrice, double? PctBelowUsual, string HitReason);

// Outcome of a receipt ingest. DuplicateReason is "file_hash" | "signature" when WasDuplicate.
public record IngestOutcome(
    int? ReceiptId, bool WasDuplicate, string? OperationId, string? Error,
    string? DuplicateReason = null, bool ReplacedExisting = false);

// The result of PrepareReceiptFileAsync: either a decided duplicate (Duplicate != null, before the user is
// asked anything) or a ready-to-commit receipt (Ingest != null) awaiting a confirmed purchase date. OcrDate
// is the ISO date OCR actually found, or null — when null the caller MUST supply a date (backfill rule:
// never default an undated old receipt to today). FallbackDate is the single-scan path's mtime/today guess.
public sealed record ReceiptPrepared(
    ReceiptIngest? Ingest, string? OperationId, string? OcrDate, string FallbackDate,
    string Merchant, double? Total, int LineCount, bool ReplacedExisting, IngestOutcome? Duplicate)
{
    public bool OcrFoundDate => OcrDate is not null;
}

// Per-file result within a backfill batch import.
public enum BatchImportStatus { Imported, DuplicateFile, DuplicateSignature, Skipped, Failed, Cancelled }

public sealed record BatchImportItem(string FilePath, BatchImportStatus Status, int? ReceiptId, string? Detail);

public sealed record BatchImportSummary(IReadOnlyList<BatchImportItem> Items)
{
    public int Imported => Items.Count(i => i.Status == BatchImportStatus.Imported);
    public int Duplicates => Items.Count(i =>
        i.Status is BatchImportStatus.DuplicateFile or BatchImportStatus.DuplicateSignature);
    public int Skipped => Items.Count(i => i.Status == BatchImportStatus.Skipped);
    public int Failed => Items.Count(i => i.Status == BatchImportStatus.Failed);
    public int Cancelled => Items.Count(i => i.Status == BatchImportStatus.Cancelled);
}

// Flat meal/recipe profile the RecipeEngine + MealSuggestionService consume (port of the dict Python's
// get_meal_profile returns). Single-profile in v2; every list defaults empty so tests build just the fields
// they exercise (e.g. new MealProfile { Allergies = ["peanuts"] }).
public sealed record MealProfile
{
    public IReadOnlyList<string> Allergies { get; init; } = [];
    public IReadOnlyList<string> AvoidIngredients { get; init; } = [];
    public IReadOnlyList<string> Restrictions { get; init; } = [];
    public IReadOnlyList<string> PreferMeats { get; init; } = [];
    public IReadOnlyList<string> AvoidMeats { get; init; } = [];
    public IReadOnlyList<string> FavoriteTags { get; init; } = [];
}

// A scored meal suggestion (port of meal_suggestion_service.SuggestedMeal). Scores are the components that
// feed total = 0.5*price + 0.3*preference + 0.2*variety; cost fields are a disclosed partial estimate.
public sealed record SuggestedMeal(
    Recipe Recipe, double TotalScore, double PreferenceScore, double DealScore, double PriceScore,
    double VarietyScore, IReadOnlyList<string> Reasons,
    double? CostTotal = null, double? CostPerServing = null, double CostKnownRatio = 0.0);

// An aggregated shopping-list ingredient across a week's suggested meals, with best-effort item mapping.
// LikelyHave is a zero-effort pantry hint (receipt recency vs purchase cadence) — informational only,
// the ingredient is still added to the list.
public sealed record PlannedIngredient(
    string Name, IReadOnlyList<string> RecipeNames, int ApproximateCount,
    int? ItemId = null, string? CanonicalName = null, double? MatchConfidence = null, string? MatchMethod = null,
    bool LikelyHave = false, string? LikelyHaveReason = null);

public sealed record WeeklyPlan(IReadOnlyList<SuggestedMeal> Suggestions, IReadOnlyList<PlannedIngredient> PlannedIngredients);

// Household config — ports of config_store.py dataclasses.
public record HouseholdMember(int Id, string Name, string Role, Dictionary<string, object?> Profile);
// NextMemberId is the highest member id ever issued (monotonic). New members take NextMemberId+1 so a deleted
// member's id is never reused — reuse would re-attribute the old member's picks/history to the new one. Older
// configs lack the field and deserialize to 0; EnsureHousehold repairs it to at least the current max id.
public record Household(int PrimaryMemberId, int ActiveMemberId, IReadOnlyList<HouseholdMember> Members,
    int NextMemberId = 0);
public record UserConfig(
    int ProfileVersion,
    string PostalCode,
    string City,
    double? MonthlyBudget,
    Household Household,
    // BasketOptimizer settings (single-profile). Defaults are the redesign's tuning starting points.
    int MaxStores = 3,
    double MinItemSavingPct = 0.10,
    double MinStoreSaving = 5.0,
    // Editable CAD food-inflation rate table {year-string -> annual %}. null on older configs; ConfigStore
    // seeds InflationRates.Seed when absent and never overwrites a user-edited table (Stage 4 I0).
    IReadOnlyDictionary<string, double>? FoodInflationByYear = null);
