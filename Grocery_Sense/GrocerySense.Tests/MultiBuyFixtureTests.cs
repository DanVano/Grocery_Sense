using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public class MultiBuyFixtureTests
{
    public static IEnumerable<object[]> Phrases() =>
        Fixtures.Rows<MultiBuyCase>("multibuy_phrases.json");

    [Theory]
    [MemberData(nameof(Phrases), DisableDiscoveryEnumeration = true)]
    public void Adjust_matches_python(MultiBuyCase c)
    {
        var r = new MultiBuyDealService().Adjust(c.Desc, c.Quantity, c.UnitPrice, c.LineTotal, c.Discount);
        Assert.Contains(c.DealNoteContains, r.DealNote);
        if (c.ExpectedUnitPrice is null) Assert.Null(r.UnitPrice);
        else Assert.Equal(c.ExpectedUnitPrice.Value, r.UnitPrice!.Value, 4);
    }
}
