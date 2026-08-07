using System.Globalization;
using System.Text.Json;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/receipts_repo.py
// Reads return typed records (money as decimal). Spend totals are summed in C# (decimal) rather than via
// SQL SUM, which would coerce the TEXT money columns to lossy REAL. Multi-write ops (cascade delete,
// delete-with-backup, restore) run in a transaction — the caller's if supplied, otherwise a local one.
public static class ReceiptsRepo
{
    private const long MaxBackupBytes = 50L * 1024 * 1024;
    private const int MaxBackupsKept = 50; // newest N undo snapshots retained (each may embed full OCR JSON)

    public static IReadOnlyList<ReceiptSummary> ListRecentReceipts(SqliteConnection conn, int limit = 50,
        int offset = 0, int? storeId = null, string? since = null, string? until = null, SqliteTransaction? tx = null)
    {
        var where = new List<string>();
        if (storeId is not null) where.Add("r.store_id = $store");
        if (!string.IsNullOrEmpty(since)) where.Add("r.purchase_date >= $since");
        if (!string.IsNullOrEmpty(until)) where.Add("r.purchase_date <= $until");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        // Select the receipt PAGE first (filter + limit/offset), then count line items only for that page.
        // The old shape GROUP BY'd the entire receipt_line_items table before applying the LIMIT, so recent-
        // receipt latency grew with total line-item history rather than with the page actually returned.
        using var cmd = Db.Command(conn, tx,
            $"""
            WITH page AS (
                SELECT r.id, r.purchase_date, r.total_amount, r.subtotal_amount, r.tax_amount,
                       r.store_id, r.file_path, r.created_at
                FROM receipts r
                {whereSql}
                ORDER BY r.id DESC
                LIMIT $limit OFFSET $offset
            )
            SELECT p.id, p.purchase_date, p.total_amount, p.subtotal_amount, p.tax_amount, p.store_id,
                   COALESCE(s.name, '') AS store_name, p.file_path, p.created_at,
                   COUNT(li.id) AS item_count
            FROM page p
            LEFT JOIN stores s ON s.id = p.store_id
            LEFT JOIN receipt_line_items li ON li.receipt_id = p.id
            GROUP BY p.id
            ORDER BY p.id DESC
            """);
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        if (!string.IsNullOrEmpty(since)) cmd.Parameters.AddWithValue("$since", since);
        if (!string.IsNullOrEmpty(until)) cmd.Parameters.AddWithValue("$until", until);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var r = cmd.ExecuteReader();
        var rows = new List<ReceiptSummary>();
        while (r.Read())
            rows.Add(new ReceiptSummary(
                Id: r.GetInt32(0), PurchaseDate: r.GetString(1),
                TotalAmount: r.GetMoneyOrNull(2), SubtotalAmount: r.GetMoneyOrNull(3), TaxAmount: r.GetMoneyOrNull(4),
                StoreId: r.GetInt32(5), StoreName: r.GetString(6),
                FilePath: r.GetStringOrNull(7), CreatedAt: r.GetStringOrNull(8), ItemCount: r.GetInt32(9)));
        return rows;
    }

