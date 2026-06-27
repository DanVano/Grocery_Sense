namespace GrocerySense.Core;

// Port of reference-python/.../services/unit_normalization_service.py — the ONLY place for unit math.
// Normalizes to a base per dimension (weight->kg, volume->L, count->each, dozen<->each) and records
// norm_unit_price/norm_unit/norm_note. GOLDEN-TEST before porting (silent money bugs live here).
public sealed class UnitNormalizationService
{
    public NormalizedPrice Normalize(int itemId, double unitPrice, string observedUnit, string? description = null)
        => throw new NotImplementedException();

    public string? GetItemDefaultUnit(int itemId) => throw new NotImplementedException();

    public void SetItemDefaultUnitIfMissing(int itemId, string observedUnit) => throw new NotImplementedException();

    public string GuessUnitFromText(string text) => throw new NotImplementedException();
}
