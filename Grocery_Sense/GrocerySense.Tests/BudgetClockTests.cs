using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Tests;

// V3 local-date convention (grill Q15): the Budget "current month" derives from the LOCAL clock. These
// fixed-clock tests pin the month-rollover boundary in zones on BOTH sides of UTC — the exact hours where
// the old DateTime.UtcNow key put the Budget page on the wrong month.
public sealed class BudgetClockTests : TempDirTestBase
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _utc;
        private readonly TimeZoneInfo _zone;
        public FixedClock(DateTimeOffset utc, int offsetHours)
        {
            _utc = utc;
            _zone = TimeZoneInfo.CreateCustomTimeZone($"test-{offsetHours}", TimeSpan.FromHours(offsetHours),
                $"Test UTC{offsetHours:+0;-0}", $"Test UTC{offsetHours:+0;-0}");
        }
        public override DateTimeOffset GetUtcNow() => _utc;
        public override TimeZoneInfo LocalTimeZone => _zone;
    }

    private static void AddReceiptOn(SqliteConnection conn, int storeId, decimal total, string date)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, total_amount, source) VALUES ($s, $d, $t, 'receipt')";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", date);
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Behind_utc_evening_stays_in_local_month()
    {
        // 2026-08-01 03:00 UTC = 2026-07-31 19:00 in UTC-8 (Vancouver evening) -> month must be July.
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceiptOn(db.Conn, store, 42m, "2026-07-31");

        var svc = new BudgetService(new ConfigStore(_dir), db.Factory,
            new FixedClock(new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero), -8));
        var status = svc.GetBudgetStatus();

        Assert.Equal("2026-07", status.Month);
        Assert.Equal(42m, status.Spent); // the evening receipt counts toward the month the user is living in
    }

    [Fact]
    public void Ahead_of_utc_morning_reaches_new_local_month()
    {
        // 2026-07-31 18:00 UTC = 2026-08-01 04:00 in UTC+10 -> month must already be August.
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceiptOn(db.Conn, store, 10m, "2026-07-15");

        var svc = new BudgetService(new ConfigStore(_dir), db.Factory,
            new FixedClock(new DateTimeOffset(2026, 7, 31, 18, 0, 0, TimeSpan.Zero), 10));
        var status = svc.GetBudgetStatus();

        Assert.Equal("2026-08", status.Month);
        Assert.Equal(0m, status.Spent); // July's receipt is last month now
    }

    [Fact]
    public void YearMonth_is_a_plain_local_key()
    {
        Assert.Equal("2026-07", BudgetService.YearMonth(new DateTime(2026, 7, 31, 23, 59, 0)));
        Assert.Equal("2026-08", BudgetService.YearMonth(new DateTime(2026, 8, 1, 0, 0, 1)));
    }
}
