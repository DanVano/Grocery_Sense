using GrocerySense.Core;

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

    private static readonly MultiBuyDealService Svc = new();

    // ---- ValidateBundle plausibility guard: the exact inputs the guard's comment names. ----
    // If the guard regresses, these fabricate a bundle price from a recipe fraction or an OCR'd date.

    [Fact]
    public void Recipe_fraction_is_not_a_bundle()
    {
        // "1/2" matches the slash regex but qty 1 < 2 must be rejected — no price invented.
        var r = Svc.Adjust("1/2 cup sugar", 1, null, null, null);
        Assert.Equal("no_deal", r.DealNote);
        Assert.Null(r.UnitPrice);
        Assert.Null(r.LineTotal);
    }

    [Fact]
    public void Date_is_not_a_bundle()
    {
        // "12/2024" matches the slash regex but total 2024 > 999 must be rejected;
        // with a real line total present, the unit price comes from the total, not 2024/12.
        var r = Svc.Adjust("PROD 12/2024 milk", 1, null, 3.49, null);
        Assert.Equal("unit_from_total", r.DealNote);
        Assert.Equal(3.49, r.UnitPrice!.Value, 4);
        Assert.Equal(3.49, r.LineTotal!.Value, 4);
    }

    // ---- Bundle qty_fix: receipt total matches the bundle total but quantity was under-reported. ----

    [Fact]
    public void Bundle_qty_fix_corrects_quantity_and_nets_total()
    {
        var r = Svc.Adjust("2/$5", 1, null, 5.00, null);
        Assert.Contains("qty_fix", r.DealNote);
        Assert.Equal(2.0, r.Quantity);
        Assert.Equal(2.50, r.UnitPrice!.Value, 4);
        Assert.Equal(5.00, r.LineTotal!.Value, 4);
    }

    // ---- Discount netting: netTotal = baseTotal - disc, floored at 0. ----

    [Fact]
    public void Bogo_nets_discount_from_total()
    {
        var r = Svc.Adjust("BOGO", 2, 5.00, 10.00, 5.00);
        Assert.Equal("bogo_effective_price", r.DealNote);
        Assert.Equal(2.50, r.UnitPrice!.Value, 4);
        Assert.Equal(5.00, r.LineTotal!.Value, 4);
    }

    [Fact]
    public void Bundle_from_total_nets_discount()
    {
        var r = Svc.Adjust("2/$5", 2, null, 5.00, 1.00);
        Assert.Contains("from_total", r.DealNote);
        Assert.Equal(2.00, r.UnitPrice!.Value, 4);
        Assert.Equal(4.00, r.LineTotal!.Value, 4);
    }

    [Fact]
    public void Bundle_discount_exceeding_total_floors_at_zero()
    {
        // Never record negative money: Math.Max(0, lt - disc).
        var r = Svc.Adjust("2/$5", 2, null, 5.00, 6.00);
        Assert.Contains("from_total", r.DealNote);
        Assert.Equal(0.00, r.UnitPrice!.Value, 4);
        Assert.Equal(0.00, r.LineTotal!.Value, 4);
    }

    // ---- qty_defaulted disclosure: non-positive quantity defaults to 1.0 AND says so. ----

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void Nonpositive_quantity_defaults_to_one_and_is_disclosed(double qty)
    {
        var r = Svc.Adjust("chicken thighs", qty, null, 4.00, null);
        Assert.Equal(1.0, r.Quantity);
        Assert.Equal("unit_from_total;qty_defaulted", r.DealNote);
        Assert.Equal(4.00, r.UnitPrice!.Value, 4);
    }

    [Fact]
    public void Positive_quantity_is_not_flagged_qty_defaulted()
    {
        var r = Svc.Adjust("chicken thighs", 2, null, 10.00, null);
        Assert.DoesNotContain("qty_defaulted", r.DealNote);
    }
}
