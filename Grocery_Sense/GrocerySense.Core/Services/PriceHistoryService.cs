using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/price_history_service.py — item history, baselines, deal classify.
public sealed class PriceHistoryService
{
    public Item GetOrCreateItem(string canonicalName, string? category = null, string? defaultUnit = null)
        => throw new NotImplementedException();

    public int RecordPriceFromReceipt(string itemName, int storeId, double unitPrice, string unit,
        string? dateStr = null, double? quantity = null, double? totalPrice = null, int? receiptId = null,
        string? rawName = null, int? confidence = null) => throw new NotImplementedException();

    public int RecordPriceFromFlyer(string itemName, int storeId, double unitPrice, string unit,
        string? dateStr = null, int? flyerSourceId = null, string? rawName = null, int? confidence = null)
        => throw new NotImplementedException();

    public Dictionary<string, object?>? GetItemStats(string itemName, int windowDays = 180) => throw new NotImplementedException();

    public double? GetBaselinePrice(string itemName, int windowDays = 90) => throw new NotImplementedException();

    public Dictionary<string, object?> ClassifyDeal(string itemName, double candidateUnitPrice, int windowDays = 180)
        => throw new NotImplementedException();
}
