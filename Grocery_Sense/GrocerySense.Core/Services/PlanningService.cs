namespace GrocerySense.Core;

// Port of reference-python/.../services/planning_service.py — greedy store selection for the active
// list. Returns stores (per-store subtotals + items), unassigned, and costs (basket_total,
// baseline_total, estimated_savings, coverage). Result kept as a dict until the UI needs a typed model.
public sealed class PlanningService
{
    public Dictionary<string, object?> BuildPlanForActiveList(int maxStores = 3, int daysBack = 180, int historyLimit = 12)
        => throw new NotImplementedException();
}
