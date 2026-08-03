using GrocerySense.Core;
using GrocerySense.Data;
using GrocerySense.Data.Repositories;

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

    // User corrections (CorrectLineMapping) store the alias as raw punctuated text ("2% milk"), which the
    // normalize pipeline strips to "2 milk" — so the normalized lookup misses and the raw-key fallback must
    // find it. The alias deliberately points at an item whose canonical name shares nothing with the input,
    // so if the fallback regresses, fuzzy can't rescue the mapping and this fails instead of passing by luck.
    [Fact]
    public void MapToItem_falls_back_to_raw_key_for_punctuated_aliases()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Table Cream").Id;
        ItemAliasesRepo.UpsertAlias(db.Conn, "2% milk", item, 1.0, "manual");

        var result = new IngredientMappingService(db.Factory).MapToItem("2% Milk");

        Assert.Equal("alias", result.Method);
        Assert.Equal(item, result.ItemId);
    }

    [Fact]
    public void FlushLearnedAliases_persists_high_confidence_fuzzy_learns_once()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Chicken Breast").Id;
        var mapper = new IngredientMappingService(db.Factory);

        var result = mapper.MapToItem("chicken breast");
        Assert.Equal("fuzzy", result.Method);
        Assert.True(result.Confidence >= 0.90);
        Assert.Null(ItemAliasesRepo.GetByAlias(db.Conn, "chicken breast")); // buffered, not yet written

        mapper.FlushLearnedAliases();
        var alias = ItemAliasesRepo.GetByAlias(db.Conn, "chicken breast");
        Assert.NotNull(alias);
        Assert.Equal(item, alias!.ItemId);
        Assert.Equal("auto_fuzzy", alias.Source);
        Assert.Equal(1, alias.TimesSeen);

        // Buffers must clear on flush: a stale buffer would re-upsert here and bump times_seen to 2.
        mapper.FlushLearnedAliases();
        Assert.Equal(1, ItemAliasesRepo.GetByAlias(db.Conn, "chicken breast")!.TimesSeen);
    }

    [Fact]
    public void FlushLearnedAliases_marks_buffered_alias_hits_seen()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Eggs").Id;
        ItemAliasesRepo.UpsertAlias(db.Conn, "dozen eggs", item, 1.0, "manual"); // times_seen = 1
        var mapper = new IngredientMappingService(db.Factory);

        Assert.Equal("alias", mapper.MapToItem("dozen eggs").Method);
        Assert.Equal(1, ItemAliasesRepo.GetByAlias(db.Conn, "dozen eggs")!.TimesSeen); // touch buffered

        mapper.FlushLearnedAliases();
        Assert.Equal(2, ItemAliasesRepo.GetByAlias(db.Conn, "dozen eggs")!.TimesSeen);

        // Empty buffers -> second flush writes nothing.
        mapper.FlushLearnedAliases();
        Assert.Equal(2, ItemAliasesRepo.GetByAlias(db.Conn, "dozen eggs")!.TimesSeen);
    }
}
