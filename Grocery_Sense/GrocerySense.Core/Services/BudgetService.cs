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

        // Month-end projection: current pace held for the rest of the month.
        // ponytail: naive linear extrapolation; add weekday/pay-cycle weighting only if it demonstrably misleads.
        var now = DateTime.UtcNow;
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var projected = decimal.Round(spend.Total / now.Day * daysInMonth, 2); // now.Day is 1..31, never 0

        // ConfigStore already normalizes MonthlyBudget to null-or-positive.
        var budget = _config.Load().MonthlyBudget is { } b ? (decimal)b : (decimal?)null;
        if (budget is null)
            return new BudgetStatus(month, spend.Total, spend.ReceiptCount, null, null, null, null, "unset",
                projected, "unset");

        var remaining = budget.Value - spend.Total;
        var pctUsed = budget.Value > 0 ? (double)(spend.Total / budget.Value) : 0.0;
        var status = pctUsed > 1.0 ? "over" : pctUsed >= 0.85 ? "warning" : "ok";
        var projStatus = Grade(projected, budget.Value);
        return new BudgetStatus(month, spend.Total, spend.ReceiptCount, budget, remaining, pctUsed,
            remaining < 0, status, projected, projStatus);
    }

    // Grade a dollar amount against the budget with the same thresholds as current-spend status.
    private static string Grade(decimal amount, decimal budget)
    {
        if (budget <= 0) return "ok";
        var pct = (double)(amount / budget);
        return pct > 1.0 ? "over" : pct >= 0.85 ? "warning" : "ok";
    }

    // Monthly spend for the last N months (oldest first).
    public IReadOnlyList<SpendTrendPoint> GetTrend(int months = 12)
    {
        using var conn = _factory.Open();
        return ReceiptsRepo.GetSpendTrend(conn, months);
    }

    // Year-over-year context line for the Budget page (Stage 4 I3). Compares this month's spend to the same
    // month last year (matched by year-month key — GetTrend is sparse, skipping receipt-free months) and
    // surfaces the current-year food-inflation rate. Both months must have receipts, else EnoughHistory=false
    // and nulls — an honest empty state, never a padded number. With ~4 receipts/month the prior-year month
    // often won't exist yet; it fills in with normal use.
    public InflationContext GetInflationContext()
    {
        var now = DateTime.UtcNow;
        var currentKey = now.ToString("yyyy-MM");
        var lastYearKey = now.AddYears(-1).ToString("yyyy-MM");

        var byMonth = GetTrend(13).ToDictionary(p => p.Month, p => p.Total);
        if (!byMonth.TryGetValue(currentKey, out var current) || !byMonth.TryGetValue(lastYearKey, out var prior)
            || prior <= 0)
            return new InflationContext(null, null, EnoughHistory: false);

        var spendYoyPct = (double)((current - prior) / prior) * 100.0;

        var rates = _config.Load().FoodInflationByYear;
        double? foodPct = rates is not null && rates.TryGetValue(now.Year.ToString(), out var r) ? r : null;

        return new InflationContext(spendYoyPct, foodPct, EnoughHistory: true);
    }

    // Persist a new monthly budget; null/non-positive clears it.
    public void SaveMonthlyBudget(double? amount)
    {
        var cfg = _config.Load();
        _config.Save(cfg with { MonthlyBudget = amount is { } a && a > 0 ? a : null });
    }

    private static string CurrentYearMonth() => DateTime.UtcNow.ToString("yyyy-MM");
}
