namespace GrocerySense.Core;

// Single-profile effective preferences (v1). The Python EffectivePreferences merged many household members
// (allergies hard household-wide, master hard-excludes, secondary soft-only, strong-soft consensus). v1 has
// ONE profile, so that collapses to: hard = allergies + hard_excludes; soft = soft_excludes; proteins/oils/
// weights = the profile's. No member-name starring, no strong-soft consensus (both need >=2 members -> v2).
// Built by PreferencesService.ComputeEffectivePreferences(). Forward-compatible: the v2 master member fills
// the same fields.
public sealed class EffectivePreferences
{
    public IReadOnlySet<string> HardExcludes { get; }
    public IReadOnlySet<string> SoftExcludes { get; }
    public IReadOnlySet<string> ExcludedProteinsHard { get; }
    public IReadOnlyDictionary<string, double> ProteinWeights { get; }
    public IReadOnlySet<string> CuisinesPreferred { get; }
    public IReadOnlySet<string> OilsAllowed { get; } // empty => unrestricted

    public EffectivePreferences(
        IReadOnlySet<string> hardExcludes,
        IReadOnlySet<string> softExcludes,
        IReadOnlySet<string> excludedProteinsHard,
        IReadOnlyDictionary<string, double> proteinWeights,
        IReadOnlySet<string> cuisinesPreferred,
        IReadOnlySet<string> oilsAllowed)
    {
        HardExcludes = hardExcludes;
        SoftExcludes = softExcludes;
        ExcludedProteinsHard = excludedProteinsHard;
        ProteinWeights = proteinWeights;
        CuisinesPreferred = cuisinesPreferred;
        OilsAllowed = oilsAllowed;
    }

    public bool IsHardExcluded(string ingredient) => Has(HardExcludes, ingredient);
    public bool IsSoftExcluded(string ingredient) => Has(SoftExcludes, ingredient);
    public bool IsProteinHardExcluded(string protein) => Has(ExcludedProteinsHard, protein);

    public double ProteinWeight(string protein) =>
        ProteinWeights.TryGetValue(Norm(protein), out var w) ? w : 1.0;

    public bool IsOilAllowed(string oil)
    {
        var key = Norm(oil);
        if (key.Length == 0) return true;
        return OilsAllowed.Count == 0 || OilsAllowed.Contains(key); // empty allow-list => unrestricted
    }

    private static bool Has(IReadOnlySet<string> set, string value)
    {
        var key = Norm(value);
        return key.Length > 0 && set.Contains(key);
    }

    private static string Norm(string? value) => (value ?? "").Trim().ToLowerInvariant();
}
