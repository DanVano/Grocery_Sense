using Xunit;

namespace GrocerySense.Tests;

public class MultiBuyFixtureTests
{
    public static IEnumerable<object[]> Phrases() =>
        Fixtures.Rows<MultiBuyCase>("multibuy_phrases.json");

    [Fact]
    public void Phrase_fixtures_load()
    {
        var cases = Fixtures.Load<MultiBuyCase>("multibuy_phrases.json");
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Case == "gap_was_now" && c.ExpectedUnitPrice is null);
        Assert.Contains(cases, c => c.Case == "bundle_slash" && c.ExpectedUnitPrice == 2.50);
    }

    [Theory(Skip = "Phase 3: implement MultiBuyDealService.Adjust.")]
    [MemberData(nameof(Phrases), DisableDiscoveryEnumeration = true)]
    public void Adjust_matches_python(MultiBuyCase c)
    {
        // Phase 3:
        //   var r = new MultiBuyDealService().Adjust(c.Desc, c.Quantity, c.UnitPrice, c.LineTotal, c.Discount);
        //   Assert.Contains(c.DealNoteContains, r.DealNote);
        //   if (c.ExpectedUnitPrice is null) Assert.Null(r.UnitPrice);
        //   else Assert.Equal(c.ExpectedUnitPrice.Value, r.UnitPrice!.Value, 4);
        _ = c;
    }
}
