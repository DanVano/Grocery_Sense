using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// The trip close-out ledger (migration 11). Money is TEXT-decimal; sums happen in C#, never SQL.
public static class TripsRepo
{
    public static int Insert(SqliteConnection conn, int receiptId, int? storeId, string tripDate,
        decimal? plannedEstimate, string? plannedEstimateBasis, int? plannedUnknownCount,
        decimal? actualTotal, decimal? realizedSaving, string? savingBasis,
        int mappedLineCount, int qualifyingLineCount, int matchedPlannedCount, int unplannedCount,
        SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            """
            INSERT INTO trips (receipt_id, store_id, trip_date, planned_estimate, planned_estimate_basis,
                planned_unknown_count, actual_total, realized_saving, saving_basis,
                mapped_line_count, qualifying_line_count, matched_planned_count, unplanned_count)
            VALUES ($rid, $store, $date, $plan, $planBasis, $planUnknown, $total, $saving, $basis,
                $mapped, $qual, $matched, $unplanned)
            """);
        cmd.Parameters.AddWithValue("$rid", receiptId);
        cmd.Parameters.AddWithValue("$store", Db.OrNull(storeId));
        cmd.Parameters.AddWithValue("$date", tripDate);
        cmd.Parameters.AddWithValue("$plan", Db.OrNull(plannedEstimate));
        cmd.Parameters.AddWithValue("$planBasis", Db.OrNull(plannedEstimateBasis));
        cmd.Parameters.AddWithValue("$planUnknown", Db.OrNull(plannedUnknownCount));
        cmd.Parameters.AddWithValue("$total", Db.OrNull(actualTotal));
        cmd.Parameters.AddWithValue("$saving", Db.OrNull(realizedSaving));
        cmd.Parameters.AddWithValue("$basis", Db.OrNull(savingBasis));
        cmd.Parameters.AddWithValue("$mapped", mappedLineCount);
        cmd.Parameters.AddWithValue("$qual", qualifyingLineCount);
        cmd.Parameters.AddWithValue("$matched", matchedPlannedCount);
        cmd.Parameters.AddWithValue("$unplanned", unplannedCount);
        cmd.ExecuteNonQuery();
        return (int)Db.LastRowId(conn, tx);
    }

    public static bool HasTripForReceipt(SqliteConnection conn, int receiptId, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx, "SELECT 1 FROM trips WHERE receipt_id = $rid LIMIT 1");
        cmd.Parameters.AddWithValue("$rid", receiptId);
        return cmd.ExecuteScalar() is not null;
    }

    // Monthly realized savings, half-open ISO month range (same convention as ReceiptsRepo month readers).
    // NULL savings are counted but never summed — a month of all-null trips reports (null, 0, N), which the
    // UI renders as "unavailable", not $0.
    public static (decimal? Total, int TripsWithSavings, int TripCount) GetMonthRealizedSavings(
        SqliteConnection conn, string yearMonth, SqliteTransaction? tx = null)
    {
        using var cmd = Db.Command(conn, tx,
            "SELECT realized_saving FROM trips WHERE trip_date >= $from AND trip_date < $to");
        var (from, to) = MonthRange(yearMonth);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        decimal sum = 0;
        int withSavings = 0, count = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            count++;
            if (r.IsDBNull(0)) continue;
            sum += r.GetDecimal(0);
            withSavings++;
        }
        return (withSavings > 0 ? sum : null, withSavings, count);
    }

    private static (string From, string To) MonthRange(string yearMonth)
    {
        var parts = yearMonth.Split('-');
        var year = int.Parse(parts[0]);
        var month = int.Parse(parts[1]);
        var from = new DateOnly(year, month, 1);
        return (from.ToString("yyyy-MM-dd"), from.AddMonths(1).ToString("yyyy-MM-dd"));
    }
}
