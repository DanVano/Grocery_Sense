namespace GrocerySense.Core;

// Service result types — ports of the @dataclass results defined across reference-python/.../services/.
// Grouped here to keep one file; split out if any grows real behavior.

public record NormalizedPrice(double NormUnitPrice, string NormUnit, string Note);

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

// Basket optimizer (trimmed; port the full BasketItemPlan/PricePick detail from basket_optimizer_service.py).
public record PricePick(int StoreId, string StoreName, double? UnitPrice, string Unit, string Source);
public record StorePlan(int StoreId, string StoreName, double TotalEstimated, int UnknownCount);
public record BasketOptimizationResult(
    string Mode,
    IReadOnlyList<StorePlan> Stores,
    double BasketTotalEstimated,
    double? SaveVsUsualAvg,
    double? SaveVsLowest,
    IReadOnlyList<string> Warnings);

public record FlyerIngestResult(int BatchesCreated, int DealsCreated, IReadOnlyList<string> SkippedUrls, IReadOnlyList<string> Errors);

public record FlyerSyncResult(int StoresSynced, int DealsInserted, string? SkippedReason, IReadOnlyList<string> Errors)
{
    public bool Ran => SkippedReason is null;
}

public record PriceDropAlert(int ItemId, int StoreId, double BaselineUnitPrice, double? CurrentUnitPrice, double DropPct);

public record IngestOutcome(int? ReceiptId, bool WasDuplicate, string? OperationId, string? Error);

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
    Household Household);
