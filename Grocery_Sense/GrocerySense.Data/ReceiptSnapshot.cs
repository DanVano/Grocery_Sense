using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrocerySense.Data;

// Backup/restore snapshot of a receipt and everything that references it (port of
// receipts_repo._snapshot_receipt's JSON structure). Money stays decimal — System.Text.Json writes
// decimal as an exact JSON number and round-trips it losslessly. Serialized via the source-gen context
// below so the backup path stays AOT/trim-safe on Android.
internal sealed class ReceiptSnapshot
{
    public SnapReceipt Receipt { get; set; } = new();
    public SnapRawJson? RawJson { get; set; }
    public List<SnapLineItem> LineItems { get; set; } = new();
    public List<SnapPrice> Prices { get; set; } = new();
    public List<SnapFileHash> FileHashes { get; set; } = new();
    public List<SnapSignature> Signatures { get; set; } = new();
    // V3 trips ledger (grill Q12): the receipt's close-out row, if it was closed. CASCADE deletes it with
    // the receipt, so without this capture "Delete (backup kept)" would silently lose the ledger entry.
    // Null on pre-V3 backups — restore simply skips it.
    public SnapTrip? Trip { get; set; }
}

internal sealed class SnapTrip
{
    public int? StoreId { get; set; }
    public string? TripDate { get; set; }
    public decimal? PlannedEstimate { get; set; }
    public string? PlannedEstimateBasis { get; set; }
    public int? PlannedUnknownCount { get; set; }
    public decimal? ActualTotal { get; set; }
    public decimal? RealizedSaving { get; set; }
    public string? SavingBasis { get; set; }
    public int MappedLineCount { get; set; }
    public int QualifyingLineCount { get; set; }
    public int MatchedPlannedCount { get; set; }
    public int UnplannedCount { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapReceipt
{
    public int Id { get; set; }
    public int? StoreId { get; set; }
    public string? PurchaseDate { get; set; }
    public decimal? SubtotalAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Source { get; set; }
    public string? FilePath { get; set; }
    public int? ImageOverallConfidence { get; set; }
    public string? KeepImageUntil { get; set; }
    public string? AzureRequestId { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapRawJson
{
    public int? ReceiptId { get; set; }
    public string? OperationId { get; set; }
    public string? JsonPath { get; set; }
    public string? RawJson { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapLineItem
{
    public int? LineIndex { get; set; }
    public int? ItemId { get; set; }
    public string? Description { get; set; }
    public double? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public decimal? Discount { get; set; }
    public int? Confidence { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapPrice
{
    public int? ItemId { get; set; }
    public int? StoreId { get; set; }
    public int? FlyerSourceId { get; set; }
    public string? Source { get; set; }
    public string? Date { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? Unit { get; set; }
    public double? Quantity { get; set; }
    public decimal? TotalPrice { get; set; }
    public string? RawName { get; set; }
    public int? Confidence { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapFileHash
{
    public string? FileHash { get; set; }
    public string? FilePath { get; set; }
    public string? CreatedAt { get; set; }
}

internal sealed class SnapSignature
{
    public string? Signature { get; set; }
    public string? CreatedAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReceiptSnapshot))]
internal sealed partial class ReceiptSnapshotContext : JsonSerializerContext;
