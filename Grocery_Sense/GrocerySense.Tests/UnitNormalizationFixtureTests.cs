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

    [Theory(Skip = "Phase 3: implement UnitNormalizationService unit-price conversion.")]
    [MemberData(nameof(ConvertCases), DisableDiscoveryEnumeration = true)]
    public void Convert_matches_python(ConvertCase c)
    {
        // Phase 3:
        //   var svc = new UnitNormalizationService();
        //   var result = svc.Convert(c.PriceFrom, c.From, c.To);
        //   if (c.Expected is null) Assert.Null(result);
        //   else Assert.Equal(c.Expected.Value, result!.Value, 6);
        _ = c;
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

    [Theory(Skip = "Phase 3: implement UnitNormalizationService unit-alias folding.")]
    [MemberData(nameof(AliasCases), DisableDiscoveryEnumeration = true)]
    public void NormalizeUnit_matches_python(AliasCase c)
    {
        // Phase 3: Assert.Equal(c.Expected, new UnitNormalizationService().NormalizeUnit(c.Raw));
        _ = c;
    }

    public static IEnumerable<object[]> GuessCases() =>
        Fixtures.Rows<GuessCase>("guess_unit_from_text.json");

    [Fact]
    public void Guess_fixtures_load()
    {
        var cases = Fixtures.Load<GuessCase>("guess_unit_from_text.json");
        Assert.NotEmpty(cases);
        Assert.Contains(cases, c => c.Text == "Soda 12 fl oz can" && c.Expected == "fl_oz"); // fl_oz beats oz
    }

    [Theory(Skip = "Phase 3: implement UnitNormalizationService.GuessUnitFromText.")]
    [MemberData(nameof(GuessCases), DisableDiscoveryEnumeration = true)]
    public void GuessUnitFromText_matches_python(GuessCase c)
    {
        // Phase 3: Assert.Equal(c.Expected, new UnitNormalizationService().GuessUnitFromText(c.Text));
        _ = c;
    }
}
