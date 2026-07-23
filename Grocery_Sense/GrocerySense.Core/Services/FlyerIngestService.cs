using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;
using static GrocerySense.Core.RawJson; // AsDict/AsList/GetProp/Str/ToDouble — the shared Azure-JSON navigation

namespace GrocerySense.Core;

// Port of reference-python/.../services/flyer_ingest_service.py — manual flyer asset ingest.
// Mirrors ReceiptIngestionService: Azure layout + extraction + item-mapping/unit-norm prep happen
// pre-transaction (the mapper/unit-norm open their own connections), then batch/asset/raw-json/deal
// rows are written in ONE transaction with rollback. Raw JSON is persisted only to SQLite
// (flyer_raw_json) — no plaintext copy is written to disk (data minimization; nothing reprocessed it).
//
// ponytail: reuses the injected IngredientMappingService (accept 0.78); Python flyer ingest used 0.75 —
// a 3-point divergence, same trade-off ReceiptIngestionService documents. Flyer deals keep item_id NULL
// when the mapper finds no match (unlike receipts, flyers do NOT auto-create items).
//
// Scope: only ingest_assets is ported (the v1 manual flyer-photo path). Python's ingest_dealrecords_json
// (pre-extracted JSON) has no v1 route and was never in the scaffold surface — skip until one needs it.
public sealed class FlyerIngestService
{
    // P0-3 service-boundary bounds (the authoritative check — UI-level caps are unprovable). Acknowledged
    // ceiling: with Pages="1-10" in the layout client, one flyer import can bill up to 10 × 10 pages.
    public const int MaxFilesPerImport = 10;
    public const long MaxAggregateBytes = 100L * 1024 * 1024;
    public const int MaxRawJsonChars = 16 * 1024 * 1024; // parse/persistence guard, post-SDK buffering
    public const int MaxDealsPerAsset = 500;             // a denser "flyer" is hostile input, not a flyer

    private readonly IFlyerLayoutClient _layout;
    private readonly OcrGate _gate;
    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;
    private readonly UnitNormalizationService _unitNorm;
    private readonly MultiBuyDealService _multibuy;
    private readonly FlyersRepo _repo = new();

    public FlyerIngestService(IFlyerLayoutClient layout, OcrGate gate, SqliteConnectionFactory factory,
        IngredientMappingService mapper, UnitNormalizationService unitNorm, MultiBuyDealService multibuy)
    {
        _layout = layout;
        _gate = gate;
        _factory = factory;
        _mapper = mapper;
        _unitNorm = unitNorm;
        _multibuy = multibuy;
    }

    public async Task<FlyerIngestResult> IngestAssetsAsync(int? storeId, string? validFrom, string? validTo,
        IReadOnlyList<string> filePaths, string sourceType = "manual_upload",
        string? sourceRef = null, string? note = null, bool tryItemMapping = true, CancellationToken ct = default)
    {
        if (storeId is null) throw new ArgumentException("storeId is required for flyer ingest.", nameof(storeId));

        // P0-3 caps before ANY paid client call: over-limit = disclosed reject, zero Azure requests.
        if (filePaths.Count > MaxFilesPerImport)
            throw new InvalidOperationException(
                $"Flyer imports are capped at {MaxFilesPerImport} files (got {filePaths.Count}) — import in batches.");
        long aggregateBytes = 0;
        foreach (var p in filePaths)
            if (new FileInfo(p) is { Exists: true } fi)
                aggregateBytes += fi.Length;
        if (aggregateBytes > MaxAggregateBytes)
            throw new InvalidOperationException(
                $"Flyer import totals {aggregateBytes / (1024 * 1024)} MiB — over the " +
                $"{MaxAggregateBytes / (1024 * 1024)} MiB cap; import fewer or smaller pages.");

        // --- Phase A (pre-transaction): Azure layout + extraction + mapping/unit-norm prep. ---
        var staged = new List<StagedAsset>();
        using (var conn = _factory.Open()) // used only for unit-norm default_unit backfill (auto-commit), like receipt ingest
        {
            foreach (var fp in filePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(fp)) continue;

                var bytes = await File.ReadAllBytesAsync(fp, ct);
                var assetType = GuessAssetType(fp);
                var sha = Sha256(bytes);

                // The one paid call per asset, serialized + deadlined by the injected singleton gate.
                var (_, analyze) = await _gate.RunAsync(tok => _layout.AnalyzeLayoutFileAsync(fp, tok), ct);

                // Raw JSON is persisted only to SQLite (flyer_raw_json) in Phase B — no plaintext disk copy.
                var rawJsonStr = RawJson.ToJsonString(analyze);
                if (rawJsonStr.Length > MaxRawJsonChars)
                    throw new InvalidDataException(
                        $"Flyer OCR response is {rawJsonStr.Length / (1024 * 1024)} MiB of JSON — over the " +
                        "16 MiB guard; not parsed or persisted.");
                var rawSha = Sha256(Encoding.UTF8.GetBytes(rawJsonStr));

                var deals = new List<FlyerDeal>();
                foreach (var d in ExtractDealsFromLayout(analyze))
                    deals.Add(BuildDeal(conn, storeId.Value, d, tryItemMapping));

                staged.Add(new StagedAsset(assetType, fp, sha, rawJsonStr, rawSha, deals));
            }
        }

