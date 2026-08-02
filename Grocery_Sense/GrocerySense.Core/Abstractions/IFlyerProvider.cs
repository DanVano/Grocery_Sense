namespace GrocerySense.Core.Abstractions;

// One deal exactly as a provider reports it: raw text and prices, no item mapping and no unit
// normalization (DealEnricher owns those, downstream). Providers hand-construct this from their own
// JSON navigation, so it is never deserialized and needs no source-gen context for the AOT heads.
// Price is the advertised amount for the offer as a whole ("2/$5" -> 5); UnitPrice is per unit when the
// provider states one. Both are best-effort — DealEnricher re-derives the effective unit price from PriceText.
public sealed record ProviderDeal(
    string Title,
    string? Description = null,
    string? PriceText = null,
    double? Price = null,
    double? UnitPrice = null,
    string? Unit = null,
    string? ValidFrom = null,
    string? ValidTo = null,
    int? PageIndex = null);

public interface IFlyerProvider
{
    Task<IReadOnlyList<ProviderDeal>> FetchFlyersForStoreAsync(
        string storeName, string postalCode, CancellationToken ct = default);
}
