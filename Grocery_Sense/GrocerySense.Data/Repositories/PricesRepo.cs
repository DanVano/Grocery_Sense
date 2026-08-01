using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/prices_repo.py (all 22 fns).
// The richest repo: single-item + batch readers and the median/"usual"/six-month-low math that
// Planning/Optimizer/Alerts depend on. Keeps the raw SQL + window functions; chunks IN-lists at 900 params.
//
// Money note: prices.unit_price/total_price are TEXT (the repo's decimal/TEXT money convention), but every
// MIN/MAX/AVG/ORDER on them is a numeric compare — TEXT affinity would sort "100" < "9" lexically. So price
// comparisons CAST(unit_price AS REAL). norm_unit_price is REAL already. PricePoint models unit_price as
// double, so reads use GetDouble. ponytail: CAST keeps this local; if unit_price ever becomes REAL the casts
// are redundant-but-harmless.
public static class PricesRepo
{
    private const int ParamChunk = 900; // SQLite default max variables is 999; stay under it.

    private const string PriceCols =
        "id, item_id, store_id, receipt_id, flyer_source_id, source, date, " +
        "unit_price, unit, quantity, total_price, raw_name, confidence, norm_unit_price, norm_unit";

    private static PricePoint MapPricePoint(SqliteDataReader r) => new(
        Id: r.GetInt32(0), ItemId: r.GetInt32(1), StoreId: r.GetInt32(2),
        Source: r.GetString(5), Date: r.GetString(6), UnitPrice: r.GetDouble(7), Unit: r.GetString(8),
        Quantity: r.GetDoubleOrNull(9), TotalPrice: r.GetDoubleOrNull(10),
        ReceiptId: r.GetIntOrNull(3), FlyerSourceId: r.GetIntOrNull(4),
        RawName: r.GetStringOrNull(11), Confidence: r.GetIntOrNull(12),
        NormUnitPrice: r.GetDoubleOrNull(13), NormUnit: r.GetStringOrNull(14));

    // ---------- CRUD + basic stats ----------

