using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// The singleton confirmed Smart Week plan (migration 10, grill Q11). One row, INSERT OR REPLACE — the
// caller (SmartWeekService) writes it in the SAME transaction as the shopping-list upsert so plan and
// list can never diverge on a crash. The JSON payload is opaque here; Core owns its shape.
public static class SmartWeekPlanRepo
{
    public static void Save(SqliteConnection conn, string weekStart, string confirmedAt, string snapshotJson,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "INSERT OR REPLACE INTO selected_smart_week_plan (id, week_start, confirmed_at, snapshot_json) " +
            "VALUES (1, $week, $at, $json)");
        cmd.Parameters.AddWithValue("$week", weekStart);
        cmd.Parameters.AddWithValue("$at", confirmedAt);
        cmd.Parameters.AddWithValue("$json", snapshotJson);
        cmd.ExecuteNonQuery();
    }

    public static (string WeekStart, string ConfirmedAt, string SnapshotJson)? Get(SqliteConnection conn,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "SELECT week_start, confirmed_at, snapshot_json FROM selected_smart_week_plan WHERE id = 1");
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }

    public static void Clear(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "DELETE FROM selected_smart_week_plan WHERE id = 1");
        cmd.ExecuteNonQuery();
    }
}