        if (tryItemMapping) _mapper.FlushLearnedAliases();

        // --- Phase B (one transaction): batch + assets + raw-json + deals. ---
        using (var conn = _factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            var flyerId = _repo.CreateFlyerBatch(conn, storeId.Value, validFrom, validTo, sourceType, sourceRef, note, tx: tx);

            var assetsCount = 0;
            var rawCount = 0;
            var dealRows = new List<FlyerDeal>();
            foreach (var a in staged)
            {
                var assetId = _repo.AddAsset(conn, flyerId, a.AssetType, a.Path, a.Sha, tx);
                assetsCount++;
                _repo.AddRawJson(conn, flyerId, a.RawJson, a.RawSha, tx);
                rawCount++;
                foreach (var d in a.Deals)
                    dealRows.Add(d with { FlyerId = flyerId, AssetId = assetId });
            }

            var dealsCount = _repo.AddDeals(conn, dealRows, tx);
            tx.Commit();
            return new FlyerIngestResult(flyerId, assetsCount, dealsCount, rawCount);
        }
    }

    private sealed record StagedAsset(
        string AssetType, string Path, string Sha, string RawJson, string RawSha, List<FlyerDeal> Deals);

    // Builds a deal row (FlyerId/AssetId stamped later inside the tx). Mirrors the Python per-deal block:
    // multibuy-adjust the price text, guess the observed unit, then optionally map to an item and normalize.
    private FlyerDeal BuildDeal(SqliteConnection conn, int storeId, ExtractedDeal d, bool tryItemMapping)
    {
        var title = d.Title ?? "";
        var description = string.IsNullOrEmpty(d.Description) ? title : d.Description;
        var combined = $"{title} {description}".Trim();

        var adj = _multibuy.Adjust($"{title} {d.PriceText ?? ""}".Trim(), quantity: 1.0,
            unitPrice: null, lineTotal: null, discount: null);

        var observedUnit = _unitNorm.GuessUnitFromText(combined);
        if (observedUnit == "unknown") observedUnit = "each";

        int? itemId = null;
        double? mapConf = null;
        var normUnitPrice = adj.UnitPrice;
        var normUnit = observedUnit;
        var normNote = $"flyer;{adj.DealNote}";

        if (tryItemMapping)
        {
            var m = _mapper.MapToItem(conn, combined);
            if (m.ItemId is not null)
            {
                itemId = m.ItemId;
                mapConf = m.Confidence;
                if (adj.UnitPrice is not null)
                {
                    var norm = _unitNorm.Normalize(conn, m.ItemId.Value, adj.UnitPrice.Value, observedUnit, combined);
                    normUnitPrice = norm.NormUnitPrice;
                    normUnit = norm.NormUnit;
                    normNote = $"{norm.Note};{adj.DealNote};flyer";
                }
            }
        }

        return new FlyerDeal(
            Id: 0, FlyerId: 0, AssetId: null, StoreId: storeId, PageIndex: d.PageIndex,
            Title: title, Description: description, PriceText: d.PriceText,
            DealQty: adj.Quantity, DealTotal: Dec(adj.LineTotal), UnitPrice: Dec(adj.UnitPrice), Unit: observedUnit,
            NormUnitPrice: Dec(normUnitPrice), NormUnit: normUnit, NormNote: normNote,
            ItemId: itemId, MappingConfidence: mapConf, Confidence: d.Confidence, CreatedAt: null);
    }

    // ---------------- extractor v1 (heuristic, ported from _extract_deals_from_layout) ----------------

    internal sealed record ExtractedDeal(int? PageIndex, string Title, string Description, string PriceText, double? Confidence);

    // Walks each page's lines; a line with a price-like token is a deal anchor, the 1-2 prior lines its title/desc.
    internal IReadOnlyList<ExtractedDeal> ExtractDealsFromLayout(Dictionary<string, object?> analyzeResult)
    {
        var outv = new List<ExtractedDeal>();
        var pages = AsList(analyzeResult.GetValueOrDefault("pages"));
        if (pages is null) return outv;

        foreach (var (page, pi) in pages.Select((p, i) => (p, i)))
        {
            var lines = AsList(GetProp(page, "lines"));
            if (lines is null) continue;

            var texts = new List<string>();
            var confs = new List<double?>();
            foreach (var ln in lines)
            {
                var content = Str(GetProp(ln, "content")).Trim();
                if (content.Length == 0) continue;
                texts.Add(content);
                confs.Add(ToDouble(GetProp(ln, "confidence")));
            }

            for (var i = 0; i < texts.Count; i++)
            {
                var priceText = ExtractPriceText(texts[i]);
                if (priceText is null) continue;

                // P0-3: a page set producing this many deal anchors is hostile input — reject before the
                // mapper/normalizer (and the DB) ever see it.
                if (outv.Count >= MaxDealsPerAsset)
                    throw new InvalidDataException(
                        $"Flyer produced over {MaxDealsPerAsset} deals from one file — rejected as malformed input.");

                var prev1 = i - 1 >= 0 ? texts[i - 1].Trim() : "";
                var prev2 = i - 2 >= 0 ? texts[i - 2].Trim() : "";

                var title = prev1.Length > 0 ? prev1 : texts[i];
                var description = string.Join(" ", new[] { prev2, prev1 }.Where(x => x.Length > 0)).Trim();
                if (description.Length == 0) description = title;

                double? c = null;
                foreach (var j in new[] { i, i - 1, i - 2 })
                    if (j >= 0 && j < confs.Count && confs[j] is { } cv)
                        c = Math.Max(c ?? 0.0, cv);

                outv.Add(new ExtractedDeal(pi, Trunc(title, 180), Trunc(description, 400), Trunc(priceText, 50), c));
            }
        }

        return outv;
    }

    // Detects flyer price forms: 2/$5, 3 for 10, 2 @ 4.00, $2.99, then a plain .dd fallback.
    internal static string? ExtractPriceText(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;

        foreach (var rx in PriceAnchors)
            if (rx.Match(t) is { Success: true } m)
                return m.Value;
        return null;
    }

    private static readonly Regex[] PriceAnchors =
    [
        new(@"\b\d+\s*/\s*\$?\s*\d+(?:\.\d+)?\b"),                          // 2/$5
        new(@"\b\d+\s*for\s*\$?\s*\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase), // 3 for 10
        new(@"\b\d+\s*@\s*\$?\s*\d+(?:\.\d+)?\b"),                          // 2 @ 4.00
        new(@"\$\s*\d+(?:\.\d{2})"),                                        // $2.99
        new(@"\b\d+\.\d{2}\b"),                                             // 3.99 (no $)
    ];

    // Strict money: a $-prefixed amount, or a bare decimal (optional trailing /unit). Rejects OCR letter-O
    // prefixes ("O.99"), EU decimals ("1,25"), and multi-amount strings ("Was $4.99 Now $2.99"). Ported from
    // _safe_float_money — the "never fabricate a price" guard (CLAUDE: fail loud, never fake).
    internal static double? SafeFloatMoney(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = StrictMoney.Match(s.Trim());
        if (!m.Success) return null;
        var raw = m.Groups["dollar"].Success ? m.Groups["dollar"].Value : m.Groups["bare"].Value;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static readonly Regex StrictMoney = new(
        @"^\s*(?:\$\s*(?<dollar>\d+(?:\.\d{1,2})?)|(?<bare>\d+(?:\.\d{1,2})?)(?:\s*/.*)?)\s*$");

    private static string GuessAssetType(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant() == "pdf" ? "pdf" : "image";

    // ---------------- helpers (sha, money cast) — JSON navigation is shared via RawJson ----------------

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static decimal? Dec(double? v) => v is { } x ? (decimal)x : null;

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
