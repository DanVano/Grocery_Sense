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
    string? Notes = null);

public record Item(
    int Id,
    string CanonicalName,
    string? Category = null,
    string? DefaultUnit = null,
    double? TypicalPackageSize = null,
    string? TypicalPackageUnit = null,
    bool IsTracked = true,
    string? Notes = null);

// Item-manager list row: an item plus light price stats (port of items_admin_repo.ItemRow).
public record ItemAdminRow(
    int Id, string CanonicalName, bool IsTracked, string? DefaultUnit, int PricePoints, string? LastPriceDate);

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

// Best-effort current quote for an item/store (ports the dicts prices_repo returns). Unit is populated
// for active-flyer quotes (norm_unit/unit/'each'); null for the most-recent-price fallback.
public record PriceQuote(double UnitPrice, string Source, string? Unit = null);

// Repo-local return shapes (ports of dataclasses defined inside *_repo.py).
// Mirrors shopping_list_repo.ShoppingListRow: quantity/unit/category/notes are coalesced non-null,
// and is_active is carried (list_all_items / get_item return inactive rows too).
public record ShoppingListRow(
    int Id,
    string DisplayName,
    double Quantity,
    string Unit,
    string Category,
    bool IsCheckedOff,
    string Notes,
    string? AddedBy,
    int? AddedByMemberId,
    bool IsActive,
    int? PlannedStoreId,
    int? ItemId = null,
    string Priority = "normal");   // must_have | normal | wait_for_sale (shopping_list.priority)

// user_recipes row (UserRecipesRepo). List columns are JSON arrays of strings in the DB.
public record UserRecipeRow(int Id, string Name, int? Servings,
    IReadOnlyList<string> Ingredients, IReadOnlyList<string> Steps, IReadOnlyList<string> Tags,
    string? CreatedAt = null);

// Receipt read shapes (ports of the row dicts in receipts_repo.py). Money is decimal (TEXT-backed).
public record ReceiptSummary(
    int Id, string PurchaseDate, decimal? TotalAmount, decimal? SubtotalAmount, decimal? TaxAmount,
    int StoreId, string StoreName, string? FilePath, string? CreatedAt, int ItemCount);

public record ReceiptDetail(
    int Id, string PurchaseDate, decimal? TotalAmount, decimal? SubtotalAmount, decimal? TaxAmount,
    int StoreId, string StoreName, string? FilePath, string Source, string? AzureRequestId, string? CreatedAt);

public record ReceiptLineItemRow(
    int Id, int LineIndex, int? ItemId, string CanonicalName, string Description,
    double? Quantity, decimal? UnitPrice, decimal? LineTotal, decimal? Discount, int? Confidence);

// Ingest write inputs (built by ReceiptIngestionService, written atomically by ReceiptsRepo.IngestReceipt).
// Money stays as double through parsing; the repo converts to decimal at the receipts/line-items boundary
// and binds prices as double (matching how the rest of the prices layer treats unit_price).
public record ReceiptIngestLine(
    int LineIndex, int? ItemId, string Description, double? Quantity, double? UnitPrice, double? LineTotal,
    double? Discount, int? Confidence, string Unit, double? NormUnitPrice, string? NormUnit, string? NormNote);

public record ReceiptIngest(
    int StoreId, string PurchaseDate, double? Subtotal, double? Tax, double? Total, string FilePath,
    int? ImageConfidence, string OperationId, string? JsonPath, string RawJson, string? FileHash,
    string? Signature, IReadOnlyList<ReceiptIngestLine> Lines);

// A family meal-pick request (port of member_requests_repo.MemberRequestRow). ItemRowIds are the
// shopping_list ids the pick created, so a parent review can undo exactly those rows.
public record MemberRequestRow(
    int Id, int? MemberId, string MemberName, string Kind, string Label,
    IReadOnlyList<int> ItemRowIds, string CreatedAt, bool Reviewed);

// Doubles as the spend-trend point (GetSpendTrend) — same month/total/count shape.
public record MonthSpend(string Month, decimal Total, int ReceiptCount);

public record StoreMonthSpend(int StoreId, string StoreName, decimal Total, int ReceiptCount);

public record DeletedBackup(int BackupId, int? OriginalReceiptId, string? DeletedAt);

// Flyer deal row (port of the flyer_deals shape). Money columns are decimal (TEXT-backed). Used as
// both the insert input (Id/CreatedAt ignored on write) and the read shape.
public record FlyerDeal(
    int Id, int FlyerId, int? AssetId, int StoreId, int? PageIndex,
    string? Title, string? Description, string? PriceText,
    double? DealQty, decimal? DealTotal, decimal? UnitPrice, string? Unit,
    decimal? NormUnitPrice, string? NormUnit, string? NormNote,
    int? ItemId, double? MappingConfidence, double? Confidence, string? CreatedAt);

public record ItemAlias(
    int Id,
    string AliasText,
    int ItemId,
    double Confidence,
    string Source,
    string? CreatedAt,
    string? LastSeenAt,
    int TimesSeen);


// Savings watchlist row (watchlist table, joined to items for ItemName). TargetPrice null => watch for any
// good deal (percent-below-usual). CreatedAt is null on insert inputs, set when read back.
public record SavingsWatchItem(
    int Id, int ItemId, string ItemName, double? TargetPrice, bool IsActive = true, string? CreatedAt = null);
