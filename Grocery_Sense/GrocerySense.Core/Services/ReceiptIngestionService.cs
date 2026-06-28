using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuzzySharp;
using FuzzySharp.SimilarityRatio;
using FuzzySharp.SimilarityRatio.Scorer.StrategySensitive;
using GrocerySense.Core.Abstractions;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Receipt ingest orchestrator. Receipt/raw/line/price writes are transactional; item/alias/unit prep is not.
// ponytail: uses the injected IngredientMappingService (accept 0.78); Python ingest used 0.75 — 3-point
// divergence, not worth a second mapper instance + DI change.
public sealed class ReceiptIngestionService
{
    private readonly IReceiptOcrClient _ocr;
    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;
    private readonly UnitNormalizationService _unitNorm;
    private readonly MultiBuyDealService _multibuy;
    private readonly ItemAliasesRepo _aliases = new();

    private const int StoreMatchThreshold = 85;

    public ReceiptIngestionService(IReceiptOcrClient ocr, SqliteConnectionFactory factory,
        IngredientMappingService mapper, UnitNormalizationService unitNorm, MultiBuyDealService multibuy)
    {
        _ocr = ocr;
        _factory = factory;
        _mapper = mapper;
        _unitNorm = unitNorm;
        _multibuy = multibuy;
    }

    public async Task<IngestOutcome> IngestReceiptFileAsync(string filePath, bool replaceExisting = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Receipt file not found", filePath);

        var fileHash = ComputeSha256(filePath);

        // 1) file-hash dedupe (BEFORE any OCR call).
        var replaced = false;
        using (var conn = _factory.Open())
        {
            var existing = ReceiptsRepo.FindReceiptIdByFileHash(conn, fileHash);
            if (existing is not null)
            {
                if (!replaceExisting)
                    return new IngestOutcome(existing, true, null, null, "file_hash");
                ReceiptsRepo.DeleteReceiptWithBackup(conn, existing.Value);
                replaced = true;
            }
        }

        // 2) OCR.
        var (operationId, rawJson) = await _ocr.AnalyzeReceiptFileAsync(filePath, ct: ct);

        // 3) signature dedupe (catches rescans of the same receipt).
        var (merchant, sigDate, total) = ExtractHeaderForSignature(rawJson);
        var signature = MakeSignature(merchant, sigDate, total);
        if (signature is not null)
        {
            using var conn = _factory.Open();
            var existingSig = ReceiptsRepo.FindReceiptIdBySignature(conn, signature);
            if (existingSig is not null)
            {
                if (!replaceExisting)
                    return new IngestOutcome(existingSig, true, operationId, null, "signature");
                ReceiptsRepo.DeleteReceiptWithBackup(conn, existingSig.Value);
                replaced = true;
            }
        }

        // 4) parse + resolve item ids/unit-norm/multibuy (pre-transaction; mapper writes alias/items here).
        var ingest = BuildIngest(rawJson, filePath, operationId, fileHash, signature, ct);
        _mapper.FlushLearnedAliases();

        // 5) atomic receipt/raw/line/price/dedupe write.
        using (var conn = _factory.Open())
        {
            using var tx = conn.BeginTransaction();
            try
            {
                var receiptId = ReceiptsRepo.IngestReceipt(conn, ingest, tx);
                tx.Commit();
                return new IngestOutcome(receiptId, false, operationId, null, null, replaced);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                tx.Rollback();
                if (ReceiptsRepo.FindReceiptIdByFileHash(conn, fileHash) is { } byHash)
                    return new IngestOutcome(byHash, true, operationId, null, "file_hash");
                if (signature is not null && ReceiptsRepo.FindReceiptIdBySignature(conn, signature) is { } bySig)
                    return new IngestOutcome(bySig, true, operationId, null, "signature");
                throw;
            }
        }
    }

