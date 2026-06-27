using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

// First port target: copy the canonical cases from
// reference-python/tests/price_intelligence/test_unit_normalization.py and assert NormalizedPrice.
// These golden tests freeze the money math before/while it's ported (ARCHITECTURE.md sequence step 1).
public class UnitNormalizationGoldenTests
{
    [Fact(Skip = "Port unit-normalization golden cases from reference-python before enabling.")]
    public void Normalize_matches_python_golden_cases()
    {
        var svc = new UnitNormalizationService();
        // var result = svc.Normalize(itemId: 1, unitPrice: 4.40, observedUnit: "lb");
        // Assert.Equal("kg", result.NormUnit);
        _ = svc;
    }

    [Fact]
    public void Solution_wiring_smoke()
    {
        // Proves the test project references Core and the host runs. Replace as real tests land.
        Assert.NotNull(new UnitNormalizationService());
    }
}
