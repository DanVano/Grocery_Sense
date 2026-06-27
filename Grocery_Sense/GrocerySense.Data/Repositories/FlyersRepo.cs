using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

public sealed class FlyersRepo
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

    // stores.name has no UNIQUE constraint, so this is select-then-insert (single-user assumption).
    public int UpsertStore(SqliteConnection conn, string name, SqliteTransaction? tx = null)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Store name is required", nameof(name));

        using (var sel = Db.Command(conn, tx, "SELECT id FROM stores WHERE name = $name"))
        {
            sel.Parameters.AddWithValue("$name", name);
            if (sel.ExecuteScalar() is { } existing) return Convert.ToInt32(existing);
        }
        using var ins = Db.Command(conn, tx, "INSERT INTO stores (name, created_at) VALUES ($name, $now)");
        ins.Parameters.AddWithValue("$name", name);
        ins.Parameters.AddWithValue("$now", Db.NowIso());
        ins.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public IReadOnlyList<StoreRow> ListStores(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT id, name FROM stores ORDER BY name ASC");
        using var r = cmd.ExecuteReader();
        var rows = new List<StoreRow>();
        while (r.Read()) rows.Add(new StoreRow(r.GetInt32(0), r.GetString(1)));
        return rows;
    }

    public int CreateFlyerBatch(SqliteConnection conn, int storeId, string? validFrom, string? validTo,
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

    public void SetBatchStatus(SqliteConnection conn, int flyerId, string status, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE flyer_batches SET status = $s WHERE id = $id");
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$id", flyerId);
        cmd.ExecuteNonQuery();
    }

    public int AddAsset(SqliteConnection conn, int flyerId, string assetType, string path, string? sha256 = null,
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

    public int AddRawJson(SqliteConnection conn, int flyerId, string rawJson, string? sha256 = null,
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

    public int AddDeals(SqliteConnection conn, IReadOnlyList<FlyerDeal> deals, SqliteTransaction? tx = null)
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

    public IReadOnlyList<FlyerDeal> ListActiveDeals(SqliteConnection conn, int? storeId = null,
        IReadOnlyList<int>? storeIds = null, string? onDate = null, int limit = 5000, SqliteTransaction? tx = null)
    {
        if (storeIds is { Count: 0 }) return Array.Empty<FlyerDeal>();

        onDate ??= DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sql =
            $"SELECT {PrefixCols("d")} FROM flyer_deals d JOIN flyer_batches b ON b.id = d.flyer_id " +
            "WHERE b.status = 'active' " +
            "AND (b.valid_from IS NULL OR b.valid_from <= $onDate) " +
            "AND (b.valid_to IS NULL OR b.valid_to >= $onDate)";

        var inParams = new List<string>();
        if (storeId is not null) sql += " AND d.store_id = $store";
        else if (storeIds is { Count: > 0 })
        {
            for (var i = 0; i < storeIds.Count; i++) inParams.Add($"$s{i}");
            sql += $" AND d.store_id IN ({string.Join(",", inParams)})";
        }
        sql += " ORDER BY d.id DESC LIMIT $limit";

        using var cmd = Db.Command(conn, tx, sql);
        cmd.Parameters.AddWithValue("$onDate", onDate);
        if (storeId is not null) cmd.Parameters.AddWithValue("$store", storeId.Value);
        else if (storeIds is { Count: > 0 })
            for (var i = 0; i < storeIds.Count; i++) cmd.Parameters.AddWithValue(inParams[i], storeIds[i]);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var r = cmd.ExecuteReader();
        var rows = new List<FlyerDeal>();
        while (r.Read()) rows.Add(MapDeal(r));
        return rows;
    }

    public IReadOnlyList<FlyerDeal> ListDealsForFlyer(SqliteConnection conn, int flyerId, int limit = 5000,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            $"SELECT {DealCols} FROM flyer_deals WHERE flyer_id = $f ORDER BY id ASC LIMIT $limit");
        cmd.Parameters.AddWithValue("$f", flyerId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        var rows = new List<FlyerDeal>();
        while (r.Read()) rows.Add(MapDeal(r));
        return rows;
    }

    private static string PrefixCols(string alias) =>
        string.Join(", ", DealCols.Split(", ").Select(c => $"{alias}.{c}"));
}
