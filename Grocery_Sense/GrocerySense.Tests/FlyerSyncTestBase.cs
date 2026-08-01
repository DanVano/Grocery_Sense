using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;

namespace GrocerySense.Tests;

// Shared fixture for FlyerSyncServiceTests + FlyerSyncHardeningTests: per-test temp dir holding the
// DB, config JSON and sync-meta file, plus the provider fake and service builder both files use.
// Field names keep the leading underscore so the inheriting tests read unchanged.
public abstract class FlyerSyncTestBase : IDisposable
{
    protected readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_sync_{Guid.NewGuid():N}");
    protected readonly SqliteConnectionFactory _factory;
    protected readonly ConfigStore _config;
    protected readonly string _metaPath;

    protected FlyerSyncTestBase()
    {
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "test.db"));
        Database.Initialize(_factory);
        _config = new ConfigStore(_dir);
        _metaPath = Path.Combine(_dir, "flyer_sync_meta.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    protected sealed class FuncProvider(Func<string, IReadOnlyList<Dictionary<string, object?>>> fn) : IFlyerProvider
    {
        public int Calls;
        public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
            string storeName, string postalCode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(fn(storeName)); // fn may throw synchronously — the service catches it
        }
    }

    protected static Dictionary<string, object?> Deal(string title, double unitPrice) => new()
    {
        ["title"] = title, ["price_text"] = $"${unitPrice}", ["unit_price"] = unitPrice, ["unit"] = "each",
    };

    protected FlyerSyncService Build(IFlyerProvider provider) => new(provider, _factory, _config,
        new IngredientMappingService(_factory), new UnitNormalizationService(), new MultiBuyDealService());
}
