using GrocerySense.Core;

namespace GrocerySense.Tests;

public class UnitNormalizationFixtureTests
{
    public static IEnumerable<object[]> ConvertCases() =>
        Fixtures.Rows<ConvertCase>("unit_convert.json");

    [Theory]
    [MemberData(nameof(ConvertCases), DisableDiscoveryEnumeration = true)]
    public void Convert_matches_python(ConvertCase c)
    {
        var result = new UnitNormalizationService().Convert(c.PriceFrom, c.From, c.To);
        if (c.Expected is null) Assert.Null(result);
        else Assert.Equal(c.Expected.Value, result!.Value, 6);
    }

    public static IEnumerable<object[]> AliasCases() =>
        Fixtures.Rows<AliasCase>("unit_aliases.json");

    [Theory]
    [MemberData(nameof(AliasCases), DisableDiscoveryEnumeration = true)]
    public void NormalizeUnit_matches_python(AliasCase c) =>
        Assert.Equal(c.Expected, new UnitNormalizationService().NormalizeUnit(c.Raw));

    public static IEnumerable<object[]> GuessCases() =>
        Fixtures.Rows<GuessCase>("guess_unit_from_text.json");

    [Theory]
    [MemberData(nameof(GuessCases), DisableDiscoveryEnumeration = true)]
    public void GuessUnitFromText_matches_python(GuessCase c) =>
        Assert.Equal(c.Expected, new UnitNormalizationService().GuessUnitFromText(c.Text));
}
