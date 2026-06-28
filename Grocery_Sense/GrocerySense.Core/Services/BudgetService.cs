using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;

namespace GrocerySense.Core;

// Port of reference-python/.../services/budget_service.py — this month's spend vs budget + spend trend.
// Gas-cost get/save are dropped (the optimizer redesign cut distance/gas; the field is never surfaced).
// Dict return replaced with the typed BudgetStatus record.
public sealed class BudgetService
{
    private readonly ConfigStore _config;
    private readonly SqliteConnectionFactory _factory;

    public BudgetService(ConfigStore config, SqliteConnectionFactory factory)
    {
        _config = config;
        _factory = factory;
    }

    public BudgetStatus GetBudgetStatus()
    {
        var month = CurrentYearMonth();
        MonthSpend spend;
        using (var conn = _factory.Open()) spend = ReceiptsRepo.GetMonthSpend(conn, month);

        // ConfigStore already normalizes MonthlyBudget to null-or-positive.
        var budget = _config.Load().MonthlyBudget is { } b ? (decimal)b : (decimal?)null;
        if (budget is null)
            return new BudgetStatus(month, spend.Total, spend.ReceiptCount, null, null, null, null, "unset");

        var remaining = budget.Value - spend.Total;
        var pctUsed = budget.Value > 0 ? (double)(spend.Total / budget.Value) : 0.0;
        var status = pctUsed > 1.0 ? "over" : pctUsed >= 0.85 ? "warning" : "ok";
        return new BudgetStatus(month, spend.Total, spend.ReceiptCount, budget, remaining, pctUsed,
            remaining < 0, status);
    }

    // Monthly spend for the last N months (oldest first).
    public IReadOnlyList<SpendTrendPoint> GetTrend(int months = 12)
    {
        using var conn = _factory.Open();
        return ReceiptsRepo.GetSpendTrend(conn, months);
    }

    // Persist a new monthly budget; null/non-positive clears it.
    public void SaveMonthlyBudget(double? amount)
    {
        var cfg = _config.Load();
        _config.Save(cfg with { MonthlyBudget = amount is { } a && a > 0 ? a : null });
    }

    private static string CurrentYearMonth() => DateTime.UtcNow.ToString("yyyy-MM");
}
