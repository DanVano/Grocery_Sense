namespace GrocerySense.Core;

// Port of reference-python/.../services/preferences_service.py — merges all household members into
// canonical filtering rules. RULES (load-bearing): allergies are hard exclusions household-wide;
// master hard-excludes apply; secondary members are soft-only (hard auto-downgrades to soft);
// strong-soft when 2+ members agree. Cache invalidated when config_store saves.
public sealed class PreferencesService
{
    public EffectivePreferences ComputeEffectivePreferences() => throw new NotImplementedException();

    public Dictionary<string, object?> GetMealProfile() => throw new NotImplementedException();

    public Dictionary<string, object?> GetHouseholdBaselineProfile() => throw new NotImplementedException();

    public Dictionary<string, object?> GetEffectiveEditStateForMember(int memberId) => throw new NotImplementedException();

    public (bool Ok, string Message) ValidateAddExclude(int memberId, string value, string excludeKind)
        => throw new NotImplementedException();

    public bool ResetSecondaryMemberToHouseholdBaseline(int memberId) => throw new NotImplementedException();
}
