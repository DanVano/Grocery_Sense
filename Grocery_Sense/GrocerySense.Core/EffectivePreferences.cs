namespace GrocerySense.Core;

// Port of the EffectivePreferences dataclass in reference-python/.../services/preferences_service.py.
// Immutable merged household state (allergies hard household-wide; master hard-excludes; secondary
// soft-only; strong-soft consensus). Built by PreferencesService.ComputeEffectivePreferences().
public sealed class EffectivePreferences
{
    public bool IsHardExcluded(string ingredient) => throw new NotImplementedException();
    public IReadOnlyList<string> SoftExcluders(string ingredient) => throw new NotImplementedException();
    public bool IsStrongSoftExcluded(string ingredient) => throw new NotImplementedException();
    public double ProteinWeight(string protein) => throw new NotImplementedException();
    public bool IsOilAllowed(string oil) => throw new NotImplementedException();
}
