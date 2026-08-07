using System.Globalization;
using System.Text.Json;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/flyer_sync_service.py — pulls flyer deals for the user's stores
// via the provider and persists them into flyer_batches + flyer_deals.
//
// P1-4 semantics: the meta file records attempt / success / retry_not_before / failure separately, so the
// 3.5-day freshness throttle keys off COMMITTED success, never a failed attempt (an all-fail sync must not
// buy a 3.5-day silent blackout). A 10-minute attempt cooldown and any server retry_not_before apply to
// manual AND automatic sync; force bypasses ONLY the freshness check. A store counts as synced only after
// its transaction commits, and that same transaction retires the store's previous flipp_api auto batches
// (a valid EMPTY provider result also removes them — stale deals must not outlive the sync that found
// nothing; manual batches are untouched). The meta lives beside the DB in the writable app-data dir.
//
// Deals are enriched BEFORE insert (same prep FlyerIngestService.BuildDeal does: multi-buy adjust ->
// unit guess -> item mapping -> unit normalization) — PricesRepo.GetActiveFlyerPricesBatch reads
// flyer_deals directly, so a mapped+normalized deal reaches the optimizer/badges/alerts; an unmapped
// one only shows on the Deals page. Flyers never auto-create items: an unmapped title keeps item_id NULL.
// The sync meta ledger. Success is the last COMMITTED sync (drives the freshness throttle); Attempt is the
// last outbound try (drives the cooldown); Failure is the last redacted failure (store + reason class),
// shown on the Deals page so a failed background sync stays visible after the snackbar is gone.
public sealed record FlyerSyncMeta(
    DateTimeOffset? Attempt, DateTimeOffset? Success, DateTimeOffset? RetryNotBefore, string? Failure);

