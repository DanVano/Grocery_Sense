using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

public static class FlyersRepo
{
    private const string DealCols =
        "id, flyer_id, asset_id, store_id, page_index, title, description, price_text, deal_qty, deal_total, " +
        "unit_price, unit, norm_unit_price, norm_unit, norm_note, item_id, mapping_confidence, confidence, created_at";

    private static FlyerDeal MapDeal(SqliteDataReader r) => new(
        Id: r.GetInt32(0), FlyerId: r.GetInt32(1), AssetId: r.GetIntOrNull(2), StoreId: r.GetInt32(3),
        PageIndex: r.GetIntOrNull(4), Title: r.GetStringOrNull(5), Description: r.GetStringOrNull(6),
        PriceText: r.GetStringOrNull(7), DealQty: r.GetDoubleOrNull(8), DealTotal: r.GetMoneyOrNull(9),
        UnitPrice: r.GetMoneyOrNull(10), Unit: r.GetStringOrNull(11), NormUnitPrice: r.GetMoneyOrNull(12),
        NormUnit: r.GetStringOrNull(13), NormNote: r.GetStringOrNull(14), ItemId: r.GetIntOrNull(15),
        MappingConfidence: r.GetDoubleOrNull(16), Confidence: r.GetDoubleOrNull(17), CreatedAt: r.GetStringOrNull(18));

