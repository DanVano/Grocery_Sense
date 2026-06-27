using GrocerySense.Core;
using GrocerySense.Data.Repositories;
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

    [Theory]
    [MemberData(nameof(NormalizeCases), DisableDiscoveryEnumeration = true)]
    public void NormalizePipeline_matches_python(NormalizeCase c)
    {
        using var db = new TempDb();
        var mapper = new IngredientMappingService(db.Factory);
        Assert.Equal(c.Expected, mapper.NormalizePipeline(c.Raw));
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

    [Theory]
    [MemberData(nameof(AmbiguityCases), DisableDiscoveryEnumeration = true)]
    public void MapToItem_matches_python(AmbiguityCase c)
    {
        using var db = new TempDb();
        foreach (var name in c.Canonicals) ItemsRepo.CreateItem(db.Conn, name);

        var result = new IngredientMappingService(db.Factory).MapToItem(c.Raw);

        Assert.Equal(c.ExpectedMethod, result.Method);
        Assert.Equal(c.ExpectedCanonical, result.CanonicalName);
    }
}
