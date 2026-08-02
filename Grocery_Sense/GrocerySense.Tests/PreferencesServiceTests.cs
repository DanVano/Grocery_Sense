using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class PreferencesServiceTests : TempDirTestBase
{


    // Sets keys on the single master profile and saves.
    private ConfigStore SeedProfile(Action<Dictionary<string, object?>> mutate)
    {
        var store = new ConfigStore(_dir);
        var cfg = store.Load();
        mutate(cfg.Household.Members[0].Profile);
        store.Save(cfg);
        return store;
    }

    [Fact]
    public void Collapses_single_profile_into_hard_soft_proteins_weights()
    {
        var store = SeedProfile(p =>
        {
            p["allergies"] = new List<string> { "Peanuts" };
            p["hard_excludes"] = new List<string> { "pork" };
            p["soft_excludes"] = new List<string> { "Cilantro" };
            p["excluded_proteins"] = new List<string> { "lamb" };
            p["preferred_protein_weights"] = new Dictionary<string, double> { ["chicken"] = 2.0 };
        });

        var eff = new PreferencesService(store).ComputeEffectivePreferences();

        // Allergies + hard_excludes are hard bans; values arrive lowercased.
        Assert.Contains("peanuts", eff.HardExcludes);
        Assert.Contains("pork", eff.HardExcludes);
        // soft is not hard.
        Assert.Contains("cilantro", eff.SoftExcludes);
        Assert.DoesNotContain("cilantro", eff.HardExcludes);
        // proteins + weights (absent protein = no entry; consumers default to 1.0).
        Assert.Contains("lamb", eff.ExcludedProteinsHard);
        Assert.Equal(2.0, eff.ProteinWeights["chicken"]);
        Assert.False(eff.ProteinWeights.ContainsKey("beef"));
    }

    [Fact]
    public void Cache_is_invalidated_on_config_save()
    {
        var store = SeedProfile(p => p["hard_excludes"] = new List<string> { "pork" });
        var prefs = new PreferencesService(store);
        Assert.Contains("pork", prefs.ComputeEffectivePreferences().HardExcludes); // populates cache

        var cfg = store.Load();
        cfg.Household.Members[0].Profile["hard_excludes"] = new List<string> { "beef" };
        store.Save(cfg); // raises Changed -> PreferencesService drops its cache

        var eff = prefs.ComputeEffectivePreferences();
        Assert.Contains("beef", eff.HardExcludes);
        Assert.DoesNotContain("pork", eff.HardExcludes);
    }
}
