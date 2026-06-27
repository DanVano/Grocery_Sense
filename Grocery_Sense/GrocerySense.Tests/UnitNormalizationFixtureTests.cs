using GrocerySense.Core;
using Xunit;

namespace GrocerySense.Tests;

public class UnitNormalizationFixtureTests
{
    public static IEnumerable<object[]> ConvertCases() =>
        Fixtures.Rows<ConvertCase>("unit_convert.json");

    [Fact]
    public void Convert_fixtures_load()
    {
        var cases = Fixtures.Load<ConvertCase>("unit_convert.json");
        Assert.NotEmpty(cases);
        var lbKg = cases.Single(c => c.Case == "lb_to_kg");
        Assert.Equal("lb", lbKg.From);
        Assert.Equal("kg", lbKg.To);
        Assert.NotNull(lbKg.Expected);
        Assert.Contains(cases, c => c.Expected is null); // cross-type rejections present
    }

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

    [Fact]
    public void Alias_fixtures_load()
    {
        var cases = Fixtures.Load<AliasCase>("unit_aliases.json");
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Raw is null && c.Expected == "unknown"); // null input case present
        Assert.Contains(cases, c => c.Raw == "#" && c.Expected == "lb");
    }

    [Theory]
    [MemberData(nameof(AliasCases), DisableDiscoveryEnumeration = true)]
    public void NormalizeUnit_matches_python(AliasCase c) =>
        Assert.Equal(c.Expected, new UnitNormalizationService().NormalizeUnit(c.Raw));

    public static IEnumerable<object[]> GuessCases() =>
        Fixtures.Rows<GuessCase>("guess_unit_from_text.json");

    [Fact]
    public void Guess_fixtures_load()
    {
        var cases = Fixtures.Load<GuessCase>("guess_unit_from_text.json");
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Text == "Soda 12 fl oz can" && c.Expected == "fl_oz"); // fl_oz beats oz
    }

    [Theory]
    [MemberData(nameof(GuessCases), DisableDiscoveryEnumeration = true)]
    public void GuessUnitFromText_matches_python(GuessCase c) =>
        Assert.Equal(c.Expected, new UnitNormalizationService().GuessUnitFromText(c.Text));
}