    private ReceiptIngest BuildIngest(Dictionary<string, object?> rawJson, string filePath, string operationId,
        string fileHash, string? signature, CancellationToken ct)
    {
        var fields = TopFields(rawJson);

        var (merchantVal, merchantConf) = FieldValue(PickField(fields, "MerchantName", "Merchant"));
        var merchant = Str(merchantVal).Trim();

        var (dateVal, dateConf) = FieldValue(PickField(fields, "TransactionDate", "Date"));
        var purchaseDate = IsIsoDate(Str(dateVal)) ? Str(dateVal).Trim() : InferDate(filePath);

        var (subVal, subConf) = FieldValue(PickField(fields, "Subtotal"));
        var (taxVal, taxConf) = FieldValue(PickField(fields, "TotalTax", "Tax"));
        var (totalVal, totalConf) = FieldValue(PickField(fields, "Total"));
        var subtotal = CurrencyAmount(subVal);
        var tax = CurrencyAmount(taxVal);
        var total = CurrencyAmount(totalVal);

        var overallConf = Average(merchantConf, dateConf, subConf, taxConf, totalConf);

        using var conn = _factory.Open();
        var storeId = GetOrCreateStoreId(conn, merchant);

        var lines = new List<ReceiptIngestLine>();
        var itemsField = PickField(fields, "Items", "ItemList", "LineItems");
        var valueArray = AsList(GetProp(itemsField, "valueArray"));
        if (valueArray is not null)
        {
            for (var idx = 0; idx < valueArray.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                var obj = AsDict(GetProp(valueArray[idx], "valueObject"));
                if (obj is null) continue;

                var (descVal, descConf) = FieldValue(PickField(obj, "Description", "Name", "Item"));
                var description = Str(descVal).Trim();
                if (description.Length == 0) continue;

                var (qtyVal, qtyConf) = FieldValue(PickField(obj, "Quantity", "Qty"));
                var (upVal, upConf) = FieldValue(PickField(obj, "UnitPrice", "Price"));
                var (ltVal, ltConf) = FieldValue(PickField(obj, "TotalPrice", "LineTotal", "Amount"));
                var (discVal, discConf) = FieldValue(PickField(obj, "Discount", "DiscountAmount"));

                var qParsed = SafeFloat(qtyVal);
                var qtyKnown = qParsed is > 0;
                var quantity = qtyKnown ? qParsed!.Value : 1.0;
                var qtyReportedButInvalid = qtyVal is not null && !qtyKnown;

                var unitPrice = CurrencyAmount(upVal);
                var lineTotal = CurrencyAmount(ltVal);
                var discount = CurrencyAmount(discVal);
                if (lineTotal is < 0) lineTotal = null;
                if (unitPrice is < 0) unitPrice = null;
                if (unitPrice is null && lineTotal is not null && quantity != 0) unitPrice = lineTotal / quantity;
                if (lineTotal is null && unitPrice is not null && quantity != 0) lineTotal = unitPrice * quantity;

                var adj = _multibuy.Adjust(description, quantity, unitPrice, lineTotal, discount);
                quantity = adj.Quantity;
                unitPrice = adj.UnitPrice;
                lineTotal = adj.LineTotal;
                var dealNote = adj.DealNote;
                if (qtyReportedButInvalid)
                    dealNote = string.IsNullOrEmpty(dealNote) ? "qty_invalid_defaulted" : $"{dealNote};qty_invalid_defaulted";

                var lineConf15 = ConfidenceTo15(Average(descConf, qtyConf, upConf, ltConf, discConf));

                var mapping = _mapper.MapToItem(description);
                var (itemId, mapConf15) = UpsertItemFromMapping(conn, description, mapping);
                if (mapping.ItemId is null) _mapper.InvalidateChoices(); // a new item exists; later lines can match it

                var observedUnit = _unitNorm.GuessUnitFromText(description);
                if (observedUnit == "unknown") observedUnit = "each";

                NormalizedPrice? norm = unitPrice is not null
                    ? _unitNorm.Normalize(conn, itemId, unitPrice.Value, observedUnit, description)
                    : null;

                var combinedNote = norm is not null
                    ? (string.IsNullOrEmpty(dealNote) ? norm.Note : $"{norm.Note};{dealNote}")
                    : dealNote;

                lines.Add(new ReceiptIngestLine(idx, itemId, description, quantity, unitPrice, lineTotal, discount,
                    lineConf15 ?? mapConf15, observedUnit, norm?.NormUnitPrice, norm?.NormUnit, combinedNote));
            }
        }

        var rawJsonStr = JsonSerializer.Serialize(rawJson);
        return new ReceiptIngest(storeId, purchaseDate, subtotal, tax, total, filePath, ConfidenceTo15(overallConf),
            operationId, null, rawJsonStr, fileHash, signature, lines);
    }

    private (int ItemId, int? Confidence15) UpsertItemFromMapping(SqliteConnection conn, string desc, MappingResult mapping)
    {
        if (mapping.ItemId is not null)
            return (mapping.ItemId.Value, ConfidenceTo15(mapping.Confidence));

        var cleaned = desc.Trim();
        if (cleaned.Length == 0) cleaned = "Unknown Item";
        var item = ItemsRepo.CreateItem(conn, cleaned);
        try { _aliases.UpsertAlias(conn, desc, item.Id, 0.60, "receipt_auto"); } catch { /* best-effort */ }
        return (item.Id, 2);
    }

    // Fuzzy-match the merchant to an existing store (token-set >= 85) else create one.
    private static int GetOrCreateStoreId(SqliteConnection conn, string merchant)
    {
        merchant = string.IsNullOrWhiteSpace(merchant) ? "Unknown Store" : merchant.Trim();
        var stores = StoresRepo.ListStores(conn);
        if (stores.Count == 0) return StoresRepo.CreateStore(conn, merchant).Id;

        var best = Process.ExtractOne(merchant, stores.Select(s => s.Name).ToList(), s => s,
            ScorerCache.Get<TokenSetScorer>());
        if (best is not null && best.Score >= StoreMatchThreshold) return stores[best.Index].Id;
        return StoresRepo.CreateStore(conn, merchant).Id;
    }

