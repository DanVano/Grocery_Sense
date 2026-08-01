using System.Text.Json;
using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/.../data/repositories/member_requests_repo.py — the parent review queue for
// family meal-picks. member_id references a config-JSON member (no DB FK, matching Python). item_row_ids is
// a JSON array of the shopping_list ids the pick created.
public static class MemberRequestsRepo
{
    private const string Cols = "id, member_id, member_name, kind, label, item_row_ids, created_at, reviewed";

    public static int AddRequest(SqliteConnection conn, int? memberId, string memberName, string kind,
        string label, IReadOnlyList<int> itemRowIds, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, """
            INSERT INTO member_requests (member_id, member_name, kind, label, item_row_ids)
            VALUES ($member, $name, $kind, $label, $rows)
            """);
        cmd.Parameters.AddWithValue("$member", Db.OrNull(memberId));
        cmd.Parameters.AddWithValue("$name", (memberName ?? "").Trim());
        cmd.Parameters.AddWithValue("$kind", (kind ?? "").Trim());
        cmd.Parameters.AddWithValue("$label", (label ?? "").Trim());
        cmd.Parameters.AddWithValue("$rows", EncodeRowIds(itemRowIds));
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static MemberRequestRow? GetRequest(SqliteConnection conn, int requestId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, $"SELECT {Cols} FROM member_requests WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", requestId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public static IReadOnlyList<MemberRequestRow> ListUnreviewed(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            $"SELECT {Cols} FROM member_requests WHERE reviewed = 0 ORDER BY id DESC");
        return ReadAll(cmd);
    }

    public static int CountUnreviewed(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT COUNT(*) FROM member_requests WHERE reviewed = 0");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static void MarkReviewed(SqliteConnection conn, int requestId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "UPDATE member_requests SET reviewed = 1 WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", requestId);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<MemberRequestRow> ReadAll(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<MemberRequestRow>();
        while (r.Read()) rows.Add(Map(r));
        return rows;
    }

    private static MemberRequestRow Map(SqliteDataReader r) => new(
        Id: r.GetInt32(0),
        MemberId: r.GetIntOrNull(1),
        MemberName: r.GetStringOrNull(2) ?? "",
        Kind: r.GetStringOrNull(3) ?? "",
        Label: r.GetStringOrNull(4) ?? "",
        ItemRowIds: DecodeRowIds(r.GetStringOrNull(5)),
        CreatedAt: r.GetStringOrNull(6) ?? "",
        Reviewed: !r.IsDBNull(7) && r.GetInt32(7) != 0);

    private static string EncodeRowIds(IReadOnlyList<int> ids) => "[" + string.Join(",", ids) + "]";

    // Tolerate NULL/empty/legacy junk by returning [] rather than crashing the review screen.
    private static List<int> DecodeRowIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            var ids = new List<int>();
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)) ids.Add(v);
            return ids;
        }
        catch (JsonException) { return new(); }
    }
}
