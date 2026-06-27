using Xunit;

namespace GrocerySense.Tests;

public class IngredientMappingFixtureTests
{
    public static IEnumerable<object[]> NormalizeCases() =>
        Fixtures.Rows<NormalizeCase>("ingredient_normalize_pipeline.json");

    [Fact]
    public void Normalize_fixtures_load()
    {
        var cases = Fixtures.Load<NormalizeCase>("ingredient_normalize_pipeline.json");
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Raw == "GRND BF" && c.Expected == "ground beef");
        Assert.Contains(cases, c => c.Raw == "" && c.Expected == "");
    }

    [Theory(Skip = "Phase 3: implement IngredientMappingService normalization pipeline.")]
    [MemberData(nameof(NormalizeCases), DisableDiscoveryEnumeration = true)]
    public void NormalizePipeline_matches_python(NormalizeCase c)
    {
        // Phase 3: Assert.Equal(c.Expected, mapper.NormalizePipeline(c.Raw));
        _ = c;
    }

    public static IEnumerable<object[]> AmbiguityCases() =>
        Fixtures.Rows<AmbiguityCase>("alias_ambiguity.json");

    [Fact]
    public void Ambiguity_fixtures_load()
    {
        var cases = Fixtures.Load<AmbiguityCase>("alias_ambiguity.json");
        Assert.NotEmpty(cases);
        var oil = cases.Single(c => c.Case == "collision_oil_vs_olive_oil");
        Assert.Equal("none", oil.ExpectedMethod);
        Assert.Null(oil.ExpectedCanonical);
        Assert.Contains(cases, c => c.Case == "exact_match" && c.Canonicals.Length == 2);
    }

    [Theory(Skip = "Phase 3: implement IngredientMappingService.MapToItem against a seeded temp DB.")]
    [MemberData(nameof(AmbiguityCases), DisableDiscoveryEnumeration = true)]
    public void MapToItem_matches_python(AmbiguityCase c)
    {
        // Phase 3: seed canonicals into a temp DB, map raw, assert method + canonical.
        _ = c;
    }
}
