namespace GrocerySense.Core;

// Port (Replace -> single-profile) of reference-python/.../services/preferences_service.py.
// v1 reads ONE profile (the household's single master member) into EffectivePreferences for the deal filter:
// hard = allergies + hard_excludes; soft = soft_excludes; proteins/weights from the profile. The Python
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
        var cfg = _config.Load();
        return new MealProfile
        {
            Allergies = eff.HardExcludes.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Restrictions = proteins.Select(p => $"no_{p}").ToList(),
            PreferMeats = eff.ProteinWeights.Where(kv => kv.Value > 1.0).Select(kv => kv.Key).ToList(),
            AvoidMeats = proteins,
            FavoriteTags = eff.CuisinesPreferred.ToList(),
            // V3: carry the weight MAGNITUDES through (previously discarded at this exact spot) plus the
            // Smart Week nutrition preferences.
            PreferMeatWeights = eff.ProteinWeights.Where(kv => kv.Value > 1.0)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            ProteinPerServingGoal = cfg.ProteinPerServingGoal,
            PreferWholeFoodForward = cfg.PreferWholeFoodForward,
        };
    }

    private void InvalidateCache()
    {
        lock (_sync) { _cache = null; }
    }

    private EffectivePreferences Compute()
    {
        // ConfigStore.Normalize already lowercased/trimmed/deduped every token, so this is pure regrouping.
        var p = _config.Load().Preferences ?? HouseholdPreferences.Empty();

        // Hard = allergies + hard_excludes (allergies are safety-critical hard bans).
        var hard = new HashSet<string>(p.Allergies);
        hard.UnionWith(p.HardExcludes);

        return new EffectivePreferences(hard, new HashSet<string>(p.SoftExcludes),
            new HashSet<string>(p.ExcludedProteins), p.PreferredProteinWeights,
            new HashSet<string>(p.FavoriteCuisines));
    }
}
