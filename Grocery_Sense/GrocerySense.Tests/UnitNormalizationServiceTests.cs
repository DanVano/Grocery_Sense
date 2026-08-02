using GrocerySense.Core;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Tests;

// DB-backed normalize: the pure unit math is covered by the fixture theories; this guards the
// default_unit backfill + conversion-into-default behavior.
public sealed class UnitNormalizationServiceTests
{
    [Fact]
    public void Normalize_backfills_default_unit_then_converts_into_it()
    {
        using var db = new TempDb();
        var svc = new UnitNormalizationService();
        var item = ItemsRepo.CreateItem(db.Conn, "Chicken").Id; // no default_unit yet

        // First observation sets the default unit (lb) and needs no conversion.
        var first = svc.Normalize(db.Conn, item, 5.00, "lb");
        Assert.Equal("lb", first.NormUnit);
        Assert.Equal("no_conversion", first.Note);
        Assert.Equal("lb", svc.GetItemDefaultUnit(db.Conn, item));

        // A later kg observation converts into the established lb default: $11.02/kg -> ~$5.00/lb.
        var second = svc.Normalize(db.Conn, item, 11.0231131, "kg");
        Assert.Equal("lb", second.NormUnit);
        Assert.StartsWith("converted(kg->lb)", second.Note);
        Assert.Equal(5.00, second.NormUnitPrice, 5);
    }

    [Fact]
    public void Normalize_falls_back_to_each_for_unknown_unit_and_description()
    {
        using var db = new TempDb();
        var svc = new UnitNormalizationService();
        var item = ItemsRepo.CreateItem(db.Conn, "Mystery").Id;

        var r = svc.Normalize(db.Conn, item, 2.49, "wat", description: "no hint here");
        Assert.Equal("each", r.NormUnit);
        Assert.Equal("no_conversion", r.Note);
        Assert.Equal(2.49, r.NormUnitPrice);
    }
}
