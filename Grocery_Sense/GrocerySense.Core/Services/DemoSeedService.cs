namespace GrocerySense.Core;

// Port of reference-python/.../services/demo_seed_service.py — synthetic stores/items/prices for demos.
// Clearly demo data — never mix into a real user's DB silently.
public sealed class DemoSeedService
{
    public void ResetAllDemoData() => throw new NotImplementedException();

    public Dictionary<string, int> SeedDemoData(bool resetFirst = true, int nPricePoints = 200, int daysBack = 90, int seed = 42)
        => throw new NotImplementedException();
}