    // Returns the inserted row id. date defaults to today (yyyy-MM-dd) when null.
    public static int AddPricePoint(SqliteConnection conn, int itemId, int storeId, double unitPrice, string unit,
        double? quantity = null, double? totalPrice = null, string? rawName = null, int? confidence = null,
        string source = "manual", string? date = null, int? receiptId = null, int? flyerSourceId = null,
        SqliteTransaction? tx = null)
    {
        date ??= DateTime.Now.ToString("yyyy-MM-dd");
        using var cmd = Db.Command(conn, tx,
            """
            INSERT INTO prices (item_id, store_id, receipt_id, flyer_source_id, source, date,
                unit_price, unit, quantity, total_price, raw_name, confidence)
            VALUES ($item, $store, $rid, $fsid, $source, $date, $uprice, $unit, $qty, $total, $raw, $conf)
            """);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$store", storeId);
        cmd.Parameters.AddWithValue("$rid", Db.OrNull(receiptId));
        cmd.Parameters.AddWithValue("$fsid", Db.OrNull(flyerSourceId));
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$uprice", unitPrice);
        cmd.Parameters.AddWithValue("$unit", unit);
        cmd.Parameters.AddWithValue("$qty", Db.OrNull(quantity));
        cmd.Parameters.AddWithValue("$total", Db.OrNull(totalPrice));
        cmd.Parameters.AddWithValue("$raw", Db.OrNull(rawName));
        cmd.Parameters.AddWithValue("$conf", Db.OrNull(confidence));
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    // Price points for an item, oldest-first. With limit set, fetches the most-recent N (DESC+LIMIT) then
    // reverses to preserve the no-limit ASC contract.
    public static IReadOnlyList<PricePoint> GetPricesForItem(SqliteConnection conn, int itemId,
        int sinceDays = 365, int? limit = null, SqliteTransaction? tx = null)
    {
        var cutoff = CutoffIso(sinceDays);
        var sql = $"SELECT {PriceCols} FROM prices WHERE item_id = $item AND date >= $cutoff";
        sql += limit is null ? " ORDER BY date ASC" : " ORDER BY date DESC LIMIT $limit";

        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", limit.Value);

        using var r = cmd.ExecuteReader();
        var rows = new List<PricePoint>();
        while (r.Read()) rows.Add(MapPricePoint(r));
        if (limit is not null) rows.Reverse(); // fetched DESC to honour LIMIT; flip back to ASC.
        return rows;
    }

    // Batch of GetPricesForItem (all stores, oldest-first) for many items in one round-trip — replaces the
    // optimizer's per-item history loop. Every requested id gets an entry (empty list if it has no rows in
    // the window). Same `date >= cutoff` filter and ASC ordering as the single-item reader; id ASC tiebreak
    // makes same-date ordering deterministic.
    public static IReadOnlyDictionary<int, IReadOnlyList<PricePoint>> GetPricesForItemsBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, int sinceDays = 365, SqliteTransaction? tx = null)
    {
        var ids = CoerceIds(itemIds);
        var rows = ids.ToDictionary(i => i, _ => new List<PricePoint>());
        if (ids.Count == 0) return rows.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PricePoint>)kv.Value);

        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                $"SELECT {PriceCols} FROM prices WHERE item_id IN ({ph}) AND date >= $cutoff " +
                "ORDER BY item_id ASC, date ASC, id ASC");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$cutoff", CutoffIso(sinceDays));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var point = MapPricePoint(reader);
                if (rows.TryGetValue(point.ItemId, out var list)) list.Add(point);
            }
        }
        return rows.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<PricePoint>)kv.Value);
    }

    public static PriceStats GetPriceStatsForItem(SqliteConnection conn, int itemId, int? storeId = null,
        int sinceDays = 365, SqliteTransaction? tx = null)
    {
        var cutoff = CutoffIso(sinceDays);
        var sql =
            "SELECT MIN(CAST(unit_price AS REAL)), MAX(CAST(unit_price AS REAL)), AVG(CAST(unit_price AS REAL)), COUNT(*) " +
            "FROM prices WHERE item_id = $item AND date >= $cutoff AND unit_price IS NOT NULL";
        if (storeId is not null) sql += " AND store_id = $store";

        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(3) || r.GetInt32(3) == 0)
            return new PriceStats(itemId, storeId, null, null, null, 0);
        return new PriceStats(itemId, storeId, r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetInt32(3));
    }

    // ---------- "usual" price + six-month-low + staples ----------

    // unit_price history for an item (newest-first). receiptOnly filters to receipt line-items; otherwise
    // sources (if given) narrows the source list. Uses COALESCE(date, created_at) for the time window.
    private static IReadOnlyList<double> ListUnitPrices(SqliteConnection conn, int itemId, int? storeId = null,
        int sinceDays = 180, IReadOnlyList<string>? sources = null, bool receiptOnly = false, int? limit = null,
        SqliteTransaction? tx = null)
    {
        var sql =
            "SELECT unit_price FROM prices WHERE item_id = $item " +
            "AND unit_price IS NOT NULL AND CAST(unit_price AS REAL) > 0 " +
            "AND date(COALESCE(date, created_at)) >= date('now', $since)";
        if (storeId is not null) sql += " AND store_id = $store";

        var sourceParams = new List<string>();
        if (receiptOnly) sql += " AND (source = 'receipt' OR receipt_id IS NOT NULL)";
        else if (sources is { Count: > 0 })
        {
            for (var i = 0; i < sources.Count; i++) sourceParams.Add($"$src{i}");
            sql += $" AND source IN ({string.Join(",", sourceParams)})";
        }
        sql += " ORDER BY COALESCE(date, created_at) DESC";
        if (limit is > 0) sql += " LIMIT $limit";

        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$item", itemId);
        cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        if (!receiptOnly && sources is { Count: > 0 })
            for (var i = 0; i < sources.Count; i++) cmd.Parameters.AddWithValue(sourceParams[i], sources[i]);
        if (limit is > 0) cmd.Parameters.AddWithValue("$limit", limit.Value);

        using var r = cmd.ExecuteReader();
        var prices = new List<double>();
        while (r.Read()) if (!r.IsDBNull(0)) prices.Add(r.GetDouble(0));
        return prices;
    }

    // (usual_price, sample_count, basis). basis: receipt_median | estimated_median | unknown.
    public static (double? Price, int Samples, string Basis) GetUsualUnitPrice(SqliteConnection conn, int itemId,
        int? storeId = null, bool receiptOnly = true, int minSamples = 4, int sinceDays = 180,
        SqliteTransaction? tx = null)
    {
        var prices = ListUnitPrices(conn, itemId, storeId, sinceDays, receiptOnly: receiptOnly, tx: tx);
        if (prices.Count >= minSamples)
            return (Median(prices), prices.Count, receiptOnly ? "receipt_median" : "estimated_median");

        if (receiptOnly)
        {
            var fallback = ListUnitPrices(conn, itemId, storeId, sinceDays, receiptOnly: false, tx: tx);
            if (fallback.Count > 0) return (Median(fallback), fallback.Count, "estimated_median");
        }
        return (null, prices.Count, "unknown");
    }

    // Likely staples from receipt history: (item_id, line_count, distinct_receipt_count).
    // This is the one prices read with no item_id bound, so the date window is the only selective predicate.
    // INDEXED BY forces the seek onto idx_prices_coalesced_date (migration 9): without stats SQLite guesses a
    // >= range hits 25% of rows and instead full-scans the item-ordered idx_prices_item_coalesced (free GROUP
    // BY order), so the cost tracked total history. Forcing the date index makes it track the window instead.
    // ponytail: this pessimizes the all-recent-data case (window ≈ whole table -> a scan would've been cheaper
    // than seek+sort), but that case is small and fast anyway; the win is the large-history case. Drop the
    // INDEXED BY if a periodic ANALYZE/PRAGMA optimize ever runs — real stats let the planner choose correctly.
    public static IReadOnlyList<(int ItemId, int LineCount, int DistinctReceipts)> ListStapleItemIds(
        SqliteConnection conn, int sinceDays = 90, int minDistinctReceipts = 3, int minLineItems = 4,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            SELECT item_id, COUNT(*) AS line_count, COUNT(DISTINCT receipt_id) AS receipt_count
            FROM prices INDEXED BY idx_prices_coalesced_date
            WHERE item_id IS NOT NULL AND unit_price IS NOT NULL
              AND (source = 'receipt' OR receipt_id IS NOT NULL)
              AND date(COALESCE(date, created_at)) >= date('now', $since)
            GROUP BY item_id
            HAVING line_count >= $minLines OR receipt_count >= $minReceipts
            ORDER BY receipt_count DESC, line_count DESC
            """);
        cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
        cmd.Parameters.AddWithValue("$minLines", minLineItems);
        cmd.Parameters.AddWithValue("$minReceipts", minDistinctReceipts);
        using var r = cmd.ExecuteReader();
        var rows = new List<(int, int, int)>();
        while (r.Read()) rows.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        return rows;
    }

    // ---------- Batch readers (single round-trip; replace N+1 service loops) ----------

    public static IReadOnlyDictionary<int, (double? Price, int Samples, string Basis)> GetUsualUnitPriceBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, bool receiptOnly = true, int minSamples = 4,
        int sinceDays = 180, SqliteTransaction? tx = null)
    {
        var ids = CoerceIds(itemIds);
        var result = new Dictionary<int, (double?, int, string)>();
        if (ids.Count == 0) return result;

        var receiptRows = ids.ToDictionary(i => i, _ => new List<double>());
        var allRows = ids.ToDictionary(i => i, _ => new List<double>());

        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                $"SELECT item_id, unit_price, " +
                "CASE WHEN (source = 'receipt' OR receipt_id IS NOT NULL) THEN 1 ELSE 0 END AS is_receipt " +
                $"FROM prices WHERE item_id IN ({ph}) " +
                "AND unit_price IS NOT NULL AND CAST(unit_price AS REAL) > 0 " +
                "AND date(COALESCE(date, created_at)) >= date('now', $since) ORDER BY item_id");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                var iid = r.GetInt32(0);
                if (!allRows.TryGetValue(iid, out var all)) continue;
                var price = r.GetDouble(1);
                all.Add(price);
                if (r.GetInt32(2) == 1) receiptRows[iid].Add(price);
            }
        }

        foreach (var iid in ids)
        {
            var rp = receiptRows[iid];
            var ap = allRows[iid];
            if (rp.Count >= minSamples) result[iid] = (Median(rp), rp.Count, "receipt_median");
            else if (receiptOnly && ap.Count > 0) result[iid] = (Median(ap), ap.Count, "estimated_median");
            else result[iid] = (null, receiptOnly ? rp.Count : ap.Count, "unknown");
        }
        return result;
    }

    public static IReadOnlyDictionary<int, (double? Price, string? WhenIso)> GetSixMonthLowBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, int sinceDays = 183, SqliteTransaction? tx = null)
    {
        var ids = CoerceIds(itemIds);
        var result = new Dictionary<int, (double?, string?)>();
        if (ids.Count == 0) return result;
        foreach (var iid in ids) result[iid] = (null, null);

        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT item_id, unit_price, when_iso FROM ( " +
                "  SELECT item_id, unit_price, COALESCE(date, created_at) AS when_iso, " +
                "         ROW_NUMBER() OVER (PARTITION BY item_id " +
                "           ORDER BY CAST(unit_price AS REAL) ASC, COALESCE(date, created_at) ASC) AS rn " +
                $"  FROM prices WHERE item_id IN ({ph}) " +
                "    AND unit_price IS NOT NULL AND CAST(unit_price AS REAL) > 0 " +
                "    AND date(COALESCE(date, created_at)) >= date('now', $since) " +
                ") WHERE rn = 1");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                result[r.GetInt32(0)] = (r.GetDouble(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
        }
        return result;
    }

    // itemIdToCeiling: per-item price ceiling (typically its current best price). Returns the most recent
    // date each item was at/below its ceiling. Fetches candidates in one query; filters per-item in C#.
    public static IReadOnlyDictionary<int, string?> GetLastSeenAtOrBelowBatch(SqliteConnection conn,
        IReadOnlyDictionary<int, double> itemIdToCeiling, int sinceDays = 183, SqliteTransaction? tx = null)
    {
        var ids = CoerceIds(itemIdToCeiling.Keys);
        var result = new Dictionary<int, string?>();
        if (ids.Count == 0) return result;
        foreach (var iid in ids) result[iid] = null;
        var seen = new HashSet<int>();

        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT item_id, unit_price, COALESCE(date, created_at) AS when_iso FROM prices " +
                $"WHERE item_id IN ({ph}) AND unit_price IS NOT NULL AND CAST(unit_price AS REAL) > 0 " +
                "AND date(COALESCE(date, created_at)) >= date('now', $since) ORDER BY item_id, when_iso DESC");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1)) continue;
                var iid = r.GetInt32(0);
                if (seen.Contains(iid)) continue; // rows are newest-first per item; first hit wins.
                if (itemIdToCeiling.TryGetValue(iid, out var ceiling) && r.GetDouble(1) <= ceiling)
                {
                    result[iid] = r.IsDBNull(2) ? null : r.GetString(2);
                    seen.Add(iid);
                }
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<(int ItemId, int StoreId), PricePoint> GetMostRecentPricesByStoreBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, IReadOnlyList<int> storeIds, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var stores = CoerceIds(storeIds);
        var result = new Dictionary<(int, int), PricePoint>();
        if (items.Count == 0 || stores.Count == 0) return result;

        var storePh = Placeholders(stores.Count, "s");
        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var itemPh = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                $"SELECT {PriceCols} FROM ( " +
                $"  SELECT {PriceCols}, ROW_NUMBER() OVER (" +
                "      PARTITION BY item_id, store_id ORDER BY date DESC, id DESC) AS rn " +
                $"  FROM prices WHERE item_id IN ({itemPh}) AND store_id IN ({storePh}) " +
                "    AND unit_price IS NOT NULL ) WHERE rn = 1");
            BindIn(cmd, chunk);
            BindIn(cmd, stores, "s");
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var pp = MapPricePoint(r); result[(pp.ItemId, pp.StoreId)] = pp; }
        }
        return result;
    }

    public static IReadOnlyDictionary<int, PricePoint> GetMostRecentPricesGlobalBatch(SqliteConnection conn,
        IReadOnlyList<int> itemIds, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var result = new Dictionary<int, PricePoint>();
        if (items.Count == 0) return result;

        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                $"SELECT {PriceCols} FROM ( " +
                $"  SELECT {PriceCols}, ROW_NUMBER() OVER (" +
                "      PARTITION BY item_id ORDER BY date DESC, id DESC) AS rn " +
                $"  FROM prices WHERE item_id IN ({ph}) AND unit_price IS NOT NULL ) WHERE rn = 1");
            BindIn(cmd, chunk);
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var pp = MapPricePoint(r); result[pp.ItemId] = pp; }
        }
        return result;
    }

    // Lowest active flyer price per (item, store). Reads the flyer_deals/flyer_batches family — the one
    // FlyerSyncService and FlyerIngestService actually populate. The prices/flyer_sources flyer path is
    // retired (it was never written in production); validity semantics deliberately mirror
    // FlyersRepo.ListActiveDeals (NULL = open-ended, ISO-string compare) so every flyer surface agrees.
    public static IReadOnlyDictionary<(int ItemId, int StoreId), PriceQuote> GetActiveFlyerPricesBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, IReadOnlyList<int> storeIds, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var stores = CoerceIds(storeIds);
        var result = new Dictionary<(int, int), PriceQuote>();
        if (items.Count == 0 || stores.Count == 0) return result;

        var onDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var storePh = Placeholders(stores.Count, "s");
        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var itemPh = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT d.item_id, d.store_id, " +
                "       MIN(COALESCE(CAST(d.norm_unit_price AS REAL), CAST(d.unit_price AS REAL))) AS unit_price, " +
                "       COALESCE(d.norm_unit, d.unit, 'each') AS unit " +
                "FROM flyer_deals d JOIN flyer_batches b ON b.id = d.flyer_id " +
                "WHERE b.status = 'active' " +
                $"  AND d.item_id IN ({itemPh}) AND d.store_id IN ({storePh}) " +
                "  AND COALESCE(CAST(d.norm_unit_price AS REAL), CAST(d.unit_price AS REAL)) > 0 " +
                "  AND (b.valid_from IS NULL OR b.valid_from <= $onDate) " +
                "  AND (b.valid_to IS NULL OR b.valid_to >= $onDate) " +
                "GROUP BY d.item_id, d.store_id");
            cmd.Parameters.AddWithValue("$onDate", onDate);
            BindIn(cmd, chunk);
            BindIn(cmd, stores, "s");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(2)) continue;
                var unit = (r.IsDBNull(3) ? "each" : r.GetString(3)).Trim().ToLowerInvariant();
                result[(r.GetInt32(0), r.GetInt32(1))] = new PriceQuote(r.GetDouble(2), "flyer", unit);
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<int, PriceStats> GetPriceStatsBatch(SqliteConnection conn,
        IReadOnlyList<int> itemIds, int sinceDays = 180, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var result = new Dictionary<int, PriceStats>();
        if (items.Count == 0) return result;

        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT item_id, MIN(COALESCE(norm_unit_price, CAST(unit_price AS REAL))), " +
                "       MAX(COALESCE(norm_unit_price, CAST(unit_price AS REAL))), " +
                "       AVG(COALESCE(norm_unit_price, CAST(unit_price AS REAL))), COUNT(*) " +
                $"FROM prices WHERE item_id IN ({ph}) AND unit_price IS NOT NULL " +
                "AND date(COALESCE(date, created_at)) >= date('now', $since) GROUP BY item_id");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var iid = r.GetInt32(0);
                result[iid] = new PriceStats(iid, null, r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetInt32(4));
            }
        }
        return result;
    }

    // Average of the most-recent `limit` unit prices per (item, store) in the window. Parity: rows ranked
    // date DESC, top `limit` kept (NULLs count toward the limit), then AVG skips NULLs.
    public static IReadOnlyDictionary<(int ItemId, int StoreId), double> GetRecentAvgUnitPriceByStoreBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, IReadOnlyList<int> storeIds, int sinceDays = 180,
        int limit = 12, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var stores = CoerceIds(storeIds);
        var result = new Dictionary<(int, int), double>();
        if (items.Count == 0 || stores.Count == 0) return result;

        var cutoff = CutoffIso(sinceDays);
        var lim = limit > 0 ? limit : (int?)null;
        var storePh = Placeholders(stores.Count, "s");
        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var itemPh = Placeholders(chunk.Length);
            string sql = lim is null
                ? "SELECT item_id, store_id, AVG(COALESCE(norm_unit_price, CAST(unit_price AS REAL))) FROM prices " +
                  $"WHERE item_id IN ({itemPh}) AND store_id IN ({storePh}) AND date >= $cutoff AND unit_price IS NOT NULL " +
                  "GROUP BY item_id, store_id"
                : "SELECT item_id, store_id, AVG(COALESCE(norm_unit_price, CAST(unit_price AS REAL))) FROM ( " +
                  "  SELECT item_id, store_id, norm_unit_price, unit_price, " +
                  "         ROW_NUMBER() OVER (PARTITION BY item_id, store_id ORDER BY date DESC, id DESC) AS rn " +
                  $"  FROM prices WHERE item_id IN ({itemPh}) AND store_id IN ({storePh}) AND date >= $cutoff " +
                  ") WHERE rn <= $limit AND unit_price IS NOT NULL GROUP BY item_id, store_id";
            using var cmd = Db.Command(conn, tx, sql);
            BindIn(cmd, chunk);
            BindIn(cmd, stores, "s");
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            if (lim is not null) cmd.Parameters.AddWithValue("$limit", lim.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(2)) result[(r.GetInt32(0), r.GetInt32(1))] = r.GetDouble(2);
        }
        return result;
    }

    public static IReadOnlyDictionary<int, double> GetRecentAvgUnitPriceGlobalBatch(SqliteConnection conn,
        IReadOnlyList<int> itemIds, int sinceDays = 180, int limit = 20, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var result = new Dictionary<int, double>();
        if (items.Count == 0) return result;

        var cutoff = CutoffIso(sinceDays);
        var lim = limit > 0 ? limit : (int?)null;
        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            string sql = lim is null
                ? "SELECT item_id, AVG(COALESCE(norm_unit_price, CAST(unit_price AS REAL))) FROM prices " +
                  $"WHERE item_id IN ({ph}) AND date >= $cutoff AND unit_price IS NOT NULL GROUP BY item_id"
                : "SELECT item_id, AVG(COALESCE(norm_unit_price, CAST(unit_price AS REAL))) FROM ( " +
                  "  SELECT item_id, norm_unit_price, unit_price, " +
                  "         ROW_NUMBER() OVER (PARTITION BY item_id ORDER BY date DESC, id DESC) AS rn " +
                  $"  FROM prices WHERE item_id IN ({ph}) AND date >= $cutoff " +
                  ") WHERE rn <= $limit AND unit_price IS NOT NULL GROUP BY item_id";
            using var cmd = Db.Command(conn, tx, sql);
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            if (lim is not null) cmd.Parameters.AddWithValue("$limit", lim.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(1)) result[r.GetInt32(0)] = r.GetDouble(1);
        }
        return result;
    }

    // (avg_interval_days, typical_qty) for staples. Interval is null with <2 distinct receipts; receipt rows only.
    public static IReadOnlyDictionary<int, (double? AvgIntervalDays, double? TypicalQty)> GetPurchaseCadenceBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, int sinceDays = 180, SqliteTransaction? tx = null)
    {
        var items = CoerceIds(itemIds);
        var result = new Dictionary<int, (double?, double?)>();
        if (items.Count == 0) return result;

        foreach (var chunk in items.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT item_id, COUNT(DISTINCT receipt_id) AS receipt_count, " +
                "       MIN(date(COALESCE(date, created_at))) AS first_date, " +
                "       MAX(date(COALESCE(date, created_at))) AS last_date, " +
                "       AVG(CASE WHEN quantity IS NOT NULL AND quantity > 0 THEN quantity END) AS avg_qty " +
                $"FROM prices WHERE item_id IN ({ph}) " +
                "  AND (source = 'receipt' OR receipt_id IS NOT NULL) " +
                "  AND date(COALESCE(date, created_at)) >= date('now', $since) GROUP BY item_id");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var iid = r.GetInt32(0);
                var receiptCount = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                var firstDate = r.IsDBNull(2) ? null : r.GetString(2);
                var lastDate = r.IsDBNull(3) ? null : r.GetString(3);
                double? avgQty = r.IsDBNull(4) ? null : r.GetDouble(4);

                double? avgInterval = null;
                if (receiptCount >= 2 && firstDate is not null && lastDate is not null && firstDate != lastDate
                    && DateOnly.TryParse(firstDate, out var d0) && DateOnly.TryParse(lastDate, out var d1))
                {
                    var span = d1.DayNumber - d0.DayNumber;
                    if (span > 0) avgInterval = span / (double)(receiptCount - 1);
                }
                result[iid] = (avgInterval, avgQty);
            }
        }
        return result;
    }

    // Most recent receipt-sourced purchase date per item (ISO yyyy-MM-dd), for the planner's
    // "likely still have" pantry inference. Items with no receipt purchase in the window are absent.
    public static IReadOnlyDictionary<int, string> GetLastReceiptPurchaseBatch(
        SqliteConnection conn, IReadOnlyList<int> itemIds, int sinceDays = 180, SqliteTransaction? tx = null)
    {
        var ids = CoerceIds(itemIds);
        var result = new Dictionary<int, string>();
        if (ids.Count == 0) return result;

        foreach (var chunk in ids.Chunk(ParamChunk))
        {
            var ph = Placeholders(chunk.Length);
            using var cmd = Db.Command(conn, tx,
                "SELECT item_id, MAX(date(COALESCE(date, created_at))) FROM prices " +
                $"WHERE item_id IN ({ph}) AND (source = 'receipt' OR receipt_id IS NOT NULL) " +
                "  AND date(COALESCE(date, created_at)) >= date('now', $since) GROUP BY item_id");
            BindIn(cmd, chunk);
            cmd.Parameters.AddWithValue("$since", SinceClause(sinceDays));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1))
                    result[r.GetInt32(0)] = r.GetString(1);
        }
        return result;
    }

    // ---------- helpers ----------

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var v = values.OrderBy(x => x).ToList();
        var n = v.Count;
        var mid = n / 2;
        return n % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
    }

    // SQLite date('now', ?) modifier, e.g. "-180 day". Floor at 1 day (matches Python).
    private static string SinceClause(int days) => $"-{Math.Max(1, days)} day";

    // ISO date `since_days` ago (UTC), for the `date >= ?` lexical range filters.
    private static string CutoffIso(int sinceDays) =>
        DateTime.UtcNow.Date.AddDays(-sinceDays).ToString("yyyy-MM-dd");

    private static List<int> CoerceIds(IEnumerable<int> ids) => ids.Where(x => x > 0).Distinct().ToList();

    private static string Placeholders(int count, string prefix = "p") =>
        string.Join(",", Enumerable.Range(0, count).Select(i => $"${prefix}{i}"));

    private static void BindIn(SqliteCommand cmd, IReadOnlyList<int> values, string prefix = "p")
    {
        for (var i = 0; i < values.Count; i++) cmd.Parameters.AddWithValue($"${prefix}{i}", values[i]);
    }

}
