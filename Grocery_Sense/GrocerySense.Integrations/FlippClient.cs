namespace GrocerySense.Integrations;

// Port of reference-python/.../integrations/flipp_client.py — STILL A STUB.
// Returns empty until a real provider is wired. Do NOT fabricate deals (fail-loud rule).
// Keep behind a provider seam (IFlyerProvider) once a second provider appears.
public sealed class FlippClient
{
    public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
        string storeName, string postalCode, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());
}
