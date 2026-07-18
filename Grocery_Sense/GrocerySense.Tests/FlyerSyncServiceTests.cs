using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class FlyerSyncServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_sync_{Guid.NewGuid():N}");
    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigStore _config;
    private readonly string _metaPath;

    public FlyerSyncServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "test.db"));
        Database.Initialize(_factory);
        _config = new ConfigStore(_dir);
        _metaPath = Path.Combine(_dir, "flyer_sync_meta.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    // ---------------- provider fakes ----------------

    private sealed class StubProvider : IFlyerProvider
    {
        public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
            string storeName, string postalCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(Array.Empty<Dictionary<string, object?>>());
    }

    private sealed class FuncProvider(Func<string, IReadOnlyList<Dictionary<string, object?>>> fn) : IFlyerProvider
    {
        public Task<IReadOnlyList<Dictionary<string, object?>>> FetchFlyersForStoreAsync(
            string storeName, string postalCode, CancellationToken ct = default)
            => Task.FromResult(fn(storeName)); // fn may throw synchronously — the service catches it
    }

    private static Dictionary<string, object?> Deal(string title, double unitPrice) => new()
    {
        ["title"] = title, ["price_text"] = $"${unitPrice}", ["unit_price"] = unitPrice, ["unit"] = "each",
    };

    private FlyerSyncService Build(IFlyerProvider provider) => new(provider, _factory, _config,
        new IngredientMappingService(_factory), new UnitNormalizationService(), new MultiBuyDealService());

    private void WriteMeta(DateTimeOffset dt) => File.WriteAllText(_metaPath, dt.ToString("o"));

    // ---------------- NeedsSync throttle ----------------

    [Fact]
    public void NeedsSync_true_when_no_meta() => Assert.True(Build(new StubProvider()).NeedsSync());

    [Fact]
    public void NeedsSync_false_when_meta_recent()
    {
        WriteMeta(DateTimeOffset.UtcNow);
        Assert.False(Build(new StubProvider()).NeedsSync());
    }

    [Fact]
    public void NeedsSync_true_when_meta_older_than_interval()
    {
        WriteMeta(DateTimeOffset.UtcNow.AddDays(-5)); // interval is 3.5 days
        Assert.True(Build(new StubProvider()).NeedsSync());
    }

    [Fact]
    public void NeedsSync_true_when_meta_malformed()
    {
        File.WriteAllText(_metaPath, "{not json");
        Assert.True(Build(new StubProvider()).NeedsSync());
    }

    // ---------------- RunSync skip paths ----------------

    [Fact]
    public async Task RunSync_skips_too_soon_when_recently_synced()
    {
        WriteMeta(DateTimeOffset.UtcNow);
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var result = await Build(new StubProvider()).RunSyncAsync(force: false);
        Assert.Equal("too_soon", result.SkippedReason);
        Assert.False(result.Ran);
    }

    [Fact]
    public async Task RunSync_skips_no_stores()
    {
        var result = await Build(new StubProvider()).RunSyncAsync(force: true);
        Assert.Equal("no_stores", result.SkippedReason);
        Assert.False(result.Ran);
    }

    // ---------------- RunSync happy / error paths ----------------

    [Fact]
    public async Task RunSync_force_with_store_and_stub_provider_records_attempt()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Real Mart");
        var result = await Build(new StubProvider()).RunSyncAsync(force: true);

        Assert.True(result.Ran);
        Assert.Equal(1, result.StoresSynced);
        Assert.Equal(0, result.DealsInserted);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(_metaPath)); // meta written so the next launch throttles
    }

    [Fact]
    public async Task RunSync_inserts_deals_and_captures_per_store_errors()
    {
        using var conn = _factory.Open();
        StoresRepo.CreateStore(conn, "Store A");
        var storeB = StoresRepo.CreateStore(conn, "Store B").Id;

        var provider = new FuncProvider(name => name switch
        {
            "Store A" => throw new InvalidOperationException("network down"),
            "Store B" => new[] { Deal("Apples", 2.50), Deal("Milk 2L", 4.99) },
            _ => Array.Empty<Dictionary<string, object?>>(),
        });

        var result = await Build(provider).RunSyncAsync(force: true);

        Assert.True(result.Ran);
        Assert.Equal(1, result.StoresSynced);       // only B counted; A errored before the increment
        Assert.Equal(2, result.DealsInserted);
        Assert.Single(result.Errors);
        Assert.Contains("Store A", result.Errors[0]);

        var deals = new FlyersRepo().ListActiveDeals(conn, storeId: storeB);
        Assert.Equal(2, deals.Count);
        Assert.Contains(deals, d => d.Title == "Apples" && d.UnitPrice == 2.50m);
    }

    // Deals are enriched before insert: item mapping + multi-buy effective unit price + norm fields.
    // GetActiveFlyerPricesBatch reads flyer_deals, so a mapped deal reaches the optimizer/badges/alerts
    // (the split-brain regression test below proves it); an unmapped one (item_id NULL) never joins.
    [Fact]
    public async Task RunSync_enriches_deals_with_item_mapping_and_effective_unit_price()
    {
        using var conn = _factory.Open();
        var store = StoresRepo.CreateStore(conn, "Mart").Id;
        var item = ItemsRepo.CreateItem(conn, "Apples").Id;
        // Alias keyed by the mapper's own normalization of the deal text (title doubles as description).
        var normalized = new IngredientMappingService(_factory).MapToItem("Apples Apples").NormalizedInput;
        new ItemAliasesRepo().UpsertAlias(conn, normalized, item, 1.0);

        var provider = new FuncProvider(_ => new[]
        {
            new Dictionary<string, object?> { ["title"] = "Apples", ["price_text"] = "2/$5.00" },
        });

        var result = await Build(provider).RunSyncAsync(force: true);

        Assert.Equal(1, result.DealsInserted);
        var deal = Assert.Single(new FlyersRepo().ListActiveDeals(conn, storeId: store));
        Assert.Equal(item, deal.ItemId);
        Assert.Equal(2.50m, deal.UnitPrice); // "2/$5" -> $2.50 effective unit price
        Assert.NotNull(deal.NormUnitPrice);
        Assert.Contains("bundle", deal.NormNote);
    }

    // Split-brain regression: a synced, mapped deal MUST surface through the same query the
    // optimizer/watchlist/alerts/badges use. Guards against the flyer data landing in tables no
    // consumer reads (the original v2 bug: sync wrote flyer_deals while consumers read prices/flyer_sources).
    [Fact]
    public async Task RunSync_mapped_deal_reaches_GetActiveFlyerPricesBatch()
    {
        using var conn = _factory.Open();
        var store = StoresRepo.CreateStore(conn, "Mart").Id;
        var item = ItemsRepo.CreateItem(conn, "Apples").Id;
        var normalized = new IngredientMappingService(_factory).MapToItem("Apples Apples").NormalizedInput;
        new ItemAliasesRepo().UpsertAlias(conn, normalized, item, 1.0);

        var provider = new FuncProvider(_ => new[] { Deal("Apples", 2.50) });
        await Build(provider).RunSyncAsync(force: true);

        var quotes = PricesRepo.GetActiveFlyerPricesBatch(conn, new[] { item }, new[] { store });
        var quote = quotes[(item, store)];
        Assert.Equal("flyer", quote.Source);
        Assert.Equal(2.50, quote.UnitPrice);
    }

    [Fact]
    public async Task RunSync_skips_stores_not_marked_shop_here()
    {
        using var conn = _factory.Open();
        var shop = StoresRepo.CreateStore(conn, "My Mart").Id;
        var skip = StoresRepo.CreateStore(conn, "Far Mart").Id;
        StoresRepo.SetStoreShopHere(conn, skip, false);

        var provider = new FuncProvider(_ => new[] { Deal("Apples", 2.50) });
        var result = await Build(provider).RunSyncAsync(force: true);

        Assert.Equal(1, result.StoresSynced);
        Assert.Single(new FlyersRepo().ListActiveDeals(conn, storeId: shop));
        Assert.Empty(new FlyersRepo().ListActiveDeals(conn, storeId: skip));
    }

    [Fact]
    public async Task RunSync_keeps_item_id_null_for_unmapped_titles()
    {
        using var conn = _factory.Open();
        var store = StoresRepo.CreateStore(conn, "Mart").Id;

        var provider = new FuncProvider(_ => new[] { Deal("Zorbulon Crisps", 3.99) });
        await Build(provider).RunSyncAsync(force: true);

        var deal = Assert.Single(new FlyersRepo().ListActiveDeals(conn, storeId: store));
        Assert.Null(deal.ItemId); // flyers never auto-create items
        Assert.Equal(3.99m, deal.UnitPrice);
    }

    // ---------------- FlyerSyncScheduler (sync-on-resume) ----------------

    [Fact]
    public async Task Scheduler_RequestSync_fires_SyncCompleted_when_it_ran()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var scheduler = new FlyerSyncScheduler(Build(new StubProvider()));
        FlyerSyncResult? fired = null;
        scheduler.SyncCompleted += r => fired = r;

        var result = await scheduler.RequestSyncAsync();

        Assert.True(result.Ran);
        Assert.NotNull(fired);
    }

    [Fact]
    public async Task Scheduler_CheckOnResume_skips_and_stays_silent_when_too_soon()
    {
        WriteMeta(DateTimeOffset.UtcNow);
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var scheduler = new FlyerSyncScheduler(Build(new StubProvider()));
        var fired = false;
        scheduler.SyncCompleted += _ => fired = true;

        var result = await scheduler.CheckOnResumeAsync();

        Assert.Equal("too_soon", result.SkippedReason);
        Assert.False(fired);
    }

    [Fact]
    public async Task Scheduler_reports_post_sync_hook_failure_in_Errors_not_as_sync_failure()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var scheduler = new FlyerSyncScheduler(Build(new StubProvider()));
        scheduler.SyncCompleted += _ => throw new InvalidOperationException("boom");

        var result = await scheduler.RequestSyncAsync();

        Assert.True(result.Ran); // the sync itself succeeded
        Assert.Contains(result.Errors, e => e.Contains("boom"));
    }
}
