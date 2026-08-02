using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

// P1-4: the sync throttle reflects COMMITTED success, cancellation is honest, the unofficial endpoint
// gets cooldowns + Retry-After, batch growth is bounded by per-store retention, and the last failure
// stays visible via the persisted meta.
public sealed class FlyerSyncHardeningTests : FlyerSyncTestBase
{
    private void WriteKeyedMeta(params string[] lines) => File.WriteAllLines(_metaPath, lines);

    private long Count(string sql)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    // ---------------- success = committed, never attempted ----------------

    [Fact]
    public async Task All_fail_sync_records_no_success_and_keeps_the_failure()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var svc = Build(new FuncProvider(_ => throw new InvalidOperationException("network down")));

        var result = await svc.RunSyncAsync(force: true);

        Assert.Equal(0, result.StoresSynced);
        Assert.Single(result.Errors);
        var meta = svc.ReadMeta();
        Assert.NotNull(meta.Attempt);
        Assert.Null(meta.Success); // no success = no 3.5-day silent blackout bought by an all-fail run
        Assert.Equal("Mart: fetch_failed", meta.Failure); // redacted: store + reason class only
    }

    [Fact]
    public async Task Cancellation_propagates_and_preserves_the_attempt()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var svc = Build(new FuncProvider(_ => throw new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunSyncAsync(force: true));

        var meta = svc.ReadMeta();
        Assert.NotNull(meta.Attempt); // stamped before the first outbound request
        Assert.Null(meta.Success);    // cancellation never writes success
    }

    [Fact]
    public async Task Db_failure_store_is_not_counted_and_success_is_not_written()
    {
        using (var conn = _factory.Open())
        {
            StoresRepo.CreateStore(conn, "Mart");
            using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE flyer_deals"; // force the per-store transaction to fail
            drop.ExecuteNonQuery();
        }
        var svc = Build(new FuncProvider(_ => new[] { Deal("Apples", 2.50) }));

        var result = await svc.RunSyncAsync(force: true);

        Assert.Equal(0, result.StoresSynced); // fetch succeeded, commit did not — not counted
        Assert.Single(result.Errors);
        Assert.Null(svc.ReadMeta().Success);
    }

    // ---------------- cooldown / clock skew / server throttle ----------------

    [Fact]
    public async Task Force_inside_the_attempt_cooldown_is_too_soon()
    {
        WriteKeyedMeta($"attempt={DateTimeOffset.UtcNow.AddMinutes(-2):o}");
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var provider = new FuncProvider(_ => Array.Empty<ProviderDeal>());

        var result = await Build(provider).RunSyncAsync(force: true);

        Assert.Equal("too_soon", result.SkippedReason);
        Assert.Equal(0, provider.Calls); // no outbound request burned
    }

    [Fact]
    public async Task Future_success_yields_a_visible_clock_skew_result_not_a_sync_storm()
    {
        WriteKeyedMeta($"success={DateTimeOffset.UtcNow.AddHours(6):o}");
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var provider = new FuncProvider(_ => Array.Empty<ProviderDeal>());

        var result = await Build(provider).RunSyncAsync(force: false);

        Assert.Equal("clock_skew", result.SkippedReason);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Throttled_provider_aborts_remaining_stores_and_the_persisted_backoff_binds_manual_sync()
    {
        using (var conn = _factory.Open())
        {
            StoresRepo.CreateStore(conn, "Mart A");
            StoresRepo.CreateStore(conn, "Mart B");
        }
        var provider = new FuncProvider(_ =>
            throw new FlyerProviderThrottledException("429", TimeSpan.FromMinutes(30)));
        var svc = Build(provider);

        var result = await svc.RunSyncAsync(force: true);

        Assert.Equal(1, provider.Calls); // remaining store never fetched
        Assert.Contains(result.Errors, e => e.Contains("throttled"));
        var meta = svc.ReadMeta();
        Assert.NotNull(meta.RetryNotBefore);
        Assert.InRange(meta.RetryNotBefore!.Value, DateTimeOffset.UtcNow.AddMinutes(25), DateTimeOffset.UtcNow.AddMinutes(35));
        Assert.Equal("Mart A: throttled", meta.Failure);

        // retry_not_before binds manual sync too — force bypasses only the freshness check.
        var second = await svc.RunSyncAsync(force: true);
        Assert.Equal("throttled", second.SkippedReason);
        Assert.Equal(1, provider.Calls);
    }

    // ---------------- retention ----------------

    [Fact]
    public async Task Sync_replaces_prior_auto_batches_in_the_same_tx_and_leaves_manual_batches_alone()
    {
        int store;
        using (var conn = _factory.Open())
        {
            store = StoresRepo.CreateStore(conn, "Mart").Id;
            var oldAuto = FlyersRepo.CreateFlyerBatch(conn, store, "2026-07-01", "2026-07-08", sourceType: "flipp_api");
            FlyersRepo.AddDeals(conn, new[] { NewDealRow(oldAuto, store, "Stale Apples") });
            var manual = FlyersRepo.CreateFlyerBatch(conn, store, "2026-07-01", "2026-07-08", sourceType: "manual_upload");
            FlyersRepo.AddDeals(conn, new[] { NewDealRow(manual, store, "Manual Bread") });
        }

        await Build(new FuncProvider(_ => new[] { Deal("Fresh Apples", 2.50) })).RunSyncAsync(force: true);

        Assert.Equal(1L, Count("SELECT COUNT(*) FROM flyer_batches WHERE source_type = 'flipp_api'"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM flyer_batches WHERE source_type = 'manual_upload'"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM flyer_deals WHERE title = 'Stale Apples'"));
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM flyer_deals WHERE title = 'Manual Bread'"));
        // FK-cascade sanity: retention leaves zero orphaned deals behind.
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM flyer_deals WHERE flyer_id NOT IN (SELECT id FROM flyer_batches)"));
    }

    [Fact]
    public async Task Valid_empty_result_removes_the_prior_auto_batch()
    {
        int store;
        using (var conn = _factory.Open())
        {
            store = StoresRepo.CreateStore(conn, "Mart").Id;
            var oldAuto = FlyersRepo.CreateFlyerBatch(conn, store, "2026-07-01", "2026-07-08", sourceType: "flipp_api");
            FlyersRepo.AddDeals(conn, new[] { NewDealRow(oldAuto, store, "Stale Apples") });
        }

        var result = await Build(new FuncProvider(_ => Array.Empty<ProviderDeal>()))
            .RunSyncAsync(force: true);

        Assert.Equal(1, result.StoresSynced); // an empty result is still a committed sync
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM flyer_batches WHERE source_type = 'flipp_api'"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM flyer_deals")); // stale deals did not outlive the sync
    }

    private static GrocerySense.Domain.FlyerDeal NewDealRow(int flyerId, int storeId, string title) => new(
        Id: 0, FlyerId: flyerId, AssetId: null, StoreId: storeId, PageIndex: null,
        Title: title, Description: null, PriceText: "$1.00", DealQty: null, DealTotal: null,
        UnitPrice: 1.00m, Unit: "each", NormUnitPrice: null, NormUnit: null, NormNote: null,
        ItemId: null, MappingConfidence: null, Confidence: null, CreatedAt: null);

    // ---------------- meta migration + shared gate ----------------

    [Fact]
    public async Task Legacy_single_timestamp_meta_migrates_to_the_keyed_format()
    {
        var legacy = DateTimeOffset.UtcNow.AddDays(-10);
        File.WriteAllText(_metaPath, legacy.ToString("o"));
        var svc = Build(new FuncProvider(_ => Array.Empty<ProviderDeal>()));

        var meta = svc.ReadMeta();
        Assert.Equal(legacy, meta.Success!.Value, TimeSpan.FromSeconds(1)); // legacy reads as success

        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        await svc.RunSyncAsync(force: true);

        Assert.Contains("success=", File.ReadAllText(_metaPath)); // rewritten keyed
        // A just-completed sync throttles the un-forced path (attempt cooldown, then the success interval).
        Assert.Equal("too_soon", (await svc.RunSyncAsync(force: false)).SkippedReason);
    }

    [Fact]
    public async Task Sync_and_manual_import_share_one_gate_so_the_loser_reports_busy()
    {
        using (var conn = _factory.Open()) StoresRepo.CreateStore(conn, "Mart");
        var gate = new FlyerMutationGate();
        var scheduler = new FlyerSyncScheduler(Build(new FuncProvider(_ => Array.Empty<ProviderDeal>())), gate);
        var mapper = new IngredientMappingService(_factory);
        var ingest = new FlyerIngestService(new NullLayout(), new OcrGate(), gate, _factory, mapper,
            new DealEnricher(mapper, new UnitNormalizationService(), new MultiBuyDealService()));

        Assert.True(gate.TryEnter()); // something else holds the flyer-write gate
        try
        {
            var syncResult = await scheduler.RequestSyncAsync();
            Assert.Equal("busy", syncResult.SkippedReason);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ingest.IngestAssetsAsync(1, "2026-07-01", "2026-07-08", new[] { "unused.jpg" }));
            Assert.Contains("already running", ex.Message);
        }
        finally
        {
            gate.Exit();
        }

        // Once released, the same gate lets a sync through.
        var after = await scheduler.RequestSyncAsync();
        Assert.Null(after.SkippedReason);
    }

    private sealed class NullLayout : IFlyerLayoutClient
    {
        public Task<(string OperationId, Dictionary<string, object?> RawJson)> AnalyzeLayoutFileAsync(
            string filePath, CancellationToken ct = default) =>
            Task.FromResult(("op", new Dictionary<string, object?>()));
    }
}
