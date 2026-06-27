using System.Globalization;
using System.Text.RegularExpressions;

namespace GrocerySense.Core;

// Port of reference-python/.../services/multibuy_deal_service.py — "2/$5", "3 for 10", "2 @ 4.00",
// "BOGO" -> effective unit price. The only place deal strings are parsed. Pure (no DB).
public sealed class MultiBuyDealService
{
    private static readonly Regex ReSlash = new(@"\b(\d+)\s*/\s*\$?\s*(\d+(?:\.\d+)?)\b");   // 2/$5
    private static readonly Regex ReFor = new(@"\b(\d+)\s*for\s*\$?\s*(\d+(?:\.\d+)?)\b");   // 3 for 10
    private static readonly Regex ReAt = new(@"\b(\d+)\s*@\s*\$?\s*(\d+(?:\.\d+)?)\b");      // 2 @ 4.00
    private static readonly Regex ReBogo =
        new(@"\b(bogo|buy\s*1\s*get\s*1|buy\s*one\s*get\s*one)\b", RegexOptions.IgnoreCase);

    public DealAdjusted Adjust(string description, double? quantity, double? unitPrice, double? lineTotal,
        double? discount)
    {
        // A reported-but-unusable quantity (<=0) gets defaulted to 1.0 in the core, which can distort the
        // effective unit price — disclose that substitution rather than coercing silently.
        var qtyReportedButInvalid = quantity is not null && quantity <= 0;

        var da = AdjustCore(description, quantity, unitPrice, lineTotal, discount);
        if (qtyReportedButInvalid)
        {
            var note = string.IsNullOrEmpty(da.DealNote) ? "qty_defaulted" : $"{da.DealNote};qty_defaulted";
            da = da with { DealNote = note };
        }
        return da;
    }

    private DealAdjusted AdjustCore(string description, double? quantity, double? unitPrice, double? lineTotal,
        double? discount)
    {
        var desc = (description ?? "").Trim();
        var q = quantity ?? 1.0;
        if (q <= 0) q = 1.0;
        var up = unitPrice;
        var lt = lineTotal;
        var disc = discount ?? 0.0;

        // 1) BOGO-like: effective unit price from net total when possible.
        if (ReBogo.IsMatch(desc))
        {
            double? baseTotal = lt ?? (up is not null ? up * q : null);
            if (baseTotal is not null && q >= 2)
            {
                var netTotal = baseTotal.Value - disc;
                if (netTotal > 0)
                    return new DealAdjusted(q, netTotal / q, netTotal, "bogo_effective_price");
            }
            return new DealAdjusted(q, up, lt, "bogo_detected_no_adjust");
        }

        // 2) Bundle patterns: 2/$5, 3 for 10.
        if (ParseBundlePrice(desc) is { } bundle)
        {
            var (bundleQty, bundleTotal) = bundle;
            if (lt is not null)
            {
                if (q < bundleQty && Close(lt.Value, bundleTotal))
                {
                    var q2 = (double)bundleQty;
                    var netTotal = Math.Max(0.0, lt.Value - disc);
                    double? eff = q2 > 0 ? netTotal / q2 : null;
                    return new DealAdjusted(q2, eff, netTotal, $"bundle({bundleQty}/${F(bundleTotal)})_qty_fix");
                }

                var net = Math.Max(0.0, lt.Value - disc);
                if (q > 0)
                    return new DealAdjusted(q, net / q, net, $"bundle({bundleQty}/${F(bundleTotal)})_from_total");
            }

            // No line total: stated promo math.
            var effText = bundleTotal / bundleQty;
            var impliedTotal = effText * q - disc;
            var dealNote = $"bundle({bundleQty}/${F(bundleTotal)})_from_text";
            if (q < bundleQty) dealNote += ";qty_below_bundle_unverified";
            return new DealAdjusted(q, effText, impliedTotal, dealNote);
        }

        // 3) "2 @ 4.00" -> 2 units at $4 each.
        if (ParseAtPrice(desc) is { } at)
        {
            var (atQty, eachPrice) = at;
            double? up2 = (up is null || up <= 0) ? eachPrice : up;

            var q2 = q;
            if (q < atQty)
            {
                if (lt is null) q2 = atQty;
                else if (Close(lt.Value, atQty * eachPrice)) q2 = atQty;
            }

            var lt2 = lt;
            if (lt2 is null && up2 is not null) lt2 = up2 * q2 - disc;

            if (lt2 is not null && q2 > 0)
            {
                var netTotal = lt is not null ? Math.Max(0.0, lt.Value - disc) : lt2.Value;
                return new DealAdjusted(q2, netTotal / q2, netTotal, $"at({atQty}@{F(eachPrice)})");
            }
            return new DealAdjusted(q2, up2, lt2, $"at({atQty}@{F(eachPrice)})_no_total");
        }

        // 4) No deal: derive unit price from totals if missing.
        if ((up is null || up <= 0) && lt is not null && q > 0)
        {
            var net = lt.Value - disc;
            return new DealAdjusted(q, net / q, net, "unit_from_total");
        }

        return new DealAdjusted(q, up, lt, "no_deal");
    }

    // ---------------- parsers ----------------

    private static (int Qty, double Total)? ParseBundlePrice(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (ReSlash.Match(t) is { Success: true } ms && ValidateBundle(ms) is { } a) return a;
        if (ReFor.Match(t) is { Success: true } mf && ValidateBundle(mf) is { } b) return b;
        return null;
    }

    // Reject implausible "bundles" the greedy patterns catch — "1/2 cup" (qty 1), a date "12/2024"
    // (total 2024). A real multi-buy has qty in [2,24] and a grocery-scale total in (0,999].
    private static (int Qty, double Total)? ValidateBundle(Match m)
    {
        if (!int.TryParse(m.Groups[1].Value, out var qty)) return null;
        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            return null;
        if (qty < 2 || qty > 24) return null;
        if (total <= 0 || total > 999) return null;
        return (qty, total);
    }

    private static (int Qty, double Each)? ParseAtPrice(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        var m = ReAt.Match(t);
        if (!m.Success) return null;
        var qty = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var each = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return qty > 0 && each > 0 ? (qty, each) : null;
    }

    // Absolute floor + relative tolerance so the 2¢ window doesn't vanish on items priced in hundreds.
    private static bool Close(double a, double b, double tol = 0.02, double rel = 0.005) =>
        Math.Abs(a - b) <= Math.Max(tol, rel * Math.Max(Math.Abs(a), Math.Abs(b)));

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}