    // ---------- header / signature ----------

    private static (string Merchant, string Date, double? Total) ExtractHeaderForSignature(Dictionary<string, object?> rawJson)
    {
        var fields = TopFields(rawJson);
        var merchant = Str(FieldValue(PickField(fields, "MerchantName", "Merchant")).Value).Trim();
        var dateStr = Str(FieldValue(PickField(fields, "TransactionDate", "Date")).Value).Trim();
        var date = IsIsoDate(dateStr) ? dateStr : "";
        var total = CurrencyAmount(FieldValue(PickField(fields, "Total")).Value);
        return (merchant, date, total);
    }

    private static string? MakeSignature(string merchant, string date, double? total)
    {
        if (string.IsNullOrEmpty(merchant) || string.IsNullOrEmpty(date) || total is null) return null;
        return $"{NormalizeMerchant(merchant)}|{date}|{total.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeMerchant(string s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return Regex.Replace(s, @"[^a-z0-9 \-]", "");
    }

    private static string InferDate(string filePath)
    {
        try { return File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd"); } // LOCAL: receipt date is a calendar day
        catch { return DateTime.Now.ToString("yyyy-MM-dd"); }
    }

    // ---------- raw-JSON navigation (top-level Dictionary with Azure JsonElement values) ----------

    private static Dictionary<string, object?> TopFields(Dictionary<string, object?> rawJson)
    {
        var docs = AsList(rawJson.GetValueOrDefault("documents"));
        if (docs is null || docs.Count == 0) return new();
        return AsDict(GetProp(docs[0], "fields")) is { } f ? new Dictionary<string, object?>(f) : new();
    }

    private static object? PickField(IReadOnlyDictionary<string, object?>? fields, params string[] names)
    {
        if (fields is null) return null;
        var lower = fields.Keys.ToDictionary(k => k.ToLowerInvariant(), k => k);
        foreach (var n in names)
            if (lower.TryGetValue(n.ToLowerInvariant(), out var key) && AsDict(fields[key]) is not null)
                return fields[key];
        return null;
    }

    private static (object? Value, double? Conf) FieldValue(object? field)
    {
        var d = AsDict(field);
        if (d is null) return (null, null);
        var conf = ToDouble(d.GetValueOrDefault("confidence"));
        foreach (var k in new[] { "valueString", "valueNumber", "valueDate", "valueTime", "valuePhoneNumber",
                     "valueCurrency", "valueInteger", "valueBoolean" })
            if (d.ContainsKey(k)) return (d[k], conf);
        return d.ContainsKey("content") ? (d["content"], conf) : (null, conf);
    }

    private static double? CurrencyAmount(object? v)
    {
        if (v is null) return null;
        if (AsDict(v) is { } d && d.ContainsKey("amount")) return SafeFloat(d["amount"]);
        return SafeFloat(v);
    }

    private static IReadOnlyDictionary<string, object?>? AsDict(object? o)
    {
        switch (o)
        {
            case IReadOnlyDictionary<string, object?> d: return d;
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                var m = new Dictionary<string, object?>();
                foreach (var p in je.EnumerateObject()) m[p.Name] = p.Value;
                return m;
            default: return null;
        }
    }

    private static IReadOnlyList<object?>? AsList(object? o)
    {
        switch (o)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                return je.EnumerateArray().Select(x => (object?)x).ToList();
            default: return null;
        }
    }

    private static object? GetProp(object? o, string key) => AsDict(o)?.GetValueOrDefault(key);

    private static string Str(object? o) => o switch
    {
        null => "",
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString(),
        _ => o.ToString() ?? "",
    };

    private static double? ToDouble(object? o) => o switch
    {
        null => null,
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
        JsonElement je when je.ValueKind == JsonValueKind.String =>
            double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
        _ => double.TryParse(o.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
    };

    private static double? SafeFloat(object? o)
    {
        if (o is null) return null;
        if (o is not string && ToDouble(o) is { } d) return d;
        var s = Str(o).Trim();
        if (s.Length == 0) return null;
        s = Regex.Replace(s.Replace(",", ""), @"[^\d.\-]", ""); // strip currency symbols/thousands separators
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? Average(params double?[] vals)
    {
        var present = vals.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }

    private static int? ConfidenceTo15(double? conf) => conf switch
    {
        null => null,
        >= 0.90 => 5,
        >= 0.75 => 4,
        >= 0.60 => 3,
        >= 0.40 => 2,
        _ => 1,
    };

    private static bool IsIsoDate(string s) => Regex.IsMatch(s.Trim(), @"^\d{4}-\d{2}-\d{2}$");

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
