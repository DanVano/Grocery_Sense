namespace GrocerySense.Core.Abstractions;

public interface IFlyerProvider
{
    Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
        string storeName, string postalCode, CancellationToken ct = default);
}
