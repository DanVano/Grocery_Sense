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

    private readonly string _configFile;
    private readonly object _sync = new();

    private UserConfig? _cache;
    private (DateTime Mtime, long Size)? _cacheKey;

    // Raised after a successful Save so downstream caches (PreferencesService) can invalidate.
    public event Action? Changed;

    public ConfigStore(string configDir)
    {
        _configFile = Path.Combine(configDir, "user_config.json");
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
            AtomicWrite(_configFile, JsonSerializer.SerializeToUtf8Bytes(normalized, UserConfigJsonContext.Default.UserConfig));
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

    public bool IsMaster(int memberId) => GetMember(memberId)?.Role == RoleMaster;

    // A secondary member is any existing non-master (family meal-picks create a review request only for these).
    public bool IsSecondary(int memberId)
    {
        var m = GetMember(memberId);
        return m is not null && m.Role != RoleMaster;
    }

    // v2 members are names-only: id + name + role, with the canonical (unused-for-prefs) default profile.
    // Preferences stay the single household profile on the master member.
    public HouseholdMember AddMember(string name, string role = RoleSecondary)
    {
        var cfg = Load();
        // Monotonic id: NextMemberId is repaired by EnsureHousehold to >= the current max id, so +1 never
        // collides with a live member AND never reuses a deleted member's id.
        var nextId = cfg.Household.NextMemberId + 1;
        var member = new HouseholdMember(nextId, (name ?? "").Trim(), SanitizeRole(role), DefaultMemberProfile());
        Save(cfg with { Household = cfg.Household with {
            Members = cfg.Household.Members.Append(member).ToList(), NextMemberId = nextId } });
        return member;
    }

    public void RenameMember(int memberId, string newName)
    {
        var name = (newName ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Member name can't be empty.", nameof(newName));
        var cfg = Load();
        var members = cfg.Household.Members
            .Select(m => m.Id == memberId ? m with { Name = name } : m).ToList();
        Save(cfg with { Household = cfg.Household with { Members = members } });
    }

    // Removes a secondary member. The master and the last-remaining member can't be deleted (a household
    // always keeps a master profile). If the removed member was active, active falls back to the primary.
    public void DeleteMember(int memberId)
    {
        var cfg = Load();
        var target = cfg.Household.Members.FirstOrDefault(m => m.Id == memberId);
        if (target is null) return;
        if (target.Role == RoleMaster) throw new InvalidOperationException("The master member can't be deleted.");
        if (cfg.Household.Members.Count <= 1) throw new InvalidOperationException("The only member can't be deleted.");

        var members = cfg.Household.Members.Where(m => m.Id != memberId).ToList();
        var active = cfg.Household.ActiveMemberId == memberId ? cfg.Household.PrimaryMemberId : cfg.Household.ActiveMemberId;
        Save(cfg with { Household = cfg.Household with { Members = members, ActiveMemberId = active } });
    }

    // ---------------- internals ----------------

    private UserConfig ReadRawConfig()
    {
        if (!File.Exists(_configFile)) return EmptyConfig();
        try
        {
            // Source-gen (no reflection) — this path runs on every start and must survive iOS full AOT (B1).
            return JsonSerializer.Deserialize(File.ReadAllText(_configFile), UserConfigJsonContext.Default.UserConfig)
                   ?? EmptyConfig();
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
        ProfileVersion, "", "", null,
        new Household(1, 1, new List<HouseholdMember>()));

    private static UserConfig Normalize(UserConfig c) => c with
    {
        ProfileVersion = ProfileVersion, // always bump to latest on load/save (safe).
        PostalCode = c.PostalCode ?? "",
        City = c.City ?? "",
        MonthlyBudget = c.MonthlyBudget is > 0 ? c.MonthlyBudget : null,
        // Optimizer settings: clamp missing/invalid (e.g. 0 from an older config) back to the defaults.
        MaxStores = c.MaxStores > 0 ? c.MaxStores : 3,
        MinItemSavingPct = c.MinItemSavingPct > 0 ? c.MinItemSavingPct : 0.10,
        MinStoreSaving = c.MinStoreSaving > 0 ? c.MinStoreSaving : 5.0,
        // Seed the StatCan defaults only when absent (null/empty) — a user-edited table is left untouched.
        FoodInflationByYear = c.FoodInflationByYear is { Count: > 0 }
            ? c.FoodInflationByYear
            : new Dictionary<string, double>(InflationRates.Seed),
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
        // Highest id ever issued: never below the current max member id (repairs older/0 configs), and never
        // regresses below a previously-persisted counter (so a deleted top id stays retired).
        var nextMemberId = Math.Max(h?.NextMemberId ?? 0, members.Max(m => m.Id));
        return new Household(primary, active, members, nextMemberId);
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


    // Container-level copy so a handed-out snapshot can be mutated without touching the cache.
    private static UserConfig Clone(UserConfig c) => c with
    {
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

    // temp -> flush(true) -> atomic replace, so a crash mid-write never leaves a truncated config file.
    private static void AtomicWrite(string path, byte[] utf8Bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(utf8Bytes, 0, utf8Bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
