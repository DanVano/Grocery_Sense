using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class PreferencesServiceTests : TempDirTestBase
{


    // Sets the household preference set and saves.
    private ConfigStore SeedPreferences(HouseholdPreferences prefs)
    {
        var store = new ConfigStore(_dir);
        store.Save(store.Load() with { Preferences = prefs });
        return store;
    }

    [Fact]
    public void Collapses_the_household_preferences_into_hard_soft_proteins_weights()
    {
        var store = SeedPreferences(new HouseholdPreferences(
            Allergies: ["Peanuts"],
            HardExcludes: ["pork"],
            SoftExcludes: ["Cilantro"],
            ExcludedProteins: ["lamb"],
            FavoriteCuisines: [],
            PreferredProteinWeights: new Dictionary<string, double> { ["chicken"] = 2.0 }));

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
        var store = SeedPreferences(HouseholdPreferences.Empty() with { HardExcludes = ["pork"] });
        var prefs = new PreferencesService(store);
        Assert.Contains("pork", prefs.ComputeEffectivePreferences().HardExcludes); // populates cache

        var cfg = store.Load();
        cfg = cfg with { Preferences = cfg.Preferences! with { HardExcludes = ["beef"] } };
        store.Save(cfg); // raises Changed -> PreferencesService drops its cache

        var eff = prefs.ComputeEffectivePreferences();
        Assert.Contains("beef", eff.HardExcludes);
        Assert.DoesNotContain("pork", eff.HardExcludes);
    }
}
