using System.Globalization;
using System.Text.RegularExpressions;
using static GrocerySense.Core.RawJson;

namespace GrocerySense.Core;

// Typed view over the loosely-typed Azure prebuilt-receipt JSON — the deep module the architecture
// review asked for. All field navigation (name aliases, valueCurrency shapes, currency-string
// scrubbing, confidence averaging, P0-3 field caps) lives behind two calls: Parse (header) and
// ParseLines (lines). Ingest consumes typed records; parsing is testable on raw dicts without OCR
// fakes. Two-phase on purpose: dedupe decisions read the cheap header BEFORE the line materialization
// (and its count guard) runs, so a duplicate still wins over a line-count reject.
internal sealed class ReceiptDocument
{
    // IsoDate is "" when OCR produced no yyyy-MM-dd date — callers decide their own fallback
    // (single-scan: file mtime; backfill: the user, never "today").
    public sealed record ReceiptHeader(
        string Merchant, string IsoDate, double? Subtotal, double? Tax, double? Total, double? OverallConfidence);

    // Quantity defaults to 1.0 when absent/invalid; QuantityReportedButInvalid says OCR CLAIMED a
    // quantity we refused, so ingest can disclose the default in the line note. UnitPrice/LineTotal
    // are post-derivation (negatives dropped, each derived from the other via quantity when missing).
    public sealed record ReceiptLine(
        int Index, string Description, double Quantity, bool QuantityReportedButInvalid,
        double? UnitPrice, double? LineTotal, double? Discount, double? Confidence);

    private readonly Dictionary<string, object?> _fields;
    public ReceiptHeader Header { get; }

    private ReceiptDocument(Dictionary<string, object?> fields, ReceiptHeader header)
    {
        _fields = fields;
        Header = header;
    }

    public static ReceiptDocument Parse(Dictionary<string, object?> rawJson, int maxMerchantChars)
    {
        var fields = TopFields(rawJson);

        var (merchantVal, merchantConf) = FieldValue(PickField(fields, "MerchantName", "Merchant"));
        // Field cap (P0-3) applied at the single extraction seam: the dedupe signature, the confirm
        // dialog and the store name all read this Merchant.
        var merchant = Truncate(Str(merchantVal).Trim(), maxMerchantChars);

        var (dateVal, dateConf) = FieldValue(PickField(fields, "TransactionDate", "Date"));
        var dateStr = Str(dateVal).Trim();
        var isoDate = IsIsoDate(dateStr) ? dateStr : "";

        var (subVal, subConf) = FieldValue(PickField(fields, "Subtotal"));
        var (taxVal, taxConf) = FieldValue(PickField(fields, "TotalTax", "Tax"));
        var (totalVal, totalConf) = FieldValue(PickField(fields, "Total"));

        var header = new ReceiptHeader(merchant, isoDate,
            CurrencyAmount(subVal), CurrencyAmount(taxVal), CurrencyAmount(totalVal),
            Average(merchantConf, dateConf, subConf, taxConf, totalConf));
        return new ReceiptDocument(fields, header);
    }

    // Deferred: rejects over-cap line counts (P0-3) before materializing anything, truncates
    // descriptions to the field cap, skips descriptionless entries.
    public IReadOnlyList<ReceiptLine> ParseLines(int maxLines, int maxFieldChars, CancellationToken ct = default)
    {
        var itemsField = PickField(_fields, "Items", "ItemList", "LineItems");
        var valueArray = AsList(GetProp(itemsField, "valueArray"));
        if (valueArray is not null && valueArray.Count > maxLines)
            throw new InvalidDataException(
                $"Receipt has {valueArray.Count} line items — over the {maxLines}-line guard; not imported.");

        var lines = new List<ReceiptLine>();
        if (valueArray is null) return lines;

        for (var idx = 0; idx < valueArray.Count; idx++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = AsDict(GetProp(valueArray[idx], "valueObject"));
            if (obj is null) continue;

            var (descVal, descConf) = FieldValue(PickField(obj, "Description", "Name", "Item"));
            var description = Truncate(Str(descVal).Trim(), maxFieldChars);
            if (description.Length == 0) continue;

            var (qtyVal, qtyConf) = FieldValue(PickField(obj, "Quantity", "Qty"));
            var (upVal, upConf) = FieldValue(PickField(obj, "UnitPrice", "Price"));
            var (ltVal, ltConf) = FieldValue(PickField(obj, "TotalPrice", "LineTotal", "Amount"));
            var (discVal, discConf) = FieldValue(PickField(obj, "Discount", "DiscountAmount"));

            var qParsed = SafeFloat(qtyVal);
            var qtyKnown = qParsed is > 0;
            var quantity = qtyKnown ? qParsed!.Value : 1.0;

            var unitPrice = CurrencyAmount(upVal);
            var lineTotal = CurrencyAmount(ltVal);
            if (lineTotal is < 0) lineTotal = null;
            if (unitPrice is < 0) unitPrice = null;
            if (unitPrice is null && lineTotal is not null && quantity != 0) unitPrice = lineTotal / quantity;
            if (lineTotal is null && unitPrice is not null && quantity != 0) lineTotal = unitPrice * quantity;

            lines.Add(new ReceiptLine(idx, description, quantity,
                QuantityReportedButInvalid: qtyVal is not null && !qtyKnown,
                unitPrice, lineTotal, CurrencyAmount(discVal),
                Average(descConf, qtyConf, upConf, ltConf, discConf)));
        }
        return lines;
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

    private static bool IsIsoDate(string s) => Regex.IsMatch(s.Trim(), @"^\d{4}-\d{2}-\d{2}$");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
