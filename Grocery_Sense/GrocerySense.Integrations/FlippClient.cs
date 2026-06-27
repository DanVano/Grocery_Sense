using GrocerySense.Core.Abstractions;

namespace GrocerySense.Integrations;

// Stub until a real provider lands. Never fabricate deals.
public sealed class FlippClient : IFlyerProvider
{
    public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
        string storeName, string postalCode, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());
}
