using System.Globalization;
using System.Text.Json;

namespace GrocerySense.Core;

// Port (Replace -> single-profile) of reference-python/.../services/preferences_service.py.
// v1 reads ONE profile (the household's single master member) into EffectivePreferences for the deal filter:
// hard = allergies + hard_excludes; soft = soft_excludes; proteins/oils/weights from the profile. The Python
// multi-member merge (secondary soft-downgrade, strong-soft consensus, star annotations, meal profile,
// member edit-state/validate/reset) is v2-deferred and intentionally not ported here.
//
// Caches the computed result and drops it when ConfigStore saves (subscribes to Changed). ponytail: the cache
// is invalidated on Save only; an out-of-band edit to user_config.json won't refresh it until the next Save,
// which is fine for the single-user v1 app that owns the file.
public sealed class PreferencesService
{
    private readonly ConfigStore _config;
    private readonly object _sync = new();
    private EffectivePreferences? _cache;

    public PreferencesService(ConfigStore config)
    {
        _config = config;
        _config.Changed += InvalidateCache;
    }

    public EffectivePreferences ComputeEffectivePreferences()
    {
        lock (_sync)
        {
            return _cache ??= Compute();
        }
    }

    // Flat meal profile the RecipeEngine + MealSuggestionService consume (port of Python get_meal_profile,
    // single-profile): allergies = hard excludes; no_<protein> restrictions + avoid_meats from hard-excluded
    // proteins; prefer_meats from protein weights > 1.0; favorite_tags from the profile's favorite cuisines.
    public MealProfile GetMealProfile()
    {
        var eff = ComputeEffectivePreferences();
        var proteins = eff.ExcludedProteinsHard.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new MealProfile
        {
            Allergies = eff.HardExcludes.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Restrictions = proteins.Select(p => $"no_{p}").ToList(),
            PreferMeats = eff.ProteinWeights.Where(kv => kv.Value > 1.0).Select(kv => kv.Key).ToList(),
            AvoidMeats = proteins,
            FavoriteTags = eff.CuisinesPreferred.ToList(),
        };
    }

    private void InvalidateCache()
    {
        lock (_sync) { _cache = null; }
    }

    private EffectivePreferences Compute()
    {
        var profile = _config.GetMasterMember().Profile;

        // Hard = allergies + hard_excludes (allergies are safety-critical hard bans).
        var hard = new HashSet<string>(NormList(Get(profile, "allergies")));
        hard.UnionWith(NormList(Get(profile, "hard_excludes")));

        var soft = new HashSet<string>(NormList(Get(profile, "soft_excludes")));
        var proteinsHard = new HashSet<string>(NormList(Get(profile, "excluded_proteins")));
        var cuisines = new HashSet<string>(NormList(Get(profile, "favorite_cuisines")));
        var oils = new HashSet<string>(NormList(Get(profile, "oils_allowed")));
        var weights = NormWeights(Get(profile, "preferred_protein_weights"));

        return new EffectivePreferences(hard, soft, proteinsHard, weights, cuisines, oils);
    }

    private static object? Get(IReadOnlyDictionary<string, object?> profile, string key) =>
        profile.TryGetValue(key, out var v) ? v : null;

    // Coerce a profile value (List, JsonElement array/string, or comma string) to lowercase tokens.
    private static IEnumerable<string> NormList(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                foreach (var t in Split(s)) yield return t;
                break;
            case JsonElement je when je.ValueKind == JsonValueKind.String:
                foreach (var t in Split(je.GetString() ?? "")) yield return t;
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

    // Coerce preferred_protein_weights to {lower_key -> double}; unparseable weights default to 1.0.
    private static IReadOnlyDictionary<string, double> NormWeights(object? value)
    {
        var weights = new Dictionary<string, double>();
        switch (value)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Object:
                foreach (var prop in je.EnumerateObject())
                {
                    var key = prop.Name.Trim().ToLowerInvariant();
                    if (key.Length == 0) continue;
                    weights[key] = prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var d)
                        ? d : 1.0;
                }
                break;
            case System.Collections.IDictionary dict:
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    var key = e.Key?.ToString()?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(key)) continue;
                    weights[key] = ToDouble(e.Value);
                }
                break;
        }
        return weights;
    }

    private static double ToDouble(object? v) =>
        v switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => double.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1.0,
        };

    private static IEnumerable<string> Split(string s) =>
        s.Split(',').Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0);
}
