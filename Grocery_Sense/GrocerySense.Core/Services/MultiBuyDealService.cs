namespace GrocerySense.Core;

// Port of reference-python/.../services/multibuy_deal_service.py — "2/$5", "3 for 10", "2 @ 4.00",
// "BOGO" -> effective unit price. The only place deal strings are parsed. GOLDEN-TEST before porting.
public sealed class MultiBuyDealService
{
    public DealAdjusted Adjust(string description, double? quantity, double? unitPrice, double? lineTotal, double? discount)
        => throw new NotImplementedException();
}