    public static ReceiptDetail? GetReceipt(SqliteConnection conn, int receiptId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            SELECT r.id, r.purchase_date, r.total_amount, r.subtotal_amount, r.tax_amount, r.store_id,
                   COALESCE(s.name, '') AS store_name, r.file_path, r.source, r.azure_request_id, r.created_at
            FROM receipts r
            LEFT JOIN stores s ON s.id = r.store_id
            WHERE r.id = $id
            """);
        cmd.Parameters.AddWithValue("$id", receiptId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ReceiptDetail(
            Id: r.GetInt32(0), PurchaseDate: r.GetString(1),
            TotalAmount: r.GetMoneyOrNull(2), SubtotalAmount: r.GetMoneyOrNull(3), TaxAmount: r.GetMoneyOrNull(4),
            StoreId: r.GetInt32(5), StoreName: r.GetString(6), FilePath: r.GetStringOrNull(7),
            Source: r.GetString(8), AzureRequestId: r.GetStringOrNull(9), CreatedAt: r.GetStringOrNull(10));
    }

    public static IReadOnlyList<ReceiptLineItemRow> ListReceiptLineItems(SqliteConnection conn, int receiptId,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            SELECT li.id, li.line_index, li.item_id, COALESCE(i.canonical_name, '') AS canonical_name,
                   COALESCE(li.description, '') AS description, li.quantity, li.unit_price, li.line_total,
                   li.discount, li.confidence
            FROM receipt_line_items li
            LEFT JOIN items i ON i.id = li.item_id
            WHERE li.receipt_id = $id
            ORDER BY li.line_index ASC
            """);
        cmd.Parameters.AddWithValue("$id", receiptId);
        using var r = cmd.ExecuteReader();
        var rows = new List<ReceiptLineItemRow>();
        while (r.Read())
            rows.Add(new ReceiptLineItemRow(
                Id: r.GetInt32(0), LineIndex: r.GetInt32(1), ItemId: r.GetIntOrNull(2),
                CanonicalName: r.GetString(3), Description: r.GetString(4), Quantity: r.GetDoubleOrNull(5),
                UnitPrice: r.GetMoneyOrNull(6), LineTotal: r.GetMoneyOrNull(7), Discount: r.GetMoneyOrNull(8),
                Confidence: r.GetIntOrNull(9)));
        return rows;
    }

    public static MonthSpend GetMonthSpend(SqliteConnection conn, string yearMonth, SqliteTransaction? tx = null)
    {
        // Parse "yyyy-MM" into a half-open [start, end) date range so the query can seek
        // idx_receipts_purchase_date instead of STRFTIME-scanning every receipt. purchase_date is ISO TEXT,
        // so a lexical range compare is a correct date-range compare. AddMonths handles Dec->Jan and leap Feb.
        if (!DateOnly.TryParseExact((yearMonth ?? "").Trim() + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            throw new ArgumentException($"yearMonth must be in 'yyyy-MM' format: '{yearMonth}'", nameof(yearMonth));
        var startIso = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = start.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var cmd = Db.Command(conn, tx,
            "SELECT total_amount FROM receipts WHERE purchase_date >= $start AND purchase_date < $end AND total_amount IS NOT NULL");
        cmd.Parameters.AddWithValue("$start", startIso);
        cmd.Parameters.AddWithValue("$end", endIso);
        using var r = cmd.ExecuteReader();
        decimal total = 0m;
        var count = 0;
        while (r.Read()) { total += r.GetDecimal(0); count++; }
        // Echo the canonical month (parsed, so never null and whitespace-normalized) as the label.
        return new MonthSpend(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), total, count);
    }

    // Per-store spend for one month (F03). Same half-open ISO range trick as GetMonthSpend; TEXT money
    // summed in C# decimal (SQL SUM over money is banned). Biggest spend first.
    public static IReadOnlyList<StoreMonthSpend> GetMonthSpendByStore(SqliteConnection conn, string yearMonth,
        SqliteTransaction? tx = null)
    {
        if (!DateOnly.TryParseExact((yearMonth ?? "").Trim() + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            throw new ArgumentException($"yearMonth must be in 'yyyy-MM' format: '{yearMonth}'", nameof(yearMonth));
        var startIso = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endIso = start.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var cmd = Db.Command(conn, tx,
            """
            SELECT r.store_id, COALESCE(s.name, '') AS store_name, r.total_amount
            FROM receipts r
            LEFT JOIN stores s ON s.id = r.store_id
            WHERE r.purchase_date >= $start AND r.purchase_date < $end AND r.total_amount IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("$start", startIso);
        cmd.Parameters.AddWithValue("$end", endIso);

        var agg = new Dictionary<int, (string Name, decimal Total, int Count)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var storeId = r.GetInt32(0);
                agg.TryGetValue(storeId, out var cur);
                agg[storeId] = (r.GetString(1), cur.Total + r.GetDecimal(2), cur.Count + 1);
            }
        }
        return agg
            .Select(kv => new StoreMonthSpend(kv.Key, kv.Value.Name, kv.Value.Total, kv.Value.Count))
            .OrderByDescending(x => x.Total)
            .ToList();
    }

    // todayIso: local calendar "today" (V3 local-date convention) — SQLite's DATE('now') is UTC and made
    // the trend window drift at day rollover for zones behind UTC. Callers pass local today; defaults local.
    public static IReadOnlyList<MonthSpend> GetSpendTrend(SqliteConnection conn, int months = 12,
        SqliteTransaction? tx = null, string? todayIso = null)
    {
        months = Math.Max(1, months);
        using var cmd = Db.Command(conn, tx,
            """
            SELECT STRFTIME('%Y-%m', purchase_date) AS month, total_amount
            FROM receipts
            WHERE total_amount IS NOT NULL AND purchase_date >= DATE($today, $delta || ' months')
            """);
        cmd.Parameters.AddWithValue("$today", todayIso ?? DateTime.Now.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$delta", $"-{months}");

        var agg = new SortedDictionary<string, (decimal Total, int Count)>(StringComparer.Ordinal);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var month = r.GetString(0);
                var amount = r.GetDecimal(1);
                agg.TryGetValue(month, out var cur); // defaults to (0, 0) when absent
                agg[month] = (cur.Total + amount, cur.Count + 1);
            }
        }
        return agg.Select(kv => new MonthSpend(kv.Key, kv.Value.Total, kv.Value.Count)).ToList();
    }

    // ---- Ingest dedupe lookups + transactional receipt write (ReceiptIngestionService, Phase 5) ----

    public static int? FindReceiptIdByFileHash(SqliteConnection conn, string fileHash, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT receipt_id FROM receipt_file_hashes WHERE file_hash = $h");
        cmd.Parameters.AddWithValue("$h", fileHash);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt32(v) : null;
    }

    public static int? FindReceiptIdBySignature(SqliteConnection conn, string signature, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT receipt_id FROM receipt_signatures WHERE signature = $s");
        cmd.Parameters.AddWithValue("$s", signature);
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt32(v) : null;
    }

    // Writes receipt + raw_json + line_items + prices + dedupe links. Caller owns the transaction.
    public static int IngestReceipt(SqliteConnection conn, ReceiptIngest r, SqliteTransaction tx)
    {
        var now = Db.NowIso();
        using (var cmd = Db.Command(conn, tx,
            """
            INSERT INTO receipts (store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                source, file_path, image_overall_confidence, keep_image_until, azure_request_id, created_at)
            VALUES ($store, $date, $sub, $tax, $total, 'receipt', $path, $conf, NULL, $op, $now)
            """))
        {
            cmd.Parameters.AddWithValue("$store", r.StoreId);
            cmd.Parameters.AddWithValue("$date", r.PurchaseDate);
            cmd.Parameters.AddWithValue("$sub", Db.OrNull(Dec(r.Subtotal)));
            cmd.Parameters.AddWithValue("$tax", Db.OrNull(Dec(r.Tax)));
            cmd.Parameters.AddWithValue("$total", Db.OrNull(Dec(r.Total)));
            cmd.Parameters.AddWithValue("$path", r.FilePath);
            cmd.Parameters.AddWithValue("$conf", Db.OrNull(r.ImageConfidence));
            cmd.Parameters.AddWithValue("$op", r.OperationId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        var receiptId = (int)Db.LastRowId(conn, tx);

        using (var cmd = Db.Command(conn, tx,
            """
            INSERT INTO receipt_raw_json (receipt_id, operation_id, json_path, raw_json, created_at)
            VALUES ($rid, $op, $path, $json, $now)
            """))
        {
            cmd.Parameters.AddWithValue("$rid", receiptId);
            cmd.Parameters.AddWithValue("$op", r.OperationId);
            cmd.Parameters.AddWithValue("$path", Db.OrNull(r.JsonPath));
            cmd.Parameters.AddWithValue("$json", r.RawJson);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        foreach (var li in r.Lines)
        {
            using var cmd = Db.Command(conn, tx,
                """
                INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description, quantity,
                    unit_price, line_total, discount, confidence, created_at)
                VALUES ($rid, $idx, $item, $desc, $qty, $price, $linetotal, $disc, $conf, $now)
                """);
            cmd.Parameters.AddWithValue("$rid", receiptId);
            cmd.Parameters.AddWithValue("$idx", li.LineIndex);
            cmd.Parameters.AddWithValue("$item", Db.OrNull(li.ItemId));
            cmd.Parameters.AddWithValue("$desc", li.Description);
            cmd.Parameters.AddWithValue("$qty", Db.OrNull(li.Quantity));
            cmd.Parameters.AddWithValue("$price", Db.OrNull(Dec(li.UnitPrice)));
            cmd.Parameters.AddWithValue("$linetotal", Db.OrNull(Dec(li.LineTotal)));
            cmd.Parameters.AddWithValue("$disc", Db.OrNull(Dec(li.Discount)));
            cmd.Parameters.AddWithValue("$conf", Db.OrNull(li.Confidence));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();

            if (li.UnitPrice is null || li.ItemId is null) continue;
            using var pcmd = Db.Command(conn, tx,
                """
                INSERT INTO prices (item_id, store_id, receipt_id, flyer_source_id, source, date,
                    unit_price, unit, quantity, total_price, raw_name, confidence, norm_unit_price, norm_unit,
                    norm_note, created_at)
                VALUES ($item, $store, $rid, NULL, 'receipt', $date, $price, $unit, $qty, $total, $raw, $conf,
                    $nuprice, $nunit, $nnote, $now)
                """);
            pcmd.Parameters.AddWithValue("$item", li.ItemId.Value);
            pcmd.Parameters.AddWithValue("$store", r.StoreId);
            pcmd.Parameters.AddWithValue("$rid", receiptId);
            pcmd.Parameters.AddWithValue("$date", r.PurchaseDate);
            pcmd.Parameters.AddWithValue("$price", Dec(li.UnitPrice)!);
            pcmd.Parameters.AddWithValue("$unit", li.Unit);
            pcmd.Parameters.AddWithValue("$qty", Db.OrNull(li.Quantity));
            pcmd.Parameters.AddWithValue("$total", Db.OrNull(Dec(li.LineTotal)));
            pcmd.Parameters.AddWithValue("$raw", li.Description);
            pcmd.Parameters.AddWithValue("$conf", Db.OrNull(li.Confidence));
            pcmd.Parameters.AddWithValue("$nuprice", Db.OrNull(li.NormUnitPrice));
            pcmd.Parameters.AddWithValue("$nunit", Db.OrNull(li.NormUnit));
            pcmd.Parameters.AddWithValue("$nnote", Db.OrNull(li.NormNote));
            pcmd.Parameters.AddWithValue("$now", now);
            pcmd.ExecuteNonQuery();
        }

        if (!string.IsNullOrEmpty(r.FileHash))
            InsertFileHash(conn, tx, r.FileHash, receiptId, r.FilePath, now);
        if (!string.IsNullOrEmpty(r.Signature))
            InsertSignature(conn, tx, r.Signature, receiptId, now);

        return receiptId;
    }

    private static void InsertFileHash(SqliteConnection conn, SqliteTransaction tx, string fileHash, int receiptId,
        string? filePath, string now)
    {
        using var cmd = Db.Command(conn, tx,
            "INSERT INTO receipt_file_hashes (file_hash, receipt_id, file_path, created_at) VALUES ($h, $rid, $p, $now)");
        cmd.Parameters.AddWithValue("$h", fileHash);
        cmd.Parameters.AddWithValue("$rid", receiptId);
        cmd.Parameters.AddWithValue("$p", Db.OrNull(filePath));
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private static void InsertSignature(SqliteConnection conn, SqliteTransaction tx, string signature, int receiptId,
        string now)
    {
        using var cmd = Db.Command(conn, tx,
            "INSERT INTO receipt_signatures (signature, receipt_id, created_at) VALUES ($s, $rid, $now)");
        cmd.Parameters.AddWithValue("$s", signature);
        cmd.Parameters.AddWithValue("$rid", receiptId);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    private static decimal? Dec(double? v) => v is { } x ? (decimal)x : null;

    public static int DeleteReceiptWithBackup(SqliteConnection conn, int receiptId, SqliteTransaction? tx = null)
    {
        var snapshot = Snapshot(conn, tx, receiptId)
            ?? throw new ArgumentException($"Receipt not found: {receiptId}", nameof(receiptId));

        var json = JsonSerializer.Serialize(snapshot, ReceiptSnapshotContext.Default.ReceiptSnapshot);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBackupBytes)
        {
            // Snapshot dominated by OCR raw_json blob; drop it so the backup stays restorable.
            snapshot.RawJson = null;
            json = JsonSerializer.Serialize(snapshot, ReceiptSnapshotContext.Default.ReceiptSnapshot);
        }

        return InTransaction(conn, tx, t =>
        {
            int backupId;
            using (var ins = Db.Command(conn, t,
                "INSERT INTO deleted_receipt_backups (original_receipt_id, deleted_at, backup_json) VALUES ($rid, $at, $json)"))
            {
                ins.Parameters.AddWithValue("$rid", receiptId);
                ins.Parameters.AddWithValue("$at", Db.NowIso());
                ins.Parameters.AddWithValue("$json", json);
                ins.ExecuteNonQuery();
            }
            backupId = (int)Db.LastRowId(conn, t);

            DeleteReceiptRows(conn, t, receiptId);

            using (var trim = Db.Command(conn, t,
                "DELETE FROM deleted_receipt_backups WHERE id NOT IN (SELECT id FROM deleted_receipt_backups ORDER BY id DESC LIMIT $keep)"))
            {
                trim.Parameters.AddWithValue("$keep", MaxBackupsKept);
                trim.ExecuteNonQuery();
            }
            return backupId;
        });
    }

    public static (int NewReceiptId, IReadOnlyList<(string Kind, string Key)> Conflicts) RestoreReceiptFromBackup(
        SqliteConnection conn, int backupId, SqliteTransaction? tx = null)
    {
        string json;
        using (var cmd = Db.Command(conn, tx, "SELECT backup_json FROM deleted_receipt_backups WHERE id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", backupId);
            json = cmd.ExecuteScalar() as string
                ?? throw new ArgumentException($"Backup not found: {backupId}", nameof(backupId));
        }
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBackupBytes)
            throw new InvalidOperationException($"Backup {backupId} is too large to restore safely");

        var snapshot = JsonSerializer.Deserialize(json, ReceiptSnapshotContext.Default.ReceiptSnapshot)
            ?? throw new InvalidOperationException($"Backup {backupId} is corrupt: not a snapshot");
        if (snapshot.Receipt is null)
            throw new InvalidOperationException($"Backup {backupId} is corrupt: missing receipt");

        var conflicts = new List<(string, string)>();

        var newId = InTransaction(conn, tx, t =>
        {
            var rec = snapshot.Receipt;
            int receiptId;
            using (var ins = Db.Command(conn, t,
                """
                INSERT INTO receipts (store_id, purchase_date, subtotal_amount, tax_amount, total_amount,
                    source, file_path, image_overall_confidence, keep_image_until, azure_request_id, created_at)
                VALUES ($store, $date, $sub, $tax, $total, $source, $file, $conf, $keep, $azure, $created)
                """))
            {
                ins.Parameters.AddWithValue("$store", Db.OrNull(rec.StoreId));
                ins.Parameters.AddWithValue("$date", Db.OrNull(rec.PurchaseDate));
                ins.Parameters.AddWithValue("$sub", Db.OrNull(rec.SubtotalAmount));
                ins.Parameters.AddWithValue("$tax", Db.OrNull(rec.TaxAmount));
                ins.Parameters.AddWithValue("$total", Db.OrNull(rec.TotalAmount));
                ins.Parameters.AddWithValue("$source", Db.OrNull(rec.Source));
                ins.Parameters.AddWithValue("$file", Db.OrNull(rec.FilePath));
                ins.Parameters.AddWithValue("$conf", Db.OrNull(rec.ImageOverallConfidence));
                ins.Parameters.AddWithValue("$keep", Db.OrNull(rec.KeepImageUntil));
                ins.Parameters.AddWithValue("$azure", Db.OrNull(rec.AzureRequestId));
                ins.Parameters.AddWithValue("$created", rec.CreatedAt ?? Db.NowIso());
                ins.ExecuteNonQuery();
            }
            receiptId = (int)Db.LastRowId(conn, t);

            if (snapshot.RawJson is { } raw)
            {
                using var rj = Db.Command(conn, t,
                    "INSERT OR REPLACE INTO receipt_raw_json (receipt_id, operation_id, json_path, raw_json, created_at) VALUES ($rid, $op, $path, $raw, $created)");
                rj.Parameters.AddWithValue("$rid", receiptId);
                rj.Parameters.AddWithValue("$op", Db.OrNull(raw.OperationId));
                rj.Parameters.AddWithValue("$path", Db.OrNull(raw.JsonPath));
                rj.Parameters.AddWithValue("$raw", Db.OrNull(raw.RawJson));
                rj.Parameters.AddWithValue("$created", raw.CreatedAt ?? Db.NowIso());
                rj.ExecuteNonQuery();
            }

            foreach (var li in snapshot.LineItems)
            {
                using var cmd = Db.Command(conn, t,
                    """
                    INSERT INTO receipt_line_items (receipt_id, line_index, item_id, description, quantity,
                        unit_price, line_total, discount, confidence, created_at)
                    VALUES ($rid, $idx, $item, $desc, $qty, $price, $total, $disc, $conf, $created)
                    """);
                cmd.Parameters.AddWithValue("$rid", receiptId);
                cmd.Parameters.AddWithValue("$idx", Db.OrNull(li.LineIndex));
                cmd.Parameters.AddWithValue("$item", Db.OrNull(li.ItemId));
                cmd.Parameters.AddWithValue("$desc", Db.OrNull(li.Description));
                cmd.Parameters.AddWithValue("$qty", Db.OrNull(li.Quantity));
                cmd.Parameters.AddWithValue("$price", Db.OrNull(li.UnitPrice));
                cmd.Parameters.AddWithValue("$total", Db.OrNull(li.LineTotal));
                cmd.Parameters.AddWithValue("$disc", Db.OrNull(li.Discount));
                cmd.Parameters.AddWithValue("$conf", Db.OrNull(li.Confidence));
                cmd.Parameters.AddWithValue("$created", li.CreatedAt ?? Db.NowIso());
                cmd.ExecuteNonQuery();
            }

            foreach (var p in snapshot.Prices)
            {
                int? fsid = p.FlyerSourceId;
                if (fsid is not null && !FlyerSourceExists(conn, t, fsid.Value)) fsid = null;
                using var cmd = Db.Command(conn, t,
                    """
                    INSERT INTO prices (item_id, store_id, receipt_id, flyer_source_id, source, date,
                        unit_price, unit, quantity, total_price, raw_name, confidence, created_at)
                    VALUES ($item, $store, $rid, $fsid, $source, $date, $price, $unit, $qty, $total, $raw, $conf, $created)
                    """);
                cmd.Parameters.AddWithValue("$item", Db.OrNull(p.ItemId));
                cmd.Parameters.AddWithValue("$store", Db.OrNull(p.StoreId));
                cmd.Parameters.AddWithValue("$rid", receiptId);
                cmd.Parameters.AddWithValue("$fsid", Db.OrNull(fsid));
                cmd.Parameters.AddWithValue("$source", Db.OrNull(p.Source));
                cmd.Parameters.AddWithValue("$date", Db.OrNull(p.Date));
                cmd.Parameters.AddWithValue("$price", Db.OrNull(p.UnitPrice));
                cmd.Parameters.AddWithValue("$unit", Db.OrNull(p.Unit));
                cmd.Parameters.AddWithValue("$qty", Db.OrNull(p.Quantity));
                cmd.Parameters.AddWithValue("$total", Db.OrNull(p.TotalPrice));
                cmd.Parameters.AddWithValue("$raw", Db.OrNull(p.RawName));
                cmd.Parameters.AddWithValue("$conf", Db.OrNull(p.Confidence));
                cmd.Parameters.AddWithValue("$created", p.CreatedAt ?? Db.NowIso());
                cmd.ExecuteNonQuery();
            }

            // Dedupe keys: INSERT (not REPLACE) so we never steal another receipt's existing key.
            foreach (var fh in snapshot.FileHashes)
            {
                if (string.IsNullOrEmpty(fh.FileHash)) continue;
                try
                {
                    using var cmd = Db.Command(conn, t,
                        "INSERT INTO receipt_file_hashes (file_hash, receipt_id, file_path, created_at) VALUES ($h, $rid, $path, $created)");
                    cmd.Parameters.AddWithValue("$h", fh.FileHash);
                    cmd.Parameters.AddWithValue("$rid", receiptId);
                    cmd.Parameters.AddWithValue("$path", Db.OrNull(fh.FilePath));
                    cmd.Parameters.AddWithValue("$created", fh.CreatedAt ?? Db.NowIso());
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    conflicts.Add(("file_hash", fh.FileHash));
                }
            }

            foreach (var sig in snapshot.Signatures)
            {
                if (string.IsNullOrEmpty(sig.Signature)) continue;
                try
                {
                    using var cmd = Db.Command(conn, t,
                        "INSERT INTO receipt_signatures (signature, receipt_id, created_at) VALUES ($s, $rid, $created)");
                    cmd.Parameters.AddWithValue("$s", sig.Signature);
                    cmd.Parameters.AddWithValue("$rid", receiptId);
                    cmd.Parameters.AddWithValue("$created", sig.CreatedAt ?? Db.NowIso());
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    conflicts.Add(("signature", sig.Signature));
                }
            }

            // Trip close-out relink (V3): re-insert the ledger row against the NEW receipt id — without
            // this, delete-with-backup silently lost the trip. trip_date/created_at survive verbatim.
            if (snapshot.Trip is { } trip)
                TripsRepo.Insert(conn, receiptId, trip.StoreId, trip.TripDate ?? rec.PurchaseDate ?? "",
                    trip.PlannedEstimate, trip.PlannedEstimateBasis, trip.PlannedUnknownCount,
                    trip.ActualTotal, trip.RealizedSaving, trip.SavingBasis,
                    trip.MappedLineCount, trip.QualifyingLineCount, trip.MatchedPlannedCount,
                    trip.UnplannedCount, t);

            // The backup is CONSUMED by a successful restore (same transaction): restoring the same
            // backup twice would insert a second identical receipt whose dedupe keys silently conflict.
            using (var consume = Db.Command(conn, t, "DELETE FROM deleted_receipt_backups WHERE id = $id"))
            {
                consume.Parameters.AddWithValue("$id", backupId);
                consume.ExecuteNonQuery();
            }

            return receiptId;
        });

        return (newId, conflicts);
    }

    public static IReadOnlyList<DeletedBackup> ListDeletedBackups(SqliteConnection conn, int limit = 25,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "SELECT id, original_receipt_id, deleted_at FROM deleted_receipt_backups ORDER BY id DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var rows = new List<DeletedBackup>();
        while (r.Read())
            rows.Add(new DeletedBackup(r.GetInt32(0), r.GetIntOrNull(1), r.GetStringOrNull(2)));
        return rows;
    }

    // ---- internals ----

    // prices, receipt_line_items, receipt_raw_json, receipt_file_hashes and receipt_signatures all declare
    // receipt_id ... ON DELETE CASCADE, and SqliteConnectionFactory sets foreign_keys=ON on every connection,
    // so the children go with the parent. Same house rule FlyersRepo states: no redundant child deletes.
    private static void DeleteReceiptRows(SqliteConnection conn, SqliteTransaction tx, int receiptId)
    {
        using var del = Db.Command(conn, tx, "DELETE FROM receipts WHERE id = $id");
        del.Parameters.AddWithValue("$id", receiptId);
        del.ExecuteNonQuery();
    }

    private static bool FlyerSourceExists(SqliteConnection conn, SqliteTransaction tx, int id)
    {
        using var cmd = Db.Command(conn, tx, "SELECT 1 FROM flyer_sources WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static ReceiptSnapshot? Snapshot(SqliteConnection conn, SqliteTransaction? tx, int receiptId)
    {
        var snap = new ReceiptSnapshot();
        using (var cmd = Db.Command(conn, tx,
            """
            SELECT id, store_id, purchase_date, subtotal_amount, tax_amount, total_amount, source, file_path,
                   image_overall_confidence, keep_image_until, azure_request_id, created_at
            FROM receipts WHERE id = $id
            """))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            snap.Receipt = new SnapReceipt
            {
                Id = r.GetInt32(0), StoreId = r.GetIntOrNull(1), PurchaseDate = r.GetStringOrNull(2),
                SubtotalAmount = r.GetMoneyOrNull(3), TaxAmount = r.GetMoneyOrNull(4), TotalAmount = r.GetMoneyOrNull(5),
                Source = r.GetStringOrNull(6), FilePath = r.GetStringOrNull(7), ImageOverallConfidence = r.GetIntOrNull(8),
                KeepImageUntil = r.GetStringOrNull(9), AzureRequestId = r.GetStringOrNull(10), CreatedAt = r.GetStringOrNull(11),
            };
        }

        using (var cmd = Db.Command(conn, tx,
            "SELECT receipt_id, operation_id, json_path, raw_json, created_at FROM receipt_raw_json WHERE receipt_id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                snap.RawJson = new SnapRawJson
                {
                    ReceiptId = r.GetIntOrNull(0), OperationId = r.GetStringOrNull(1),
                    JsonPath = r.GetStringOrNull(2), RawJson = r.GetStringOrNull(3), CreatedAt = r.GetStringOrNull(4),
                };
        }

        using (var cmd = Db.Command(conn, tx,
            """
            SELECT line_index, item_id, description, quantity, unit_price, line_total, discount, confidence, created_at
            FROM receipt_line_items WHERE receipt_id = $id ORDER BY line_index ASC
            """))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                snap.LineItems.Add(new SnapLineItem
                {
                    LineIndex = r.GetIntOrNull(0), ItemId = r.GetIntOrNull(1), Description = r.GetStringOrNull(2),
                    Quantity = r.GetDoubleOrNull(3), UnitPrice = r.GetMoneyOrNull(4), LineTotal = r.GetMoneyOrNull(5),
                    Discount = r.GetMoneyOrNull(6), Confidence = r.GetIntOrNull(7), CreatedAt = r.GetStringOrNull(8),
                });
        }

        using (var cmd = Db.Command(conn, tx,
            """
            SELECT item_id, store_id, flyer_source_id, source, date, unit_price, unit, quantity, total_price,
                   raw_name, confidence, created_at
            FROM prices WHERE receipt_id = $id
            """))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                snap.Prices.Add(new SnapPrice
                {
                    ItemId = r.GetIntOrNull(0), StoreId = r.GetIntOrNull(1), FlyerSourceId = r.GetIntOrNull(2),
                    Source = r.GetStringOrNull(3), Date = r.GetStringOrNull(4), UnitPrice = r.GetMoneyOrNull(5),
                    Unit = r.GetStringOrNull(6), Quantity = r.GetDoubleOrNull(7), TotalPrice = r.GetMoneyOrNull(8),
                    RawName = r.GetStringOrNull(9), Confidence = r.GetIntOrNull(10), CreatedAt = r.GetStringOrNull(11),
                });
        }

        using (var cmd = Db.Command(conn, tx,
            "SELECT file_hash, file_path, created_at FROM receipt_file_hashes WHERE receipt_id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                snap.FileHashes.Add(new SnapFileHash
                {
                    FileHash = r.GetStringOrNull(0), FilePath = r.GetStringOrNull(1), CreatedAt = r.GetStringOrNull(2),
                });
        }

        using (var cmd = Db.Command(conn, tx,
            "SELECT signature, created_at FROM receipt_signatures WHERE receipt_id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                snap.Signatures.Add(new SnapSignature { Signature = r.GetStringOrNull(0), CreatedAt = r.GetStringOrNull(1) });
        }

        // Trip close-out row (V3) — CASCADE would silently drop it on delete; capture it so restore relinks.
        using (var cmd = Db.Command(conn, tx,
            """
            SELECT store_id, trip_date, planned_estimate, planned_estimate_basis, planned_unknown_count,
                   actual_total, realized_saving, saving_basis, mapped_line_count, qualifying_line_count,
                   matched_planned_count, unplanned_count, created_at
            FROM trips WHERE receipt_id = $id
            """))
        {
            cmd.Parameters.AddWithValue("$id", receiptId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                snap.Trip = new SnapTrip
                {
                    StoreId = r.GetIntOrNull(0), TripDate = r.GetStringOrNull(1),
                    PlannedEstimate = r.GetMoneyOrNull(2), PlannedEstimateBasis = r.GetStringOrNull(3),
                    PlannedUnknownCount = r.GetIntOrNull(4), ActualTotal = r.GetMoneyOrNull(5),
                    RealizedSaving = r.GetMoneyOrNull(6), SavingBasis = r.GetStringOrNull(7),
                    MappedLineCount = r.GetInt32(8), QualifyingLineCount = r.GetInt32(9),
                    MatchedPlannedCount = r.GetInt32(10), UnplannedCount = r.GetInt32(11),
                    CreatedAt = r.GetStringOrNull(12),
                };
        }

        return snap;
    }

    // Runs body inside the caller's transaction if supplied, otherwise a local one (commit/rollback owned here).
    private static T InTransaction<T>(SqliteConnection conn, SqliteTransaction? tx, Func<SqliteTransaction, T> body)
    {
        if (tx is not null) return body(tx);
        using var local = conn.BeginTransaction();
        var result = body(local);
        local.Commit();
        return result;
    }
}
