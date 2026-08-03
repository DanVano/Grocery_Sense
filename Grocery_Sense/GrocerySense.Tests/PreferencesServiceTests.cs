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
    public void GetMealProfile_flattens_proteins_weights_allergies_and_cuisines()
    {
        var store = SeedPreferences(new HouseholdPreferences(
            Allergies: ["peanuts"],
            HardExcludes: ["shellfish"],
            SoftExcludes: [],
            ExcludedProteins: ["pork", "lamb"],
            FavoriteCuisines: ["italian"],
            PreferredProteinWeights: new Dictionary<string, double> { ["chicken"] = 2.0, ["beef"] = 1.0 }));

        var p = new PreferencesService(store).GetMealProfile();

        // Excluded proteins land twice: as no_<p> restrictions AND as avoid_meats (ordinal-sorted).
        Assert.Equal(new[] { "no_lamb", "no_pork" }, p.Restrictions);
        Assert.Equal(new[] { "lamb", "pork" }, p.AvoidMeats);
        // Boundary pin: prefer_meats takes weights strictly > 1.0 — beef at exactly 1.0 stays out.
        Assert.Equal(new[] { "chicken" }, p.PreferMeats);
        // Allergies on the profile = the full hard-exclude union (allergies + hard_excludes), sorted.
        Assert.Equal(new[] { "peanuts", "shellfish" }, p.Allergies);
        Assert.Equal(new[] { "italian" }, p.FavoriteTags);
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
