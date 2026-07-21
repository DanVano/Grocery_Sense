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
// Throttled to at most twice a week (every 3.5 days); force=true bypasses it (the manual Sync button).
// The meta timestamp lives beside the DB in the writable app-data dir (NOT a source-relative path —
// mobile revokes those).
//
// Deals are enriched BEFORE insert (same prep FlyerIngestService.BuildDeal does: multi-buy adjust ->
// unit guess -> item mapping -> unit normalization) — PricesRepo.GetActiveFlyerPricesBatch reads
// flyer_deals directly, so a mapped+normalized deal reaches the optimizer/badges/alerts; an unmapped
// one only shows on the Deals page. Flyers never auto-create items: an unmapped title keeps item_id NULL.
public sealed class FlyerSyncService
{
    public const double SyncIntervalDays = 3.5;

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

    // True if no sync ran yet, the meta is unreadable, the clock skewed backwards, or the last sync is
    // older than the throttle interval.
    public bool NeedsSync()
    {
        var last = ReadLastSyncUtc();
        if (last is null) return true;
        var elapsed = DateTimeOffset.UtcNow - last.Value;
        if (elapsed < TimeSpan.Zero) return true; // clock skew: last sync in the future -> overdue
        return elapsed.TotalSeconds >= SyncIntervalDays * 86400;
    }

    public async Task<FlyerSyncResult> RunSyncAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !NeedsSync())
            return new FlyerSyncResult(0, 0, "too_soon", Array.Empty<string>());

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
            catch (Exception ex)
            {
                errors.Add($"{store.Name}: fetch failed — {ex.Message}");
                continue;
            }

            // Count the store as synced (attempted) even when the provider returns nothing.
            storesSynced++;
            if (rawDeals.Count == 0) continue;

            try
            {
                var validFrom = rawDeals.Select(d => IsoOr(defaultFrom, GetStr(d, "valid_from"))).Min() ?? defaultFrom;
                var validTo = rawDeals.Select(d => IsoOr(defaultTo, GetStr(d, "valid_to"))).Max() ?? defaultTo;

                using var conn = _factory.Open();
                using var tx = conn.BeginTransaction();
                var flyerId = _repo.CreateFlyerBatch(conn, store.Id, validFrom, validTo,
                    sourceType: "flipp_api", sourceRef: $"auto_sync_{defaultFrom}", note: $"Auto-sync {defaultFrom}", tx: tx);
                var rows = rawDeals.Select(d => EnrichDeal(conn, tx, MapDeal(flyerId, store.Id, d))).ToList();
                dealsInserted += _repo.AddDeals(conn, rows, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                errors.Add($"{store.Name}: DB insert failed — {ex.Message}");
            }
        }

        // The mapper buffers auto-learned aliases during EnrichDeal; flush them once per sync (own conn).
        _mapper.FlushLearnedAliases();

        WriteLastSyncUtc(DateTimeOffset.UtcNow);
        return new FlyerSyncResult(storesSynced, dealsInserted, null, errors);
    }

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

    // ---------------- meta (last-sync timestamp) ----------------

    // Meta is a single ISO-8601 timestamp line (not JSON) — trivially trim-safe for the AOT Android head and
    // all this file needs. Atomic temp->replace so a crash mid-write can't leave a truncated file.
    private DateTimeOffset? ReadLastSyncUtc()
    {
        if (!File.Exists(_metaPath)) return null;
        try
        {
            var ts = File.ReadAllText(_metaPath).Trim();
            if (ts.Length == 0) return null;
            return DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }
        catch
        {
            // Unreadable meta counts as "never synced" — NeedsSync() then returns true (fail toward syncing).
            return null;
        }
    }

    private void WriteLastSyncUtc(DateTimeOffset dt)
    {
        var tmp = _metaPath + ".tmp";
        File.WriteAllText(tmp, dt.ToString("o"));
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
