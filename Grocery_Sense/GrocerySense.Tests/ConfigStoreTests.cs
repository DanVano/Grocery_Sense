using System.Text.Json;
using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gs_cfg_{Guid.NewGuid():N}");

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

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

        ((List<string>)cfg.Household.Members[0].Profile["allergies"]!).Add("peanuts");

        Assert.DoesNotContain("peanuts", store.GetHouseholdAllergies());
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
        store.Save(store.Load() with { City = "Ottawa" });
        Assert.Equal("Ottawa", store.Load().City);

        // Same instance must notice an out-of-band write (mtime/size key changes).
        var other = New();
        other.Save(other.Load() with { City = "Toronto" });
        Assert.Equal("Toronto", store.Load().City);
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
    public void GetHouseholdAllergies_unions_lowercased_tokens()
    {
        var store = New();
        var cfg = store.Load();
        cfg.Household.Members[0].Profile["allergies"] = new List<string> { "Peanuts", " Shellfish " };
        store.Save(cfg);

        var allergies = New().GetHouseholdAllergies();
        Assert.Contains("peanuts", allergies);
        Assert.Contains("shellfish", allergies);
    }

    [Fact]
    public void DealsCache_set_get_respects_ttl()
    {
        var store = New();
        store.CacheSet("flyers:loblaws", new { count = 3 }, maxAgeDays: 7);

        var hit = store.CacheGet("flyers:loblaws");
        Assert.NotNull(hit);
        Assert.Equal(3, ((JsonElement)hit!).GetProperty("count").GetInt32());

        // Expired read (maxAge 0) is a miss; missing key is a miss.
        Assert.Null(store.CacheGet("flyers:loblaws", maxAgeDays: 0));
        Assert.Null(store.CacheGet("nope"));
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