public sealed class FlyerSyncService
{
    public const double SyncIntervalDays = 3.5;
    public const string AutoSourceType = "flipp_api";
    public static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);
    // Retry-After handling for the unofficial endpoint: a missing header still backs off, a hostile
    // header can't lock the app out for more than a day.
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    private readonly IFlyerProvider _provider;
    private readonly SqliteConnectionFactory _factory;
    private readonly ConfigStore _config;
    private readonly IngredientMappingService _mapper;
    private readonly DealEnricher _enricher;

    private readonly string _metaPath;

    public FlyerSyncService(IFlyerProvider provider, SqliteConnectionFactory factory, ConfigStore config,
        IngredientMappingService mapper, DealEnricher enricher)
    {
        _provider = provider;
        _factory = factory;
        _config = config;
        _mapper = mapper;
        _enricher = enricher;
        var dir = Path.GetDirectoryName(factory.DbPath) is { Length: > 0 } d ? d : ".";
        _metaPath = Path.Combine(dir, "flyer_sync_meta.json");
    }

    public async Task<FlyerSyncResult> RunSyncAsync(bool force = false, CancellationToken ct = default)
    {
        var meta = ReadMeta();
        var now = DateTimeOffset.UtcNow;

        // Server throttle and the minimum-attempt cooldown bind manual AND automatic sync; force bypasses
        // only the freshness check below.
        if (meta.RetryNotBefore is { } rnb && rnb > now)
            return new FlyerSyncResult(0, 0, "throttled", Array.Empty<string>());
        // Attempt within [now - cooldown, now + tolerance] → too_soon. A far-future attempt is clock skew,
        // handled below for auto; force proceeds and heals the meta with a fresh attempt.
        if (meta.Attempt is { } att && now - att < AttemptCooldown && att - now <= ClockSkewTolerance)
            return new FlyerSyncResult(0, 0, "too_soon", Array.Empty<string>());
        if (!force)
        {
            // Future timestamps = clock skew: a visible result, never a resume-driven sync storm. A manual
            // force heals the meta by writing a fresh attempt below.
            if (IsFuture(meta.Attempt, now) || IsFuture(meta.Success, now))
                return new FlyerSyncResult(0, 0, "clock_skew", Array.Empty<string>());
            if (meta.Success is { } succ && (now - succ).TotalSeconds < SyncIntervalDays * 86400)
                return new FlyerSyncResult(0, 0, "too_soon", Array.Empty<string>());
        }

        List<Store> stores;
        using (var conn = _factory.Open())
            // Only stores the user actually shops at — matches every downstream flyer-price consumer
            // (optimizer/insights/watchlist) and avoids needless calls to the unofficial Flipp endpoint.
            stores = StoresRepo.ListStores(conn).Where(s => s.ShopHere && s.IsActive).ToList();
        if (stores.Count == 0)
            return new FlyerSyncResult(0, 0, "no_stores", Array.Empty<string>());

        var postal = _config.Load().PostalCode;
        var errors = new List<string>();
        var storesSynced = 0;
        var dealsInserted = 0;
        string? lastFailure = null;
        TimeSpan? retryAfter = null;

        // attempt= is stamped immediately before the first outbound request. Cancellation after this point
        // preserves it (the cooldown still applies) and never writes success.
        WriteMeta(meta with { Attempt = now });

        var today = DateOnly.FromDateTime(DateTime.Now); // local calendar date (V3 local-date convention)
        var defaultFrom = today.ToString("yyyy-MM-dd");
        var defaultTo = today.AddDays(7).ToString("yyyy-MM-dd");

        foreach (var store in stores)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<ProviderDeal> rawDeals;
            try
            {
                rawDeals = await _provider.FetchFlyersForStoreAsync(store.Name, postal, ct);
            }
            catch (FlyerProviderThrottledException ex)
            {
                // The provider said stop — abort the remaining stores and persist the back-off.
                retryAfter = Clamp(ex.RetryAfter ?? DefaultRetryAfter, MaxRetryAfter);
                errors.Add($"{store.Name}: provider throttled — remaining stores skipped; " +
                           $"retrying no sooner than {retryAfter.Value.TotalMinutes:0} min from now.");
                lastFailure = $"{store.Name}: throttled";
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{store.Name}: fetch failed — {ex.Message}");
                lastFailure = $"{store.Name}: fetch_failed";
                continue;
            }

            try
            {
                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();

                // Retention, inside the SAME transaction as the insert: this store's previous flipp_api
                // auto batches die with their deals/assets/raw_json (ON DELETE CASCADE; foreign_keys=ON).
                // A valid EMPTY result also lands here — stale deals must not outlive the sync that found
                // nothing. Manual batches (other source_type) are untouched, and prices.flyer_source_id
                // points at flyer_sources, a different table — price history cannot be affected.
                FlyersRepo.DeleteBatchesForStore(conn, store.Id, AutoSourceType, tx);

                if (rawDeals.Count > 0)
                {
                    var validFrom = rawDeals.Select(d => IsoOr(defaultFrom, d.ValidFrom)).Min() ?? defaultFrom;
                    var validTo = rawDeals.Select(d => IsoOr(defaultTo, d.ValidTo)).Max() ?? defaultTo;
                    var flyerId = FlyersRepo.CreateFlyerBatch(conn, store.Id, validFrom, validTo,
                        sourceType: AutoSourceType, sourceRef: $"auto_sync_{defaultFrom}", note: $"Auto-sync {defaultFrom}", tx: tx);
                    var rows = rawDeals.Select(d => EnrichDeal(conn, tx, MapDeal(flyerId, store.Id, d))).ToList();
                    dealsInserted += FlyersRepo.AddDeals(conn, rows, tx);
                }

                tx.Commit();
                storesSynced++; // a store counts as synced only after its transaction commits
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{store.Name}: DB insert failed — {ex.Message}");
                lastFailure = $"{store.Name}: db_failed";
            }
        }

        // The mapper buffers auto-learned aliases during EnrichDeal; flush them once per sync (own conn).
        _mapper.FlushLearnedAliases();

        // success= only when at least one store COMMITTED; an all-fail run keeps the previous success so
        // NeedsSync() stays true. failure= holds the last redacted failure (store + reason class — no
        // URLs, no postal code); a clean run clears it.
        WriteMeta(new FlyerSyncMeta(
            Attempt: now,
            Success: storesSynced > 0 ? DateTimeOffset.UtcNow : meta.Success,
            RetryNotBefore: retryAfter is { } ra ? DateTimeOffset.UtcNow + ra : null,
            Failure: lastFailure));
        return new FlyerSyncResult(storesSynced, dealsInserted, null, errors);
    }

    private static bool IsFuture(DateTimeOffset? ts, DateTimeOffset now) =>
        ts is { } t && t > now + ClockSkewTolerance;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) => value > max ? max : value;

    // Per-deal prep via the shared DealEnricher (the same pipeline manual flyer ingest uses), applied to a
    // provider row inside the caller's tx. A row with no text at all stays untouched.
    private FlyerDeal EnrichDeal(SqliteConnection conn, SqliteTransaction tx, FlyerDeal d)
    {
        var e = _enricher.Enrich(conn, d.Title, d.Description, d.PriceText,
            (double?)d.UnitPrice, (double?)d.DealTotal, tx);
        if (e is null) return d;
        return d with
        {
            DealQty = e.DealQty, DealTotal = e.DealTotal, UnitPrice = e.UnitPrice, Unit = e.Unit,
            NormUnitPrice = e.NormUnitPrice, NormUnit = e.NormUnit, NormNote = e.NormNote,
            ItemId = e.ItemId, MappingConfidence = e.MappingConfidence,
        };
    }

    // Maps a provider deal into a flyer_deals row. No item-mapping/unit-norm here (mirrors Python
    // insert_deals — sync stores raw provider data; the ingest pipeline is where mapping happens).
    private static FlyerDeal MapDeal(int flyerId, int storeId, ProviderDeal d) => new(
        Id: 0, FlyerId: flyerId, AssetId: null, StoreId: storeId, PageIndex: d.PageIndex,
        Title: d.Title, Description: d.Description, PriceText: d.PriceText,
        DealQty: null, DealTotal: Dec(d.Price), UnitPrice: Dec(d.UnitPrice), Unit: d.Unit,
        NormUnitPrice: null, NormUnit: null, NormNote: null,
        ItemId: null, MappingConfidence: null, Confidence: null, CreatedAt: null);

    // ---------------- meta (attempt / success / retry_not_before / failure) ----------------

    // Keyed plain-text lines ("attempt=…"), NOT JSON — trivially trim-safe for the AOT Android head, no
    // serializer context needed (landmine §4.7 discipline). Backward compatible: a legacy file holding a
    // single bare ISO timestamp reads as that moment's success AND attempt. Unreadable meta counts as
    // "never synced" (fail toward syncing). Atomic temp->replace so a crash can't truncate it.
    public FlyerSyncMeta ReadMeta()
    {
        if (!File.Exists(_metaPath)) return new(null, null, null, null);
        try
        {
            var lines = File.ReadAllLines(_metaPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0) return new(null, null, null, null);

            if (lines.Count == 1 && !lines[0].Contains('='))
            {
                var legacy = ParseTs(lines[0]);
                return legacy is null ? new(null, null, null, null) : new FlyerSyncMeta(legacy, legacy, null, null);
            }

            DateTimeOffset? attempt = null, success = null, retryNotBefore = null;
            string? failure = null;
            foreach (var line in lines)
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                var value = line[(i + 1)..];
                switch (line[..i])
                {
                    case "attempt": attempt = ParseTs(value); break;
                    case "success": success = ParseTs(value); break;
                    case "retry_not_before": retryNotBefore = ParseTs(value); break;
                    case "failure": failure = value; break;
                }
            }
            return new FlyerSyncMeta(attempt, success, retryNotBefore, failure);
        }
        catch
        {
            return new(null, null, null, null);
        }
    }

    private static DateTimeOffset? ParseTs(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)
            ? ts : null;

    private void WriteMeta(FlyerSyncMeta meta)
    {
        var lines = new List<string>();
        if (meta.Attempt is { } a) lines.Add($"attempt={a:o}");
        if (meta.Success is { } s) lines.Add($"success={s:o}");
        if (meta.RetryNotBefore is { } r) lines.Add($"retry_not_before={r:o}");
        if (meta.Failure is { } f) lines.Add($"failure={f.ReplaceLineEndings(" ")}");

        var tmp = _metaPath + ".tmp";
        File.WriteAllText(tmp, string.Join(Environment.NewLine, lines));
        if (File.Exists(_metaPath)) File.Replace(tmp, _metaPath, null);
        else File.Move(tmp, _metaPath);
    }

    // ---------------- dict/value helpers (plain CLR or JsonElement) ----------------

    private static string IsoOr(string fallback, string? v) =>
        v is not null && DateOnly.TryParseExact(v.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) ? v.Trim() : fallback;

    private static decimal? Dec(double? v) => v is { } x ? (decimal)x : null;
}
