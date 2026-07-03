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
