using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public class DiResolutionSmokeTests
{
    // AddGrocerySenseCore plus the four head-bound abstractions every registration graph needs.
    private static ServiceCollection CoreServices(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddGrocerySenseCore(dbPath);
        services.AddSingleton<IReceiptOcrClient, FakeOcrClient>();
        services.AddSingleton<IFlyerProvider, FakeFlyerProvider>();
        services.AddSingleton<IFlyerLayoutClient, FakeFlyerLayoutClient>();
        services.AddSingleton<ILocalNotifier, FakeLocalNotifier>();
        return services;
    }

    private sealed class FakeOcrClient : IReceiptOcrClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeReceiptFileAsync(
            string filePath, CancellationToken ct = default)
            => Task.FromResult(("fake-op", new Dictionary<string, object?>()));
    }

    private sealed class FakeFlyerProvider : IFlyerProvider
    {
        public Task<IReadOnlyList<ProviderDeal>> FetchFlyersForStoreAsync(
            string storeName, string postalCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderDeal>>(Array.Empty<ProviderDeal>());
    }

    private sealed class FakeFlyerLayoutClient : IFlyerLayoutClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default)
            => Task.FromResult(("fake-op", new Dictionary<string, object?>()));
    }

    // ScanAlertNotificationService (registered in Core) needs an ILocalNotifier; the head binds the real one.
    private sealed class FakeLocalNotifier : ILocalNotifier
    {
        public Task<bool> ShowAsync(string title, string body, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public void Every_registered_service_resolves()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gs_smoke_{Guid.NewGuid():N}.db");
        var services = CoreServices(dbPath);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        foreach (var descriptor in services)
            provider.GetRequiredService(descriptor.ServiceType);
    }

    // Both gates are load-bearing and both work ONLY as singletons: OcrGate (P0-3) is the one paid-OCR
    // call in flight app-wide — it can't live inside the Azure clients because the App head builds a new
    // client per call — and FlyerMutationGate (P1-4) is the single flyer-write lock shared by scheduler
    // resume, manual sync, and manual import. Each service takes its gate by constructor injection, so an
    // AddSingleton -> AddTransient slip would hand every consumer a private semaphore, serializing nothing,
    // with no other test failing. Resolving twice and comparing identity is what catches that.
    [Fact]
    public void Paid_ocr_and_flyer_write_gates_are_singletons_so_the_services_share_them()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gs_gates_{Guid.NewGuid():N}.db");
        var services = CoreServices(dbPath);

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Same(provider.GetRequiredService<OcrGate>(), provider.GetRequiredService<OcrGate>());
        Assert.Same(provider.GetRequiredService<FlyerMutationGate>(), provider.GetRequiredService<FlyerMutationGate>());
    }

    [Fact]
    public async Task SyncCompleted_wiring_refreshes_price_drop_alerts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gs_wire_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var services = CoreServices(Path.Combine(dir, "test.db"));
            using var provider = services.BuildServiceProvider(validateScopes: true);

            var factory = provider.GetRequiredService<SqliteConnectionFactory>();
            Database.Initialize(factory);
            using (var conn = factory.Open()) SeedStapleWithDrop(conn);

            var result = await provider.GetRequiredService<FlyerSyncScheduler>().RequestSyncAsync();

            Assert.True(result.Ran);
            Assert.Empty(result.Errors); // the wired handler must not have thrown
            var alert = Assert.Single(provider.GetRequiredService<PriceDropAlertService>().GetAlerts());
            Assert.Equal("below_usual", alert.AlertKind);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp */ } }
    }

    // Staple with a 30% drop: 4 receipts @ $10 (usual), one today @ $7.
    private static void SeedStapleWithDrop(SqliteConnection conn)
    {
        var store = StoresRepo.CreateStore(conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(conn, "Milk").Id;
        foreach (var d in new[] { 40, 30, 20, 10, 0 })
            PricesRepo.AddPricePoint(conn, item, store, d == 0 ? 7.0 : 10.0, "each",
                source: "receipt", date: DaysAgo(d), receiptId: AddReceipt(conn, store, DaysAgo(d)));
    }
}
