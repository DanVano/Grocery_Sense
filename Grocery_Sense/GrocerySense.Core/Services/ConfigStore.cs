namespace GrocerySense.Core;

// Port of reference-python/.../config/config_store.py — owns user_config.json.
// Atomic write (temp -> fsync -> replace), mtime/size cache, invalidates PreferencesService cache on save.
// ARCHITECTURE.md: consider moving household members/preferences into SQLite for the multi-device future;
// keep JSON only for local-only settings.
public sealed class ConfigStore
{
    public UserConfig Load() => throw new NotImplementedException();

    public void Save(UserConfig cfg) => throw new NotImplementedException();

    public IReadOnlyList<HouseholdMember> ListMembers() => throw new NotImplementedException();

    public HouseholdMember? GetMember(int memberId) => throw new NotImplementedException();

    public HouseholdMember GetMasterMember() => throw new NotImplementedException();

    public HouseholdMember GetActiveMember() => throw new NotImplementedException();

    public void SetActiveMemberId(int memberId) => throw new NotImplementedException();

    public IReadOnlySet<string> GetHouseholdAllergies() => throw new NotImplementedException();

    // 7-day TTL deal cache (config/deals_cache.json).
    public object? CacheGet(string key, int maxAgeDays = 7) => throw new NotImplementedException();
    public void CacheSet(string key, object value, int maxAgeDays = 7) => throw new NotImplementedException();
}
