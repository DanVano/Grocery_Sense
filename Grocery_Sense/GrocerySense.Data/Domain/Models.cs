namespace GrocerySense.Domain;

// Domain records — ports of reference-python/src/Grocery_Sense/domain/models.py (@dataclass).
// Pure data, no behavior. Live in the Data project so Data has no upward dependency;
// Core/Integrations/App see them through their reference to Data.

public record Store(
    int Id,
    string Name,
    string? Address = null,
    string? City = null,
    string? PostalCode = null,
    string? FlippStoreId = null,
    bool IsFavorite = false,
    int Priority = 0,
    bool ShopHere = true,
    bool IsActive = true,
    string? Notes = null,
    double? DistanceKm = null);

public record Item(
    int Id,
    string CanonicalName,
    string? Category = null,
    string? DefaultUnit = null,
    double? TypicalPackageSize = null,
    string? TypicalPackageUnit = null,
    bool IsTracked = true,
    string? Notes = null);

public record Receipt(
    int Id,
    int StoreId,
    string PurchaseDate,
    double? SubtotalAmount = null,
    double? TaxAmount = null,
    double? TotalAmount = null,
    string Source = "receipt",
    string? FilePath = null,
    int? ImageOverallConfidence = null,
    string? KeepImageUntil = null,
    string? AzureRequestId = null);

public record PricePoint(
    int Id,
    int ItemId,
    int StoreId,
    string Source,
    string Date,
    double UnitPrice,
    string Unit,
    double? Quantity = null,
    double? TotalPrice = null,
    int? ReceiptId = null,
    int? FlyerSourceId = null,
    string? RawName = null,
    int? Confidence = null,
    double? NormUnitPrice = null,
    string? NormUnit = null);

public record PriceStats(
    int ItemId,
    int? StoreId,
    double? MinPrice,
    double? MaxPrice,
    double? AvgPrice,
    int Count);

public record ShoppingListItem(
    int Id,
    string DisplayName,
    double? Quantity = null,
    string? Unit = null,
    int? ItemId = null,
    int? PlannedStoreId = null,
    string? AddedBy = null,
    string? AddedAt = null,
    bool IsCheckedOff = false,
    bool IsActive = true,
    string? Notes = null,
    string? Category = null,
    int? AddedByMemberId = null);

// Repo-local return shapes (ports of dataclasses defined inside *_repo.py).
public record ShoppingListRow(
    int Id,
    string DisplayName,
    double? Quantity,
    string? Unit,
    string? Category,
    int? ItemId,
    int? PlannedStoreId,
    string? AddedBy,
    int? AddedByMemberId,
    bool IsCheckedOff,
    string? Notes);

public record ItemAlias(
    int Id,
    string AliasText,
    int ItemId,
    double Confidence,
    string Source,
    string? CreatedAt,
    string? LastSeenAt,
    int TimesSeen);

public record ItemRow(
    int Id,
    string CanonicalName,
    bool IsTracked,
    string? DefaultUnit,
    int PricePoints,
    string? LastPriceDate);

public record MemberRequestRow(
    int Id,
    int? MemberId,
    string MemberName,
    string Kind,
    string Label,
    IReadOnlyList<int> ItemRowIds,
    string? CreatedAt,
    bool Reviewed);

public record StoreRow(int Id, string Name);