    public static int CreateFlyerBatch(SqliteConnection conn, int storeId, string? validFrom, string? validTo,
        string? sourceType = null, string? sourceRef = null, string? note = null, string status = "active",
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            INSERT INTO flyer_batches (store_id, valid_from, valid_to, source_type, source_ref, note, status, imported_at)
            VALUES ($store, $from, $to, $stype, $sref, $note, $status, $now)
            """);
        cmd.Parameters.AddWithValue("$store", storeId);
        cmd.Parameters.AddWithValue("$from", Db.OrNull(validFrom));
        cmd.Parameters.AddWithValue("$to", Db.OrNull(validTo));
        cmd.Parameters.AddWithValue("$stype", Db.OrNull(sourceType));
        cmd.Parameters.AddWithValue("$sref", Db.OrNull(sourceRef));
        cmd.Parameters.AddWithValue("$note", Db.OrNull(note));
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", Db.NowIso());
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    // P1-4 retention: delete a store's batches of ONE source type (e.g. flipp_api auto-sync batches),
    // leaving manual batches alone. Deals/assets/raw_json cascade via their FKs (foreign_keys=ON per
    // connection) — do not add redundant child deletes. Runs in the caller's transaction.
    public static int DeleteBatchesForStore(SqliteConnection conn, int storeId, string sourceType, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "DELETE FROM flyer_batches WHERE store_id = $store AND source_type = $stype");
        cmd.Parameters.AddWithValue("$store", storeId);
        cmd.Parameters.AddWithValue("$stype", sourceType);
        return cmd.ExecuteNonQuery();
    }

    public static int AddAsset(SqliteConnection conn, int flyerId, string assetType, string path, string? sha256 = null,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "INSERT INTO flyer_assets (flyer_id, asset_type, path, sha256, created_at) VALUES ($f, $t, $p, $h, $now)");
        cmd.Parameters.AddWithValue("$f", flyerId);
        cmd.Parameters.AddWithValue("$t", (assetType ?? "").Trim());
        cmd.Parameters.AddWithValue("$p", (path ?? "").Trim());
        cmd.Parameters.AddWithValue("$h", Db.OrNull(sha256));
        cmd.Parameters.AddWithValue("$now", Db.NowIso());
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static int AddRawJson(SqliteConnection conn, int flyerId, string rawJson, string? sha256 = null,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "INSERT INTO flyer_raw_json (flyer_id, sha256, json, created_at) VALUES ($f, $h, $j, $now)");
        cmd.Parameters.AddWithValue("$f", flyerId);
        cmd.Parameters.AddWithValue("$h", Db.OrNull(sha256));
        cmd.Parameters.AddWithValue("$j", rawJson);
        cmd.Parameters.AddWithValue("$now", Db.NowIso());
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static int AddDeals(SqliteConnection conn, IReadOnlyList<FlyerDeal> deals, SqliteTransaction? tx = null)
    {
        var now = Db.NowIso();
        foreach (var d in deals)
        {
            using var cmd = Db.Command(conn, tx,
                """
                INSERT INTO flyer_deals (flyer_id, asset_id, store_id, page_index, title, description, price_text,
                    deal_qty, deal_total, unit_price, unit, norm_unit_price, norm_unit, norm_note,
                    item_id, mapping_confidence, confidence, created_at)
                VALUES ($flyer, $asset, $store, $page, $title, $desc, $ptext, $qty, $total, $uprice, $unit,
                    $nuprice, $nunit, $nnote, $item, $mconf, $conf, $now)
                """);
            cmd.Parameters.AddWithValue("$flyer", d.FlyerId);
            cmd.Parameters.AddWithValue("$asset", Db.OrNull(d.AssetId));
            cmd.Parameters.AddWithValue("$store", d.StoreId);
            cmd.Parameters.AddWithValue("$page", Db.OrNull(d.PageIndex));
            cmd.Parameters.AddWithValue("$title", Db.OrNull(d.Title));
            cmd.Parameters.AddWithValue("$desc", Db.OrNull(d.Description));
            cmd.Parameters.AddWithValue("$ptext", Db.OrNull(d.PriceText));
            cmd.Parameters.AddWithValue("$qty", Db.OrNull(d.DealQty));
            cmd.Parameters.AddWithValue("$total", Db.OrNull(d.DealTotal));
            cmd.Parameters.AddWithValue("$uprice", Db.OrNull(d.UnitPrice));
            cmd.Parameters.AddWithValue("$unit", Db.OrNull(d.Unit));
            cmd.Parameters.AddWithValue("$nuprice", Db.OrNull(d.NormUnitPrice));
            cmd.Parameters.AddWithValue("$nunit", Db.OrNull(d.NormUnit));
            cmd.Parameters.AddWithValue("$nnote", Db.OrNull(d.NormNote));
            cmd.Parameters.AddWithValue("$item", Db.OrNull(d.ItemId));
            cmd.Parameters.AddWithValue("$mconf", Db.OrNull(d.MappingConfidence));
            cmd.Parameters.AddWithValue("$conf", Db.OrNull(d.Confidence));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        return deals.Count;
    }

    // COUNT twin of ListActiveDeals (same predicate) for dashboards — the Home screen must not
    // materialize 5 000 deal rows to show a number.
    public static int CountActiveDeals(SqliteConnection conn, string? onDate = null, SqliteTransaction? tx = null)
    {
        onDate ??= DateTime.UtcNow.ToString("yyyy-MM-dd");
        using var cmd = Db.Command(conn, tx,
            "SELECT COUNT(*) FROM flyer_deals d JOIN flyer_batches b ON b.id = d.flyer_id " +
            "WHERE b.status = 'active' " +
            "AND (b.valid_from IS NULL OR b.valid_from <= $onDate) " +
            "AND (b.valid_to IS NULL OR b.valid_to >= $onDate)");
        cmd.Parameters.AddWithValue("$onDate", onDate);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static IReadOnlyList<FlyerDeal> ListActiveDeals(SqliteConnection conn, int? storeId = null,
        string? onDate = null, int limit = 5000, SqliteTransaction? tx = null)
    {
        onDate ??= DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sql =
            $"SELECT {PrefixCols("d")} FROM flyer_deals d JOIN flyer_batches b ON b.id = d.flyer_id " +
            "WHERE b.status = 'active' " +
            "AND (b.valid_from IS NULL OR b.valid_from <= $onDate) " +
            "AND (b.valid_to IS NULL OR b.valid_to >= $onDate)";
        if (storeId is not null) sql += " AND d.store_id = $store";
        sql += " ORDER BY d.id DESC LIMIT $limit";

        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$onDate", onDate);
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var r = cmd.ExecuteReader();
        var rows = new List<FlyerDeal>();
        while (r.Read()) rows.Add(MapDeal(r));
        return rows;
    }

    private static string PrefixCols(string alias) =>
        string.Join(", ", DealCols.Split(", ").Select(c => $"{alias}.{c}"));
}
