using System.Text.Json;

namespace GrocerySense.Core;

// Port of reference-python/.../config/config_store.py — owns user_config.json.
// v1 scope (CONTRACT_AUDIT.md): atomic JSON I/O (temp -> flush(true) -> replace), mtime/size read cache,
// single-profile household, deals cache, and a prefs-cache-invalidation seam. Multi-member CRUD
// (add/rename/delete/primary, member switching) is v2-deferred — the Household shape is kept only so the
// single profile is forward-compatible with a future "master member".
//
// Path: callers pass the writable app-data directory (NOT a source-relative path — mobile revokes those).
// Prefs invalidation: PreferencesService (Phase 3) subscribes to Changed in its ctor; ConfigStore has no
// upward dependency on it (mirrors Python's lazy import + _invalidate_effective_cache()).
public sealed class ConfigStore
{
    public const int ProfileVersion = 3;
    private const string RoleMaster = "master";
    private const string RoleSecondary = "secondary";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // ponytail: no sort_keys (Python had it for stable diffs); STJ has no built-in key sort and it isn't load-bearing.
    };

    private readonly string _configFile;
    private readonly string _cacheFile;
    private readonly object _sync = new();

    private UserConfig? _cache;
    private (DateTime Mtime, long Size)? _cacheKey;

    // Raised after a successful Save so downstream caches (PreferencesService) can invalidate.
    public event Action? Changed;

    public ConfigStore(string configDir)
    {
        _configFile = Path.Combine(configDir, "user_config.json");
        _cacheFile = Path.Combine(configDir, "deals_cache.json");
    }

    // ---------------- config load/save ----------------

    public UserConfig Load()
    {
        lock (_sync)
        {
            var key = StatKey(_configFile);
            if (_cache is null || key != _cacheKey)
            {
                _cache = Normalize(ReadRawConfig());
                _cacheKey = key;
            }
            // Hand out an independent snapshot: async workers (flyer sync, alerts) read while the UI
            // mutates on read-modify-Save. The household is tiny, so the clone cost is negligible.
            return Clone(_cache);
        }
    }

    public void Save(UserConfig cfg)
    {
        var normalized = Normalize(cfg);
        lock (_sync)
        {
            AtomicWriteJson(_configFile, normalized);
            _cache = normalized;
            _cacheKey = StatKey(_configFile);
        }
        Changed?.Invoke();
    }

    // ---------------- household (single-profile) ----------------

    public IReadOnlyList<HouseholdMember> ListMembers() => Load().Household.Members;

    public HouseholdMember? GetMember(int memberId) =>
        Load().Household.Members.FirstOrDefault(m => m.Id == memberId);

    public HouseholdMember GetMasterMember()
    {
        var h = Load().Household;
        return h.Members.FirstOrDefault(m => m.Role == RoleMaster) ?? h.Members[0];
    }

    public HouseholdMember GetActiveMember()
    {
        var h = Load().Household;
        return h.Members.FirstOrDefault(m => m.Id == h.ActiveMemberId) ?? GetMasterMember();
    }

    public void SetActiveMemberId(int memberId)
    {
        var cfg = Load();
        if (cfg.Household.Members.Any(m => m.Id == memberId))
            Save(cfg with { Household = cfg.Household with { ActiveMemberId = memberId } });
    }

    // Union of all members' allergy tokens (canonical lowercase). Single member in v1, but the union
    // shape stays correct if v2 adds members.
    public IReadOnlySet<string> GetHouseholdAllergies()
    {
        var allergies = new HashSet<string>();
        foreach (var m in Load().Household.Members)
            if (m.Profile.TryGetValue("allergies", out var v))
                foreach (var token in CoerceStringList(v))
                    allergies.Add(token);
        return allergies;
    }

    // ---------------- deals cache (regenerable, 7-day TTL) ----------------

    public object? CacheGet(string key, int maxAgeDays = 7)
    {
        var cache = LoadCache();
        if (!cache.TryGetValue(key, out var entry)) return null;
        var ageDays = (NowEpoch() - entry.StoredAt) / 86400.0;
        if (ageDays < 0 || ageDays > maxAgeDays) return null; // reject expired + future-stamped (clock skew).
        return entry.Value;
    }

    public void CacheSet(string key, object value, int maxAgeDays = 7)
    {
        lock (_sync)
        {
            var now = NowEpoch();
            var cache = LoadCache();
            cache[key] = new CacheEntry(now, JsonSerializer.SerializeToElement(value, JsonOpts));
            foreach (var stale in cache
                         .Where(kv => { var a = (now - kv.Value.StoredAt) / 86400.0; return a < 0 || a > maxAgeDays; })
                         .Select(kv => kv.Key).ToList())
                cache.Remove(stale);
            AtomicWriteJson(_cacheFile, cache);
        }
    }

    private Dictionary<string, CacheEntry> LoadCache()
    {
        if (!File.Exists(_cacheFile)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cacheFile), JsonOpts)
                   ?? new();
        }
        catch (JsonException)
        {
            // The deals cache is regenerable — resetting is acceptable, but disclose it (no silent degrade).
            Console.Error.WriteLine($"deals_cache.json was corrupt; rebuilding empty cache ({_cacheFile}).");
            return new();
        }
    }

    private sealed record CacheEntry(double StoredAt, JsonElement Value);

    // ---------------- internals ----------------

    private UserConfig ReadRawConfig()
    {
        if (!File.Exists(_configFile)) return EmptyConfig();
        try
        {
            return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_configFile), JsonOpts) ?? EmptyConfig();
        }
        catch (JsonException e)
        {
            // Fail loud: a corrupt user_config.json must NOT be reset to defaults — that silently wipes the
            // user's allergies (safety-critical). Surface an actionable error instead (CLAUDE: fail loud).
            throw new InvalidOperationException(
                $"user_config.json is corrupt and cannot be parsed ({e.Message}). Refusing to overwrite it " +
                $"with defaults — your allergy settings would be lost. Fix or restore the file at {_configFile}.", e);
        }
    }

    private static UserConfig EmptyConfig() => new(
        ProfileVersion, "", "", "CA", new Dictionary<string, int>(), new List<int>(), null, 0.18,
        new Household(1, 1, new List<HouseholdMember>()));

    private static UserConfig Normalize(UserConfig c) => c with
    {
        ProfileVersion = ProfileVersion, // always bump to latest on load/save (safe).
        PostalCode = c.PostalCode ?? "",
        City = c.City ?? "",
        Country = string.IsNullOrWhiteSpace(c.Country) ? "CA" : c.Country,
        StorePriority = c.StorePriority ?? new Dictionary<string, int>(),
        FavoriteStoreIds = c.FavoriteStoreIds ?? new List<int>(),
        MonthlyBudget = c.MonthlyBudget is > 0 ? c.MonthlyBudget : null,
        GasCostPerKm = c.GasCostPerKm > 0 ? c.GasCostPerKm : 0.18, // gas unused (optimizer redesign) but kept valid.
        // Optimizer settings: clamp missing/invalid (e.g. 0 from an older config) back to the defaults.
        MaxStores = c.MaxStores > 0 ? c.MaxStores : 3,
        MinItemSavingPct = c.MinItemSavingPct > 0 ? c.MinItemSavingPct : 0.10,
        MinStoreSaving = c.MinStoreSaving > 0 ? c.MinStoreSaving : 5.0,
        Household = EnsureHousehold(c.Household),
    };

    // Ensures: >=1 member, >=1 master (tied to primary if possible), valid primary/active ids, non-null
    // profiles. ponytail: structural only — per-field profile sanitization (ensure_member_profile_defaults)
    // moves to PreferencesService in Phase 3 (single-profile Replace). Does NOT drop extra members (no data loss).
    private static Household EnsureHousehold(Household? h)
    {
        var members = h?.Members is { Count: > 0 } ms ? ms.ToList() : new List<HouseholdMember>();
        if (members.Count == 0)
            members.Add(new HouseholdMember(1, "Primary", RoleMaster, DefaultMemberProfile()));

        var primary = h?.PrimaryMemberId ?? 1;
        if (!members.Any(m => SanitizeRole(m.Role) == RoleMaster))
        {
            var idx = members.FindIndex(m => m.Id == primary);
            if (idx < 0) idx = 0;
            members[idx] = members[idx] with { Role = RoleMaster };
        }

        for (var i = 0; i < members.Count; i++)
            members[i] = members[i] with
            {
                Role = SanitizeRole(members[i].Role),
                Profile = members[i].Profile is { Count: > 0 } p ? p : DefaultMemberProfile(),
            };

        var ids = members.Select(m => m.Id).ToHashSet();
        if (!ids.Contains(primary)) primary = members.Min(m => m.Id);
        var active = h?.ActiveMemberId ?? primary;
        if (!ids.Contains(active)) active = primary;
        return new Household(primary, active, members);
    }

    // Canonical profile shape (forward-compatible with a v2 master member). Kept whole though v1 only reads
    // allergies + hard/soft excludes + oils (PreferencesService, Phase 3).
    private static Dictionary<string, object?> DefaultMemberProfile() => new()
    {
        ["eats_meat"] = true,
        ["eats_fish"] = true,
        ["eats_dairy"] = true,
        ["eats_eggs"] = true,
        ["excluded_proteins"] = new List<string>(),
        ["preferred_protein_weights"] = new Dictionary<string, double>(),
        ["allergies"] = new List<string>(),
        ["hard_excludes"] = new List<string>(),
        ["soft_excludes"] = new List<string>(),
        ["favorite_cuisines"] = new List<string>(),
        ["spice_level"] = "medium",
        ["meal_styles"] = new List<string>(),
        ["oils_allowed"] = new List<string>(),
        ["diet"] = "meat eater",
        ["favorite_tags"] = new List<string>(),
        ["price_sensitivity"] = "medium",
    };

    private static string SanitizeRole(string? role)
    {
        var r = (role ?? RoleSecondary).Trim().ToLowerInvariant();
        return r is RoleMaster or RoleSecondary ? r : RoleSecondary;
    }

    // Coerce a profile value (List, JsonElement array/string, or comma string) to lowercase tokens.
    private static IEnumerable<string> CoerceStringList(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                foreach (var t in SplitTokens(s)) yield return t;
                break;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                foreach (var t in SplitTokens(je.GetString() ?? "")) yield return t;
                break;
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                foreach (var el in je.EnumerateArray())
                {
                    var t = (el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString())?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(t)) yield return t;
                }
                break;
            case System.Collections.IEnumerable seq:
                foreach (var el in seq)
                {
                    var t = el?.ToString()?.Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(t)) yield return t;
                }
                break;
        }
    }

    private static IEnumerable<string> SplitTokens(string s) =>
        s.Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0);

    // Container-level copy so a handed-out snapshot can be mutated without touching the cache.
    private static UserConfig Clone(UserConfig c) => c with
    {
        StorePriority = new Dictionary<string, int>(c.StorePriority),
        FavoriteStoreIds = c.FavoriteStoreIds.ToList(),
        Household = c.Household with
        {
            Members = c.Household.Members
                .Select(m => m with { Profile = CloneProfile(m.Profile) }).ToList(),
        },
    };

    private static Dictionary<string, object?> CloneProfile(Dictionary<string, object?> profile) =>
        profile.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            List<string> v => v.ToList(),
            Dictionary<string, double> v => new Dictionary<string, double>(v),
            JsonElement v => v.Clone(),
            _ => kv.Value,
        });

    private static (DateTime, long)? StatKey(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : null;
    }

    private static double NowEpoch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // temp -> flush(true) -> atomic replace, so a crash mid-write never leaves a truncated config.
    private static void AtomicWriteJson<T>(string path, T data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, data, JsonOpts);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
