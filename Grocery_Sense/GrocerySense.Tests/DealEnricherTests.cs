using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

// The single deal-enrichment pipeline both flyer sync and manual flyer ingest route through.
// End-to-end behavior through each caller stays covered by FlyerSyncServiceTests /
// FlyerIngestServiceTests; these hit the one interface directly.
public sealed class DealEnricherTests
{
    private static DealEnricher Build(TempDb db) =>
        new(new IngredientMappingService(db.Factory), new UnitNormalizationService(), new MultiBuyDealService());

    [Fact]
    public void Mapped_title_gets_item_id_effective_unit_price_and_norm_fields()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Apples").Id;
        var normalized = new IngredientMappingService(db.Factory).MapToItem("Apples Apples").NormalizedInput;
        new ItemAliasesRepo().UpsertAlias(db.Conn, normalized, item, 1.0);

        var e = Build(db).Enrich(db.Conn, "Apples", null, "2/$5.00", unitPrice: null, dealTotal: null)!;

        Assert.Equal(item, e.ItemId);
        Assert.Equal(2.50m, e.UnitPrice); // "2/$5" -> effective unit price
        Assert.NotNull(e.NormUnitPrice);
        Assert.Contains("bundle", e.NormNote);
    }

    [Fact]
    public void Unmapped_title_keeps_item_id_null_and_never_fabricates_a_price()
    {
        using var db = new TempDb();

        var e = Build(db).Enrich(db.Conn, "Zorbulon Crisps", null, null, unitPrice: null, dealTotal: null)!;

        Assert.Null(e.ItemId);          // flyers never auto-create items
        Assert.Null(e.UnitPrice);       // no price text, no provider price -> no price
        Assert.StartsWith("flyer;", e.NormNote);
    }

    [Fact]
    public void No_text_at_all_returns_null_so_the_caller_keeps_its_row_untouched()
    {
        using var db = new TempDb();
        Assert.Null(Build(db).Enrich(db.Conn, null, null, "$2.99", unitPrice: 2.99, dealTotal: null));
    }
}
