using System.Text.Json.Serialization;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Service result types — ports of the @dataclass results defined across reference-python/.../services/.
// Grouped here to keep one file; split out if any grows real behavior.

public record NormalizedPrice(double NormUnitPrice, string NormUnit, string Note);

// Item price-history profile for the Items page (F02). Points are the most recent in the window,
// newest-first, store names resolved. Stats degrade honestly: UsualPrice null (Basis "unknown") on
// thin history, never a fabricated number.
public sealed record ItemPriceHistoryPoint(string Date, string StoreName, double UnitPrice, string Unit, string Source);
public sealed record ItemPriceProfile(
    IReadOnlyList<ItemPriceHistoryPoint> Points, double? UsualPrice, int UsualSamples, string UsualBasis,
    double? MinPrice, double? MaxPrice, int SampleCount, int WindowDays);

// PriceHistoryService result — typed replacement for the Python dict return.
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

// Outcome of a receipt ingest. DuplicateReason is "file_hash" | "signature" when WasDuplicate. Ingest
// failures THROW (the receipt never commits) — there is no error field to carry a failed outcome.
// ReplaceConflict is the fail-closed replace outcome (P0-1): the observed duplicate owner changed,
// appeared, or split between prepare and commit — nothing was deleted, nothing was imported, and the
// UI must say so distinctly (never folded into "duplicate" or "failed").
public record IngestOutcome(
    int? ReceiptId, bool WasDuplicate, string? OperationId,
    string? DuplicateReason = null, bool ReplacedExisting = false,
    bool ReplaceConflict = false, string? ConflictDetail = null);

// The single-scan workflow's result: the ingest outcome plus the price-alert pass. AlertError is set when the
// receipt imported but the post-commit alert scan threw — the receipt stays imported and its image is kept;
// only the alert enrichment failed. Ingest failures throw (the receipt never committed) and are not modeled here.
public sealed record ScanIngestOutcome(IngestOutcome Ingest, int AlertsOpened, string? AlertError = null);

// Outcome of importing a claimed shared batch (ScanIngestService.ImportSharedBatchAsync). Rejected counts
// the share-time rejections claimed with the batch. Cancelled / FailureMessage describe an aborted run —
// the failing and remaining copies were already handed to the deleteCopy callback, and counts up to the
// abort point are real.
public sealed record SharedImportSummary(
    int Imported, int Duplicates, int Conflicts, int Rejected,
    int AlertsOpened, int AlertFailures, bool Cancelled, string? FailureMessage);

// The result of PrepareReceiptFileAsync: either a decided duplicate (Duplicate != null, before the user is
// asked anything) or a ready-to-commit receipt (Ingest != null) awaiting a confirmed purchase date. OcrDate
// is the ISO date OCR actually found, or null — when null the caller MUST supply a date (backfill rule:
// never default an undated old receipt to today). FallbackDate is the single-scan path's mtime/today guess.
// ReplaceRequested + the two observed owner ids feed the commit transaction (P0-1): prepare only OBSERVES
// duplicates, never deletes — the commit re-reads both owners and deletes only what prepare observed.
public sealed record ReceiptPrepared(
    ReceiptIngest? Ingest, string? OperationId, string? OcrDate, string FallbackDate,
    string Merchant, double? Total, int LineCount, bool ReplaceRequested, IngestOutcome? Duplicate,
    int? FileHashOwnerId = null, int? SignatureOwnerId = null)
{
    public bool OcrFoundDate => OcrDate is not null;
}

// Per-file result within a backfill batch import. Conflict = replace fail-closed (see IngestOutcome).
public enum BatchImportStatus { Imported, DuplicateFile, DuplicateSignature, Skipped, Failed, Cancelled, Conflict }

public sealed record BatchImportItem(string FilePath, BatchImportStatus Status, int? ReceiptId, string? Detail);

public sealed record BatchImportSummary(IReadOnlyList<BatchImportItem> Items)
{
    public int Imported => Items.Count(i => i.Status == BatchImportStatus.Imported);
    public int Duplicates => Items.Count(i =>
        i.Status is BatchImportStatus.DuplicateFile or BatchImportStatus.DuplicateSignature);
    public int Skipped => Items.Count(i => i.Status == BatchImportStatus.Skipped);
    public int Failed => Items.Count(i => i.Status == BatchImportStatus.Failed);
    public int Cancelled => Items.Count(i => i.Status == BatchImportStatus.Cancelled);
    public int Conflicts => Items.Count(i => i.Status == BatchImportStatus.Conflict);
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
    double? CostTotal = null, double? CostPerServing = null, double CostKnownRatio = 0.0,
    // Pantry-aware marginal cost (null when the service has no DB factory): what this meal costs in NEW
    // spend after discounting likely-on-hand ingredients. Exact-name matching only (same keying as the
    // baseline cost estimate) — disclosed estimate, same caveats as CostTotal.
    double? MarginalCostTotal = null, int? NewIngredientCount = null,
    IReadOnlyList<string>? LikelyHaveIngredients = null);

