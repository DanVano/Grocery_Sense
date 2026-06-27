using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public sealed class PreferencesServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_pref_{Guid.NewGuid():N}");

    public PreferencesServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

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
    public void Collapses_single_profile_into_hard_soft_proteins_oils_weights()
    {
        var store = SeedProfile(p =>
        {
            p["allergies"] = new List<string> { "Peanuts" };
            p["hard_excludes"] = new List<string> { "pork" };
            p["soft_excludes"] = new List<string> { "Cilantro" };
            p["excluded_proteins"] = new List<string> { "lamb" };
            p["oils_allowed"] = new List<string> { "olive oil" };
            p["preferred_protein_weights"] = new Dictionary<string, double> { ["chicken"] = 2.0 };
        });

        var eff = new PreferencesService(store).ComputeEffectivePreferences();

        // Allergies + hard_excludes are hard bans (case-insensitive).
        Assert.True(eff.IsHardExcluded("peanuts"));
        Assert.True(eff.IsHardExcluded("PORK"));
        // soft is not hard.
        Assert.True(eff.IsSoftExcluded("cilantro"));
        Assert.False(eff.IsHardExcluded("cilantro"));
        // proteins + weights.
        Assert.True(eff.IsProteinHardExcluded("lamb"));
        Assert.Equal(2.0, eff.ProteinWeight("chicken"));
        Assert.Equal(1.0, eff.ProteinWeight("beef")); // default
        // oils restricted to the allow-list.
        Assert.True(eff.IsOilAllowed("olive oil"));
        Assert.False(eff.IsOilAllowed("canola oil"));
    }

    [Fact]
    public void Empty_oils_allow_list_is_unrestricted()
    {
        var store = new ConfigStore(_dir); // default profile has empty oils_allowed
        var eff = new PreferencesService(store).ComputeEffectivePreferences();
        Assert.True(eff.IsOilAllowed("anything"));
    }

    [Fact]
    public void Cache_is_invalidated_on_config_save()
    {
        var store = SeedProfile(p => p["hard_excludes"] = new List<string> { "pork" });
        var prefs = new PreferencesService(store);
        Assert.True(prefs.ComputeEffectivePreferences().IsHardExcluded("pork")); // populates cache

        var cfg = store.Load();
        cfg.Household.Members[0].Profile["hard_excludes"] = new List<string> { "beef" };
        store.Save(cfg); // raises Changed -> PreferencesService drops its cache

        var eff = prefs.ComputeEffectivePreferences();
        Assert.True(eff.IsHardExcluded("beef"));
        Assert.False(eff.IsHardExcluded("pork"));
    }
}
