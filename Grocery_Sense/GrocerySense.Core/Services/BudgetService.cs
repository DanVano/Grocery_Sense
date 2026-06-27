namespace GrocerySense.Core;

// Port of reference-python/.../services/budget_service.py — month spend vs budget + trend; gas cost.
public sealed class BudgetService
{
    public Dictionary<string, object?> GetBudgetStatus() => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> GetTrend(int months = 12) => throw new NotImplementedException();

    public void SaveMonthlyBudget(double? amount) => throw new NotImplementedException();

    public void SaveGasCostPerKm(double rate) => throw new NotImplementedException();

    public double GetGasCostPerKm() => throw new NotImplementedException();
}