// An aggregated shopping-list ingredient across a week's suggested meals, with best-effort item mapping.
// LikelyHave is a zero-effort pantry hint (receipt recency vs purchase cadence) — informational only,
// the ingredient is still added to the list.
public sealed record PlannedIngredient(
    string Name, IReadOnlyList<string> RecipeNames, int ApproximateCount,
    int? ItemId = null, string? CanonicalName = null, double? MatchConfidence = null, string? MatchMethod = null,
    bool LikelyHave = false, string? LikelyHaveReason = null);

public sealed record WeeklyPlan(IReadOnlyList<SuggestedMeal> Suggestions, IReadOnlyList<PlannedIngredient> PlannedIngredients);

// One flagged receipt line (TripReconciliationService). Single kind in the MVP: "flyer_below_paid" —
// the CURRENT flyer quote is below what was paid (check the receipt / register). Paid stays decimal
// (TEXT-backed); Expected is the double flyer quote it was judged against. ("above_usual" was cut:
// the receipt's own rows contaminate the usual median and lines carry no unit — can't be honest.)
public sealed record TripLineFlag(string ItemName, string Kind, decimal Paid, double Expected, string Note);

// Right-after-the-trip diff of one receipt vs the CURRENT shopping list and CURRENTLY-active flyer
// quotes. Reconcile REFUSES receipts older than RecentTripDays or future-dated (the UI also hides the
// button). MatchedPlanned/UnplannedCount are DISTINCT items, not line counts. Known limits disclosed
// in DataNote: unmapped lines never judged; unit-mismatched flyer quotes skipped.
public sealed record TripReconciliation(
    int ReceiptId, string StoreName, string PurchaseDate,
    int MatchedPlanned, int UnplannedCount, decimal UnplannedTotal,
    IReadOnlyList<TripLineFlag> Flags, IReadOnlyList<string> PlannedNotBought, string? DataNote);

// A weekly plan constrained to an ESTIMATED spending cap (WeeklyPlannerService.BuildWeeklyPlanUnderBudget).
// Selection is count-first (most meals), then best-effort score swaps. SkippedNoEstimate covers both
// unpriced recipes and partial estimates below MinKnownRatioForBudget (they'd understate cost);
// SkippedOverBudget = priced but didn't fit. AvgCostKnownRatio < 1 => even the qualifying estimates are
// partial — the UI must say "estimated".
public sealed record BudgetedWeeklyPlan(WeeklyPlan Plan, double BudgetCap, double EstimatedTotal,
    int SkippedOverBudget, int SkippedNoEstimate, double AvgCostKnownRatio);

// A kid-pickable recipe with a one-glance deal flag (Family page). OnSaleThisWeek = "uses ingredients
// that are on sale this week": DealScore > 0.2, i.e. >20% of ingredients have a live priced deal.
public sealed record PickableRecipe(string Name, bool OnSaleThisWeek);

// One overdue staple on the restock draft (StapleRestockService). No quantity suggestion on purpose:
// receipt quantities carry units (kg/L/each) that can't be honestly mapped onto a new list row.
public sealed record RestockSuggestion(int ItemId, string Name, int DaysSinceLast, int IntervalDays);

// Household config — ports of config_store.py dataclasses. Members are identity + role only; preferences
// are household-wide (see HouseholdPreferences below).
public record HouseholdMember(int Id, string Name, string Role);
// NextMemberId is the highest member id ever issued (monotonic). New members take NextMemberId+1 so a deleted
// member's id is never reused — reuse would re-attribute the old member's picks/history to the new one. Older
// configs lack the field and deserialize to 0; EnsureHousehold repairs it to at least the current max id.
public record Household(int PrimaryMemberId, int ActiveMemberId, IReadOnlyList<HouseholdMember> Members,
    int NextMemberId = 0);

// The household's ONE shared preference set. v2 decided members are names-only (brainstorm 2026-07-02 Q5);
// per-member profiles stay a v3 idea, and the v3 shape is overrides-on-a-baseline anyway, so nothing is
// lost by typing this now. Lists hold lowercase trimmed tokens — ConfigStore.Normalize enforces that,
// because user_config.json is hand-editable. Weights above 1.0 mark a preferred protein.
public record HouseholdPreferences(
    List<string> Allergies,
    List<string> HardExcludes,
    List<string> SoftExcludes,
    List<string> ExcludedProteins,
    List<string> FavoriteCuisines,
    Dictionary<string, double> PreferredProteinWeights)
{
    public static HouseholdPreferences Empty() => new([], [], [], [], [], []);
}

public record UserConfig(
    int ProfileVersion,
    string PostalCode,
    double? MonthlyBudget,
    Household Household,
    HouseholdPreferences? Preferences = null,
    // BasketOptimizer settings (single-profile). Defaults are the redesign's tuning starting points.
    int MaxStores = 3,
    double MinItemSavingPct = 0.10,
    double MinStoreSaving = 5.0,
    // Editable CAD food-inflation rate table {year-string -> annual %}. null on older configs; ConfigStore
    // seeds InflationRates.Seed when absent and never overwrites a user-edited table (Stage 4 I0).
    IReadOnlyDictionary<string, double>? FoodInflationByYear = null);
