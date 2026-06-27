namespace GrocerySense.Core;

// Port of reference-python/.../services/deals_service.py — cache-backed external deal search +
// store-minimizing selection. Mostly future-facing (the provider is the Flipp stub). Don't fake deals.
public sealed class DealsService
{
    public IReadOnlyDictionary<string, IReadOnlyList<Deal>> GroupDealsByStore(IReadOnlyList<Deal> deals)
        => throw new NotImplementedException();

    public IReadOnlyList<string> ChooseStoresMinTrips(IReadOnlyDictionary<string, IReadOnlyList<Deal>> byStore,
        bool allowSingletonForMeat = true, IReadOnlyList<string>? storePriority = null) => throw new NotImplementedException();

    public IReadOnlyList<Deal> SearchDeals(string term, string? postalCode = null, int maxAgeDays = 7,
        string locale = "en-CA") => throw new NotImplementedException();
}
