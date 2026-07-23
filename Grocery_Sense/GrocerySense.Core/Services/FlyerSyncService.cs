using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly UnitNormalizationService _unitNorm;
    private readonly MultiBuyDealService _multibuy;
    private readonly FlyersRepo _repo = new();
    private readonly string _metaPath;

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$");

    public FlyerSyncService(IFlyerProvider provider, SqliteConnectionFactory factory, ConfigStore config,
        IngredientMappingService mapper, UnitNormalizationService unitNorm, MultiBuyDealService multibuy)
    {
        _provider = provider;
        _factory = factory;
        _config = config;
        _mapper = mapper;
        _unitNorm = unitNorm;
        _multibuy = multibuy;
        var dir = Path.GetDirectoryName(factory.DbPath) is { Length: > 0 } d ? d : ".";
        _metaPath = Path.Combine(dir, "flyer_sync_meta.json");
    }

    // True if no COMMITTED sync happened yet, the meta is unreadable, the clock skewed backwards, or the
    // last success is older than the throttle interval. Attempts never satisfy this — an all-fail sync
    // leaves NeedsSync() true.
    public bool NeedsSync()
    {
        var meta = ReadMeta();
        if (meta.Success is null) return true;
        var elapsed = DateTimeOffset.UtcNow - meta.Success.Value;
        if (elapsed < TimeSpan.Zero) return true; // clock skew: RunSync discloses it instead of syncing
        return elapsed.TotalSeconds >= SyncIntervalDays * 86400;
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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var defaultFrom = today.ToString("yyyy-MM-dd");
        var defaultTo = today.AddDays(7).ToString("yyyy-MM-dd");

        foreach (var store in stores)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<Dictionary<string, object?>> rawDeals;
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
                _repo.DeleteBatchesForStore(conn, store.Id, AutoSourceType, tx);

                if (rawDeals.Count > 0)
                {
                    var validFrom = rawDeals.Select(d => IsoOr(defaultFrom, GetStr(d, "valid_from"))).Min() ?? defaultFrom;
                    var validTo = rawDeals.Select(d => IsoOr(defaultTo, GetStr(d, "valid_to"))).Max() ?? defaultTo;
                    var flyerId = _repo.CreateFlyerBatch(conn, store.Id, validFrom, validTo,
                        sourceType: AutoSourceType, sourceRef: $"auto_sync_{defaultFrom}", note: $"Auto-sync {defaultFrom}", tx: tx);
                    var rows = rawDeals.Select(d => EnrichDeal(conn, tx, MapDeal(flyerId, store.Id, d))).ToList();
                    dealsInserted += _repo.AddDeals(conn, rows, tx);
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

    // Same per-deal prep as FlyerIngestService.BuildDeal, applied to a provider row: promo phrase -> effective
    // unit price, observed-unit guess, item mapping (no auto-create), unit normalization inside the caller's tx.
    private FlyerDeal EnrichDeal(SqliteConnection conn, SqliteTransaction tx, FlyerDeal d)
    {
        var title = d.Title ?? "";
        var description = string.IsNullOrEmpty(d.Description) ? title : d.Description!;
        var combined = $"{title} {description}".Trim();
        if (combined.Length == 0) return d;

        var adj = _multibuy.Adjust($"{title} {d.PriceText ?? ""}".Trim(), quantity: 1.0,
            unitPrice: ToDouble(d.UnitPrice), lineTotal: ToDouble(d.DealTotal), discount: null);

        var observedUnit = _unitNorm.GuessUnitFromText(combined);
        if (observedUnit == "unknown") observedUnit = "each";

        int? itemId = null;
        double? mapConf = null;
        var normUnitPrice = adj.UnitPrice;
        var normUnit = observedUnit;
        var normNote = $"flyer;{adj.DealNote}";

        var m = _mapper.MapToItem(conn, combined, tx);
        if (m.ItemId is not null)
        {
            itemId = m.ItemId;
            mapConf = m.Confidence;
            if (adj.UnitPrice is not null)
            {
                var norm = _unitNorm.Normalize(conn, m.ItemId.Value, adj.UnitPrice.Value, observedUnit, combined, tx);
                normUnitPrice = norm.NormUnitPrice;
                normUnit = norm.NormUnit;
                normNote = $"{norm.Note};{adj.DealNote};flyer";
            }
        }

        return d with
        {
            DealQty = adj.Quantity, DealTotal = Dec(adj.LineTotal), UnitPrice = Dec(adj.UnitPrice),
            Unit = observedUnit, NormUnitPrice = Dec(normUnitPrice), NormUnit = normUnit, NormNote = normNote,
            ItemId = itemId, MappingConfidence = mapConf,
        };
    }

    // Maps a provider deal dict into a flyer_deals row. No item-mapping/unit-norm here (mirrors Python
    // insert_deals — sync stores raw provider data; the ingest pipeline is where mapping happens).
    private static FlyerDeal MapDeal(int flyerId, int storeId, Dictionary<string, object?> d) => new(
        Id: 0, FlyerId: flyerId, AssetId: null, StoreId: storeId, PageIndex: ToInt(GetVal(d, "page_index")),
        Title: GetStr(d, "title"), Description: GetStr(d, "description"), PriceText: GetStr(d, "price_text"),
        DealQty: null, DealTotal: Dec(ToDouble(GetVal(d, "deal_total") ?? GetVal(d, "price"))),
        UnitPrice: Dec(ToDouble(GetVal(d, "unit_price"))), Unit: GetStr(d, "unit"),
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

    private static string IsoOr(string fallback, string? v) => v is not null && IsoDate.IsMatch(v.Trim()) ? v.Trim() : fallback;

    private static decimal? Dec(double? v) => v is { } x ? (decimal)x : null;

    private static object? GetVal(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key);

    private static string? GetStr(Dictionary<string, object?> d, string key) => GetVal(d, key) switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        var o => o.ToString(),
    };

    private static int? ToInt(object? o) => ToDouble(o) is { } v ? (int)v : null;

    private static double? ToDouble(object? o) => o switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        _ => double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
    };
}
