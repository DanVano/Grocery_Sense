using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GrocerySense.Tests;

public class DiResolutionSmokeTests
{
    private sealed class FakeOcrClient : IReceiptOcrClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
            => Task.FromResult(("fake-op", new Dictionary<string, object?>()));
    }

    private sealed class FakeFlyerProvider : IFlyerProvider
    {
        public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
            string storeName, string postalCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());
    }

    [Fact]
    public void Every_registered_service_resolves()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gs_smoke_{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddGrocerySenseCore(dbPath);
        services.AddSingleton<IReceiptOcrClient, FakeOcrClient>();
        services.AddSingleton<IFlyerProvider, FakeFlyerProvider>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        foreach (var descriptor in services)
            provider.GetRequiredService(descriptor.ServiceType);
    }
}
