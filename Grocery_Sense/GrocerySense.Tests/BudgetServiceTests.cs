using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GrocerySense.Tests;

public sealed class BudgetServiceTests : IDisposable
{
    private readonly string _cfgDir = Path.Combine(Path.GetTempPath(), $"gs_budget_{Guid.NewGuid():N}");

    public BudgetServiceTests() => Directory.CreateDirectory(_cfgDir);
    public void Dispose() { try { Directory.Delete(_cfgDir, recursive: true); } catch { /* temp */ } }

    private static string ThisMonthDate => DateTime.UtcNow.ToString("yyyy-MM") + "-15";

    private static void AddReceipt(SqliteConnection conn, int storeId, decimal total)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO receipts (store_id, purchase_date, total_amount, source) VALUES ($s, $d, $t, 'receipt')";
        cmd.Parameters.AddWithValue("$s", storeId);
        cmd.Parameters.AddWithValue("$d", ThisMonthDate);
        cmd.Parameters.AddWithValue("$t", total);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Status_is_unset_without_a_budget_but_still_reports_spend()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceipt(db.Conn, store, 50m);
        AddReceipt(db.Conn, store, 30m);

        var svc = new BudgetService(new ConfigStore(_cfgDir), db.Factory);
        var status = svc.GetBudgetStatus();

        Assert.Equal("unset", status.Status);
        Assert.Null(status.Budget);
        Assert.Equal(80m, status.Spent);
        Assert.Equal(2, status.ReceiptCount);
    }

    [Fact]
    public void Status_ok_warning_over_track_pct_used()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceipt(db.Conn, store, 80m);
        var svc = new BudgetService(new ConfigStore(_cfgDir), db.Factory);

        svc.SaveMonthlyBudget(100); // 80% used
        var ok = svc.GetBudgetStatus();
        Assert.Equal("ok", ok.Status);
        Assert.Equal(20m, ok.Remaining);
        Assert.Equal(0.80, ok.PctUsed!.Value, 6);
        Assert.False(ok.OverBudget);

        svc.SaveMonthlyBudget(90); // ~88% used
        Assert.Equal("warning", svc.GetBudgetStatus().Status);

        svc.SaveMonthlyBudget(50); // over
        var over = svc.GetBudgetStatus();
        Assert.Equal("over", over.Status);
        Assert.Equal(-30m, over.Remaining);
        Assert.True(over.OverBudget);
    }

    // Projected month-end spend = current pace held to the end of the month.
    private static decimal ExpectedProjection(decimal spent)
    {
        var now = DateTime.UtcNow;
        return decimal.Round(spent / now.Day * DateTime.DaysInMonth(now.Year, now.Month), 2);
    }

    [Fact]
    public void Projection_is_reported_even_without_a_budget()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceipt(db.Conn, store, 80m);

        var status = new BudgetService(new ConfigStore(_cfgDir), db.Factory).GetBudgetStatus();

        Assert.Equal("unset", status.ProjectedStatus);
        Assert.Equal(ExpectedProjection(80m), status.ProjectedSpend);
    }

    [Fact]
    public void Projection_grades_over_or_ok_against_the_budget()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        AddReceipt(db.Conn, store, 80m);
        var svc = new BudgetService(new ConfigStore(_cfgDir), db.Factory);
        var projected = ExpectedProjection(80m);

        svc.SaveMonthlyBudget((double)(projected - 10m)); // pace overshoots the budget
        Assert.Equal("over", svc.GetBudgetStatus().ProjectedStatus);

        svc.SaveMonthlyBudget((double)(projected * 2m)); // pace well under the budget
        Assert.Equal("ok", svc.GetBudgetStatus().ProjectedStatus);
    }

    [Fact]
    public void SaveMonthlyBudget_null_or_nonpositive_clears()
    {
        using var db = new TempDb();
        var svc = new BudgetService(new ConfigStore(_cfgDir), db.Factory);
        svc.SaveMonthlyBudget(100);
        svc.SaveMonthlyBudget(0); // clears
        Assert.Equal("unset", svc.GetBudgetStatus().Status);
    }
}
