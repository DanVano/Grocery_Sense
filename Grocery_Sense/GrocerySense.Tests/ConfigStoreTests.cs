using GrocerySense.Core;

namespace GrocerySense.Tests;

public sealed class ConfigStoreTests : TempDirTestBase
{


    private ConfigStore New() => new(_dir);
    private string ConfigPath => Path.Combine(_dir, "user_config.json");

    [Fact]
    public void Load_on_empty_dir_yields_single_master_member()
    {
        var cfg = New().Load();
        var member = Assert.Single(cfg.Household.Members);
        Assert.Equal("master", member.Role);
        Assert.Equal(ConfigStore.ProfileVersion, cfg.ProfileVersion);
    }

    [Fact]
    public void Load_returns_snapshot_that_cannot_mutate_cache()
    {
        var store = New();
        var cfg = store.Load();

        cfg.Preferences!.Allergies.Add("peanuts");

        // A handed-out snapshot must not mutate the cache — a fresh Load has the unmodified preferences.
        Assert.DoesNotContain("peanuts", store.Load().Preferences!.Allergies);
    }

    [Fact]
    public void Save_then_Load_round_trips_and_persists_atomically()
    {
        var store = New();
        var cfg = store.Load() with { PostalCode = "K1A0B1", MonthlyBudget = 450.0 };
        store.Save(cfg);

        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(ConfigPath + ".tmp")); // temp cleaned up by the replace.

        var reloaded = New().Load(); // fresh instance -> reads from disk, not cache.
        Assert.Equal("K1A0B1", reloaded.PostalCode);
        Assert.Equal(450.0, reloaded.MonthlyBudget);
    }

    [Fact]
    public void Load_rereads_after_external_file_change()
    {
        var store = New();
        store.Save(store.Load() with { PostalCode = "K1A0B1" });
        Assert.Equal("K1A0B1", store.Load().PostalCode);

        // Same instance must notice an out-of-band write (mtime/size key changes).
        var other = New();
        other.Save(other.Load() with { PostalCode = "M5V2T6" });
        Assert.Equal("M5V2T6", store.Load().PostalCode);
    }

    [Fact]
    public void Corrupt_config_fails_loud_and_is_not_overwritten()
    {
        File.WriteAllText(ConfigPath, "{ not valid json ");
        var ex = Assert.Throws<InvalidOperationException>(() => New().Load());
        Assert.Contains("corrupt", ex.Message);
        // Refused to overwrite — the bad bytes are still there for the user to fix.
        Assert.Equal("{ not valid json ", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void NonPositive_budget_is_normalized()
    {
        var store = New();
        store.Save(store.Load() with { MonthlyBudget = -5 });
        var cfg = New().Load();
        Assert.Null(cfg.MonthlyBudget);
    }

    [Fact]
    public void Save_raises_Changed_for_prefs_invalidation()
    {
        var store = New();
        var fired = 0;
        store.Changed += () => fired++;
        store.Save(store.Load());
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Household_preferences_survive_the_source_gen_roundtrip()
    {
        var store = New();
        store.Save(store.Load() with
        {
            Preferences = new HouseholdPreferences(
                Allergies: ["peanuts"], HardExcludes: [], SoftExcludes: [], ExcludedProteins: ["lamb"],
                FavoriteCuisines: ["thai", "italian"],
                PreferredProteinWeights: new Dictionary<string, double> { ["chicken"] = 2.0 }),
        });

        var reloaded = New().Load().Preferences!; // fresh instance -> reads from disk
        Assert.Equal(2.0, reloaded.PreferredProteinWeights["chicken"]);
        Assert.Contains("thai", reloaded.FavoriteCuisines);
        Assert.Contains("peanuts", reloaded.Allergies);
        Assert.Contains("lamb", reloaded.ExcludedProteins);
    }

    // Configs written before the typed record kept these six keys on the master member's profile dict.
    // Losing them silently would wipe the user's allergies, so Load lifts them once.
    [Fact]
    public void Legacy_member_profile_is_lifted_into_household_preferences()
    {
        File.WriteAllText(ConfigPath, """
            {
              "profile_version": 2,
              "postal_code": "K1A0B1",
              "household": {
                "primary_member_id": 1,
                "active_member_id": 1,
                "members": [
                  {
                    "id": 1, "name": "Primary", "role": "master",
                    "profile": {
                      "allergies": ["Peanuts"],
                      "hard_excludes": ["pork"],
                      "favorite_cuisines": ["thai"],
                      "preferred_protein_weights": { "chicken": 2.0 },
                      "diet": "meat eater"
                    }
                  }
                ]
              }
            }
            """);

        var prefs = New().Load().Preferences!;

        Assert.Contains("peanuts", prefs.Allergies); // lifted AND normalized to lowercase
        Assert.Contains("pork", prefs.HardExcludes);
        Assert.Contains("thai", prefs.FavoriteCuisines);
        Assert.Equal(2.0, prefs.PreferredProteinWeights["chicken"]);
    }

    [Fact]
    public void FoodInflation_seeds_defaults_when_absent()
    {
        var cfg = New().Load();
        Assert.NotNull(cfg.FoodInflationByYear);
        Assert.Equal(InflationRates.Seed["2022"], cfg.FoodInflationByYear!["2022"]);
    }

    [Fact]
    public void FoodInflation_roundtrips_and_user_edits_are_not_clobbered()
    {
        var store = New();
        var edited = new Dictionary<string, double>(store.Load().FoodInflationByYear!) { ["2026"] = 5.1 };
        store.Save(store.Load() with { FoodInflationByYear = edited });

        // Fresh instance -> reads from disk; Normalize must not re-seed over the edit.
        var reloaded = New().Load();
        Assert.Equal(5.1, reloaded.FoodInflationByYear!["2026"]);
    }
}
