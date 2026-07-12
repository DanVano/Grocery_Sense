using System.Globalization;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Core;

// Port of reference-python/.../services/price_drop_alert_service.py — the alert engine:
//  - "usual price" = receipt median (with fallback); 6-month low; staples from receipt frequency.
//  - Two alert kinds: A) dropped >=15% below usual; B) stock-up near a 6-month low past a 30-day cooldown.
//  - Persists open alerts to price_drop_alerts (migration ledger) for display + dismissal.
// Dict returns are replaced with the typed PriceDropAlert record. No _ensure_tables — the table is in the
// migration ledger. Opens connections via the factory; persistence runs in a transaction.
public sealed class PriceDropAlertService
{
    // Tunables (per PORTING: keep the 15%/5%/staple defaults). The public ones are the single source for
    // every surface that classifies a price (ShoppingInsightsService badges reuse them — no duplicated numbers).
    public const double DropBelowUsualThresholdPct = 15.0;
    public const double NearSixMonthLowThresholdPct = 5.0;
    public const int UsualLookbackDays = 180;
    public const int LowLookbackDays = 183;
    public const int MinReceiptSamplesForUsual = 4;
    public const double MinLowPriceFloor = 0.05;
    private const int AlertSuppressionDays = 30;
    private const int StockUpCooldownDays = 30;
    private const int StapleLookbackDays = 90;
    // Stock-up horizon: suggest ~4 weeks' worth (locked decision 2026-07-09; was 42), still capped at 3x typical.
    private const int StockUpHorizonDays = 28;
    private const int MaxStockupMultiple = 3;

    private readonly SqliteConnectionFactory _factory;

    public PriceDropAlertService(SqliteConnectionFactory factory) => _factory = factory;

    private readonly record struct AlertKey(int ItemId, int StoreId, string AlertKind);

    // ----------------------- public API -----------------------

    public int RefreshEngineAlerts(bool staplesOnly = true) => PersistEngineAlerts(ComputeEngineAlerts(staplesOnly));

    public IReadOnlyList<PriceDropAlert> ComputeEngineAlerts(bool staplesOnly = true)
    {
        using var conn = _factory.Open();
        return ComputeEngineAlerts(conn, staplesOnly);
    }

