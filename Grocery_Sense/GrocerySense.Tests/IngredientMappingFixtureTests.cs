using GrocerySense.Core;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public class IngredientMappingFixtureTests
{
    public static IEnumerable<object[]> NormalizeCases() =>
        Fixtures.Rows<NormalizeCase>("ingredient_normalize_pipeline.json");

    [Theory]
    [MemberData(nameof(NormalizeCases), DisableDiscoveryEnumeration = true)]
    public void NormalizePipeline_matches_python(NormalizeCase c)
    {
        using var db = new TempDb();
        var mapper = new IngredientMappingService(db.Factory);
        Assert.Equal(c.Expected, mapper.NormalizePipeline(c.Raw));
    }

    // The bulk overload must map on the passed connection and never touch the factory. The factory here points
    // at a DB in a directory that does not exist — if the overload opened it, this would throw or create the
    // file. It does neither, proving a bulk caller's connection is reused (no per-line connection open).
    [Fact]
    public void MapToItem_accepts_a_caller_owned_connection()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        var missingDb = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "db.sqlite");
        var mapper = new IngredientMappingService(new SqliteConnectionFactory(missingDb));

        var result = mapper.MapToItem(db.Conn, "milk");

        Assert.Equal(item, result.ItemId);
        Assert.False(File.Exists(missingDb));
    }

    public static IEnumerable<object[]> AmbiguityCases() =>
        Fixtures.Rows<AmbiguityCase>("alias_ambiguity.json");

    // Python-parity regression: rapidfuzz lowercases both sides (default_process); the FuzzySharp scorer is
    // case-sensitive, so without lowercased choices "milk" vs "Milk" scores 0.75 and misses the 0.78 gate.
    [Fact]
    public void MapToItem_fuzzy_ignores_canonical_name_casing()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;

        var result = new IngredientMappingService(db.Factory).MapToItem("milk");

        Assert.Equal(item, result.ItemId);
        Assert.Equal("Milk", result.CanonicalName); // original casing reported, not the scoring key
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