    public IReadOnlyList<PriceDropAlert> GetAlerts(int limit = 250)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, item_id, store_id, store_name, item_name, current_price, usual_price, pct_below_usual, " +
            "six_month_low, pct_above_low, alert_kind, is_staple, receipt_samples, basis, source, " +
            "last_seen_at_or_below, notes, created_at, status, suggested_qty, suggested_qty_note " +
            "FROM price_drop_alerts WHERE status = 'open' ORDER BY created_at DESC";
        if (limit > 0)
        {
            cmd.CommandText += " LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
        }
        using var r = cmd.ExecuteReader();
        var alerts = new List<PriceDropAlert>();
        while (r.Read()) alerts.Add(MapAlertRow(r));
        return alerts;
    }

    public IReadOnlyList<PriceDropAlert> GetOpenAlerts() => GetAlerts(0);

    public void DismissAlert(int alertId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE price_drop_alerts SET status = 'dismissed', dismissed_at = datetime('now') WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", alertId);
        cmd.ExecuteNonQuery();
    }

    // ----------------------- engine -----------------------

    private IReadOnlyList<PriceDropAlert> ComputeEngineAlerts(SqliteConnection conn, bool staplesOnly)
    {
        var stores = StoresRepo.ListStores(conn);
        if (stores.Count == 0) return Array.Empty<PriceDropAlert>();

        var stapleItemIds = PricesRepo.ListStapleItemIds(conn, StapleLookbackDays, 3, 4)
            .ToDictionary(s => s.ItemId, s => (s.LineCount, s.DistinctReceipts));
        var itemIds = staplesOnly ? stapleItemIds.Keys.ToList() : ItemsRepo.ListItems(conn).Select(i => i.Id).ToList();
        if (itemIds.Count == 0) return Array.Empty<PriceDropAlert>();
        var storeIds = stores.Select(s => s.Id).ToList();
        var storeNameMap = stores.ToDictionary(s => s.Id, s => s.Name);

        // Batch-load all price signals upfront (no per-item queries).
        var itemsMap = ItemsRepo.GetItemsByIds(conn, itemIds);
        var flyerQuotes = PricesRepo.GetActiveFlyerPricesBatch(conn, itemIds, storeIds);
        var storeQuotes = PricesRepo.GetMostRecentPricesByStoreBatch(conn, itemIds, storeIds);
        var globalQuotes = PricesRepo.GetMostRecentPricesGlobalBatch(conn, itemIds);
        var usualMap = PricesRepo.GetUsualUnitPriceBatch(conn, itemIds, receiptOnly: true,
            minSamples: MinReceiptSamplesForUsual, sinceDays: UsualLookbackDays);
        var sixLowMap = PricesRepo.GetSixMonthLowBatch(conn, itemIds, LowLookbackDays);

        // Pass 1: best current price per item + near-low ceilings.
        var bestPrices = new Dictionary<int, (double Unit, int StoreId, string StoreName, string Source)>();
        var nearLowCeilings = new Dictionary<int, double>();

        foreach (var itemId in itemIds)
        {
            double? bestUnit = null;
            var bestStoreId = 0;
            var bestStoreName = "";
            var bestSource = "unknown";

            foreach (var s in stores)
            {
                double unit;
                string source;
                if (flyerQuotes.TryGetValue((itemId, s.Id), out var fq))
                {
                    unit = fq.UnitPrice;
                    source = string.IsNullOrEmpty(fq.Source) ? "flyer" : fq.Source;
                }
                else if (storeQuotes.TryGetValue((itemId, s.Id), out var pr) && pr.UnitPrice > 0)
                {
                    unit = pr.UnitPrice;
                    source = string.IsNullOrEmpty(pr.Source) ? "latest" : pr.Source;
                }
                else continue;

                if (unit <= 0) continue;
                if (bestUnit is null || unit < bestUnit)
                {
                    bestUnit = unit;
                    bestStoreId = s.Id;
                    bestStoreName = s.Name;
                    bestSource = source;
                }
            }

            if (bestUnit is null && globalQuotes.TryGetValue(itemId, out var gl) && gl.UnitPrice > 0)
            {
                bestUnit = gl.UnitPrice;
                bestStoreId = gl.StoreId;
                bestStoreName = storeNameMap.GetValueOrDefault(gl.StoreId, "Unknown");
                bestSource = string.IsNullOrEmpty(gl.Source) ? "global_latest" : gl.Source;
            }

            if (bestUnit is null || bestUnit <= 0) continue;
            bestPrices[itemId] = (bestUnit.Value, bestStoreId, bestStoreName, bestSource);

            var (sixLow, _) = sixLowMap.GetValueOrDefault(itemId, (null, null));
            if (sixLow is > 0)
            {
                var nearThreshold = sixLow.Value * (1.0 + NearSixMonthLowThresholdPct / 100.0);
                if (bestUnit.Value <= nearThreshold) nearLowCeilings[itemId] = bestUnit.Value;
            }
        }

        var lastSeenMap = PricesRepo.GetLastSeenAtOrBelowBatch(conn, nearLowCeilings, LowLookbackDays);
        var cadenceMap = nearLowCeilings.Count > 0
            ? PricesRepo.GetPurchaseCadenceBatch(conn, nearLowCeilings.Keys.ToList(), UsualLookbackDays)
            : new Dictionary<int, (double?, double?)>();

        // Pass 2: build alerts.
        var outAlerts = new List<PriceDropAlert>();
        foreach (var itemId in itemIds)
        {
            if (!bestPrices.TryGetValue(itemId, out var bp)) continue;
            if (!itemsMap.TryGetValue(itemId, out var item)) continue;

            var (bestUnit, bestStoreId, bestStoreName, bestSource) = bp;
            var (usualPrice, usualSamples, basis) = usualMap.GetValueOrDefault(itemId, (null, 0, "unknown"));
            var (sixLow, sixLowWhen) = sixLowMap.GetValueOrDefault(itemId, (null, null));

            double? pctBelowUsual = usualPrice is > 0 ? (usualPrice.Value - bestUnit) / usualPrice.Value * 100.0 : null;
            double? pctAboveLow = sixLow >= MinLowPriceFloor ? (bestUnit - sixLow!.Value) / sixLow.Value * 100.0 : null;

            var lastSeen = lastSeenMap.GetValueOrDefault(itemId);
            var stockUpOk = nearLowCeilings.ContainsKey(itemId) && PassesStockupCooldown(lastSeen);
            var droppedOk = pctBelowUsual >= DropBelowUsualThresholdPct;
            if (!droppedOk && !stockUpOk) continue;

            var alertKind = droppedOk && stockUpOk ? "both" : droppedOk ? "below_usual" : "stock_up";
            var (lineCount, receiptCount) = stapleItemIds.GetValueOrDefault(itemId, (0, 0));
            var isStaple = receiptCount >= 3 || lineCount >= 4;

            double? suggestedQty = null;
            string? suggestedQtyNote = null;
            if (alertKind is "stock_up" or "both" && SuggestStockUpQty(cadenceMap, itemId) is { } sq)
            {
                suggestedQty = sq.Qty;
                suggestedQtyNote = sq.Note;
            }

            var notes = BuildNotes(bestStoreName, bestUnit, usualPrice, pctBelowUsual, sixLow, pctAboveLow,
                alertKind, basis, usualSamples, lastSeen, sixLowWhen);

            outAlerts.Add(new PriceDropAlert(itemId, item.CanonicalName, bestStoreId, bestStoreName, bestUnit,
                usualPrice, pctBelowUsual, sixLow, pctAboveLow, alertKind, isStaple, usualSamples, basis,
                bestSource, lastSeen, notes, suggestedQty, suggestedQtyNote));
        }

        // Strongest savings first (below-usual), then nearest 6-mo low.
        outAlerts.Sort((a, b) =>
        {
            var c = (-(a.PctBelowUsual ?? -1.0)).CompareTo(-(b.PctBelowUsual ?? -1.0));
            return c != 0 ? c : (a.PctAboveLow ?? 9999.0).CompareTo(b.PctAboveLow ?? 9999.0);
        });
        return outAlerts;
    }

    // Optional helper: scan recent receipt lines and open below-usual alerts for prices far under usual.
    public int ScanRecentReceipts(int days = 21)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days)).ToString("yyyy-MM-dd");
        using var conn = _factory.Open();

        var rows = new List<(int ItemId, int StoreId, double Paid)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT item_id, store_id, CAST(unit_price AS REAL) AS paid, COALESCE(date, created_at) AS when_iso " +
                "FROM prices WHERE (source = 'receipt' OR receipt_id IS NOT NULL) AND item_id IS NOT NULL " +
                "AND unit_price IS NOT NULL AND date(COALESCE(date, created_at)) >= date($since) " +
                "ORDER BY when_iso DESC LIMIT 50000";
            cmd.Parameters.AddWithValue("$since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.GetDouble(2)));
        }
        return OpenBelowUsualAlertsFromRows(conn, rows);
    }

    // Receipt-SCOPED scan (A7): open below-usual alerts from the lines of ONE receipt only. The single-scan
    // notification path uses this — a global date-window scan (ScanRecentReceipts) would credit a freshly
    // scanned receipt with alerts from OTHER recent receipts (e.g. backfilled recent-dated ones), the
    // misattribution landmine (V2_FOLLOWUPS §4). Returns the number of new alerts opened.
    public int ScanReceipt(long receiptId)
    {
        using var conn = _factory.Open();
        var rows = new List<(int ItemId, int StoreId, double Paid)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT item_id, store_id, CAST(unit_price AS REAL) AS paid " +
                "FROM prices WHERE receipt_id = $rid AND item_id IS NOT NULL AND unit_price IS NOT NULL";
            cmd.Parameters.AddWithValue("$rid", receiptId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.GetDouble(2)));
        }
        return OpenBelowUsualAlertsFromRows(conn, rows);
    }

    // Shared body for both the date-window and receipt-scoped scans: for each (item, store) row priced far
    // enough below usual, open ONE below-usual alert (strongest per pair), skipping dismissed + already-open.
    private int OpenBelowUsualAlertsFromRows(SqliteConnection conn, List<(int ItemId, int StoreId, double Paid)> rows)
    {
        var dismissed = LoadRecentDismissedKeys(conn, null);
        var itemIds = rows.Select(x => x.ItemId).Distinct().ToList();
        var itemsMap = ItemsRepo.GetItemsByIds(conn, itemIds);
        var storeNameMap = StoresRepo.ListStores(conn).ToDictionary(s => s.Id, s => s.Name);
        var usualMap = PricesRepo.GetUsualUnitPriceBatch(conn, itemIds, receiptOnly: true,
            minSamples: MinReceiptSamplesForUsual, sinceDays: UsualLookbackDays);
        var sixLowMap = PricesRepo.GetSixMonthLowBatch(conn, itemIds, LowLookbackDays);

        // Keep only the strongest (largest pct_below) row per (item, store) so a staple bought cheaply
        // several times yields ONE alert, not one per receipt line.
        var best = new Dictionary<(int, int), PriceDropAlert>();
        foreach (var (itemId, storeId, paid) in rows)
        {
            if (paid <= 0 || !itemsMap.TryGetValue(itemId, out var item)) continue;
            var (usual, samples, basis) = usualMap.GetValueOrDefault(itemId, (null, 0, "unknown"));
            if (usual is null or <= 0) continue;

            var pctBelow = (usual.Value - paid) / usual.Value * 100.0;
            if (pctBelow < DropBelowUsualThresholdPct) continue;

            var key = new AlertKey(itemId, storeId, "below_usual");
            if (dismissed.Contains(key)) continue;

            var dedupe = (itemId, storeId);
            if (best.TryGetValue(dedupe, out var prev) && prev.PctBelowUsual >= pctBelow) continue;

            var (sixLow, sixLowWhen) = sixLowMap.GetValueOrDefault(itemId, (null, null));
            double? pctAboveLow = sixLow >= MinLowPriceFloor ? (paid - sixLow!.Value) / sixLow.Value * 100.0 : null;
            var storeName = storeNameMap.GetValueOrDefault(storeId, "Unknown");
            var notes = BuildNotes(storeName, paid, usual, pctBelow, sixLow, pctAboveLow, "below_usual",
                basis, samples, null, sixLowWhen);

            best[dedupe] = new PriceDropAlert(itemId, item.CanonicalName, storeId, storeName, paid, usual,
                pctBelow, sixLow, pctAboveLow, "below_usual", false, samples, basis, "receipt", null, notes);
        }

        var inserted = 0;
        using var tx = conn.BeginTransaction();
        var open = LoadOpenKeys(conn, tx, "receipt");
        foreach (var a in best.Values)
        {
            var key = new AlertKey(a.ItemId, a.StoreId, a.AlertKind);
            if (open.Contains(key)) continue;
            InsertAlert(conn, tx, a, source: "receipt");
            open.Add(key);
            inserted++;
        }
        tx.Commit();
        return inserted;
    }

    // ----------------------- persistence -----------------------

    private int PersistEngineAlerts(IReadOnlyList<PriceDropAlert> alerts)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        var dismissed = LoadRecentDismissedKeys(conn, tx);

        using (var del = Cmd(conn, tx,
            "DELETE FROM price_drop_alerts WHERE status = 'open' AND source = 'engine'"))
            del.ExecuteNonQuery();

        var inserted = 0;
        foreach (var a in alerts)
        {
            if (dismissed.Contains(new AlertKey(a.ItemId, a.StoreId, a.AlertKind))) continue;
            InsertAlert(conn, tx, a, source: "engine");
            inserted++;
        }
        tx.Commit();
        return inserted;
    }

    private static void InsertAlert(SqliteConnection conn, SqliteTransaction tx, PriceDropAlert a, string source)
    {
        using var cmd = Cmd(conn, tx,
            """
            INSERT INTO price_drop_alerts
                (item_id, store_id, store_name, item_name, current_price, usual_price, pct_below_usual,
                 six_month_low, pct_above_low, alert_kind, is_staple, receipt_samples, basis, source,
                 last_seen_at_or_below, notes, created_at, status, suggested_qty, suggested_qty_note)
            VALUES ($item, $store, $sname, $iname, $cur, $usual, $pctb, $low, $pcta, $kind, $staple, $samples,
                 $basis, $source, $lastseen, $notes, datetime('now'), 'open', $sqty, $sqtynote)
            """);
        cmd.Parameters.AddWithValue("$item", a.ItemId);
        cmd.Parameters.AddWithValue("$store", a.StoreId);
        cmd.Parameters.AddWithValue("$sname", a.StoreName);
        cmd.Parameters.AddWithValue("$iname", a.ItemName);
        cmd.Parameters.AddWithValue("$cur", a.CurrentPrice);
        cmd.Parameters.AddWithValue("$usual", OrNull(a.UsualPrice));
        cmd.Parameters.AddWithValue("$pctb", OrNull(a.PctBelowUsual));
        cmd.Parameters.AddWithValue("$low", OrNull(a.SixMonthLow));
        cmd.Parameters.AddWithValue("$pcta", OrNull(a.PctAboveLow));
        cmd.Parameters.AddWithValue("$kind", a.AlertKind);
        cmd.Parameters.AddWithValue("$staple", a.IsStaple ? 1 : 0);
        cmd.Parameters.AddWithValue("$samples", a.ReceiptSamples);
        cmd.Parameters.AddWithValue("$basis", a.Basis);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$lastseen", OrNull(a.LastSeenAtOrBelow));
        cmd.Parameters.AddWithValue("$notes", a.Notes);
        cmd.Parameters.AddWithValue("$sqty", OrNull(a.SuggestedQty));
        cmd.Parameters.AddWithValue("$sqtynote", OrNull(a.SuggestedQtyNote));
        cmd.ExecuteNonQuery();
    }

    private static HashSet<AlertKey> LoadRecentDismissedKeys(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = Cmd(conn, tx,
            "SELECT item_id, store_id, alert_kind FROM price_drop_alerts " +
            "WHERE status = 'dismissed' AND dismissed_at IS NOT NULL AND date(dismissed_at) >= date('now', $window)");
        cmd.Parameters.AddWithValue("$window", $"-{AlertSuppressionDays} days");
        using var r = cmd.ExecuteReader();
        var keys = new HashSet<AlertKey>();
        while (r.Read())
            keys.Add(new AlertKey(r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? "" : r.GetString(2)));
        return keys;
    }

    private static HashSet<AlertKey> LoadOpenKeys(SqliteConnection conn, SqliteTransaction? tx, string? source = null)
    {
        var sql = "SELECT item_id, store_id, alert_kind FROM price_drop_alerts WHERE status = 'open'";
        if (!string.IsNullOrEmpty(source)) sql += " AND source = $source";
        using var cmd = Cmd(conn, tx, sql);
        if (!string.IsNullOrEmpty(source)) cmd.Parameters.AddWithValue("$source", source);
        using var r = cmd.ExecuteReader();
        var keys = new HashSet<AlertKey>();
        while (r.Read())
            keys.Add(new AlertKey(r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? "" : r.GetString(2)));
        return keys;
    }

    private static PriceDropAlert MapAlertRow(SqliteDataReader r) => new(
        ItemId: r.IsDBNull(1) ? 0 : r.GetInt32(1),
        ItemName: StrOrNull(r, 4) ?? "",
        StoreId: r.IsDBNull(2) ? 0 : r.GetInt32(2),
        StoreName: StrOrNull(r, 3) ?? "",
        CurrentPrice: r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
        UsualPrice: DblOrNull(r, 6),
        PctBelowUsual: DblOrNull(r, 7),
        SixMonthLow: DblOrNull(r, 8),
        PctAboveLow: DblOrNull(r, 9),
        AlertKind: StrOrNull(r, 10) ?? "",
        IsStaple: !r.IsDBNull(11) && r.GetInt32(11) != 0,
        ReceiptSamples: r.IsDBNull(12) ? 0 : r.GetInt32(12),
        Basis: StrOrNull(r, 13) ?? "",
        Source: StrOrNull(r, 14) ?? "",
        LastSeenAtOrBelow: StrOrNull(r, 15),
        Notes: StrOrNull(r, 16) ?? "",
        SuggestedQty: DblOrNull(r, 19),
        SuggestedQtyNote: StrOrNull(r, 20),
        Id: IntOrNull(r, 0),
        CreatedAt: StrOrNull(r, 17),
        Status: StrOrNull(r, 18));

    // ----------------------- helpers -----------------------

    // Shared with ShoppingInsightsService (Shop Mode stock-up hint) so the quantity math and its wording
    // live in one place. Null when cadence data is missing/unusable — no guessed quantities.
    public static (double Qty, string Note)? SuggestStockUpQty(
        IReadOnlyDictionary<int, (double? AvgIntervalDays, double? TypicalQty)> cadence, int itemId)
    {
        var (avgInterval, typicalQty) = cadence.GetValueOrDefault(itemId, (null, null));
        if (avgInterval is null or <= 0 || typicalQty is null or <= 0) return null;
        var rawQty = StockUpHorizonDays / avgInterval.Value * typicalQty.Value;
        var qty = Math.Min(Math.Round(rawQty / typicalQty.Value) * typicalQty.Value, typicalQty.Value * MaxStockupMultiple);
        qty = Math.Max(qty, typicalQty.Value);
        var intervalWeeks = (int)Math.Round(avgInterval.Value / 7.0, MidpointRounding.AwayFromZero);
        return (qty, $"You buy this every ~{intervalWeeks} week(s); at this low, buy {G4(qty)}.");
    }

    private static bool PassesStockupCooldown(string? lastSeenIso)
    {
        if (string.IsNullOrEmpty(lastSeenIso)) return true; // can't tell -> allow (still near-low)
        if (DateTime.TryParse(lastSeenIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return (DateTime.UtcNow - dt).Days >= StockUpCooldownDays;
        return true;
    }

    private static string BuildNotes(string storeName, double current, double? usual, double? pctBelow,
        double? low, double? pctOverLow, string kind, string basis, int samples, string? lastSeen, string? lowWhen)
    {
        var parts = new List<string>();
        if (kind is "below_usual" or "both" && usual is not null && pctBelow is not null)
            parts.Add($"Dropped {pctBelow.Value.ToString("F0", CultureInfo.InvariantCulture)}% below usual " +
                      $"(${F2(current)} vs ${F2(usual.Value)}).");
        if (kind is "stock_up" or "both" && low is not null)
            parts.Add(pctOverLow is >= 0
                ? $"Near 6-month low (${F2(current)}; low ${F2(low.Value)}, +{F1(pctOverLow.Value)}%)."
                : $"Near 6-month low (${F2(current)}; low ${F2(low.Value)}).");

        parts.Add(basis switch
        {
            "receipt_median" => $"Usual is receipt median (samples: {samples}).",
            "estimated_median" => $"Usual is estimated median (samples: {samples}).",
            _ => "Usual price is unknown/insufficient history.",
        });

        if (!string.IsNullOrEmpty(lowWhen)) parts.Add($"6-mo low seen on: {Trunc(lowWhen)}.");
        if (!string.IsNullOrEmpty(lastSeen)) parts.Add($"Last time at/under this price: {Trunc(lastSeen)}.");
        parts.Add($"Best current store: {storeName}.");
        return string.Join(" ", parts).Trim();
    }

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string G4(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    private static string Trunc(string s) => s.Length > 10 ? s[..10] : s;

    // Local DB helpers (the Data project's Db/reader extensions are internal to that assembly).
    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object OrNull(object? value) => value ?? DBNull.Value;
    private static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? DblOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static int? IntOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
}
