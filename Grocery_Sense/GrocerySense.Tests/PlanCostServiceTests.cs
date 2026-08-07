using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

// V3 Phase 2 cost-model gates: serving scaling, unit conversion, shared-ingredient dedup, pantry-aware
// incremental total, flyer precedence, honest coverage, and the read-only mapping guarantee (estimating a
// plan must never write aliases).
public sealed class PlanCostServiceTests
{
    private static PlanCostService Svc(TempDb db) =>
        new(db.Factory, new IngredientMappingService(db.Factory));

    private static Recipe RecipeWith(string name, int servings, params (string Name, double Qty, string Unit)[] ings) =>
        new(1, name, servings,
            ings.Select(i => i.Name).ToList(), [], [],
            new RecipeDetails(
                ings.Select(i => new RecipeIngredientDetail(i.Name, i.Qty, i.Unit, "whole")).ToList(),
                20, [ings[0].Name], "curated"));

    [Fact]
    public void Shared_ingredients_count_once_with_summed_quantities()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 5.0, "kg", source: "manual", date: Today);

        var a = RecipeWith("A", 4, ("rice", 300, "g"));
        var b = RecipeWith("B", 4, ("rice", 300, "g"));
        var est = Svc(db).EstimatePlanCost([a, b]);

        var row = Assert.Single(est.Ingredients, i => i.Name == "rice");
        Assert.Equal(600, row.NeededQty);                    // 300 + 300, one row — not two
        Assert.Equal(3.0, row.Cost!.Value, 2);               // 0.6 kg x $5/kg
        Assert.Equal(3.0, est.PricedTotal, 2);
    }

    [Fact]
    public void Quantities_scale_by_household_servings()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 5.0, "kg", source: "manual", date: Today);

        var est = Svc(db).EstimatePlanCost([RecipeWith("A", 4, ("rice", 300, "g"))], householdServings: 8);

        Assert.Equal(600, Assert.Single(est.Ingredients).NeededQty); // 2x the 4-serving quantity
    }

    [Fact]
    public void Cross_dimension_units_are_disclosed_not_guessed()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 4.0, "each", source: "manual", date: Today);

        var est = Svc(db).EstimatePlanCost([RecipeWith("A", 4, ("rice", 300, "g"))]);

        var row = Assert.Single(est.Ingredients);
        Assert.Equal(PlanCostService.StatusUnitMismatch, row.Status); // each -> g is not convertible
        Assert.Null(row.Cost);
        Assert.Equal(0.0, est.CoverageRatio);
    }

    [Fact]
    public void Flyer_quote_beats_recent_store_price()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 6.0, "kg", source: "manual", date: Today);
        SeedActiveFlyerDeal(db, store, "rice", "4.00", itemId: rice, unit: "kg");

        var est = Svc(db).EstimatePlanCost([RecipeWith("A", 4, ("rice", 1000, "g"))]);

        var row = Assert.Single(est.Ingredients);
        Assert.Equal(4.0, row.UnitPrice!.Value, 2);
        Assert.Equal(4.0, row.Cost!.Value, 2); // 1 kg at the flyer price, not the $6 shelf price
    }

    [Fact]
    public void Likely_have_items_reduce_incremental_but_not_priced_total()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;

        // rice: receipt cadence 10d, last bought 2d ago -> likely have.
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        foreach (var d in new[] { 32, 22, 12, 2 })
        {
            var rid = AddReceipt(db, store, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, rice, store, 5.0, "kg", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
        var beef = ItemsRepo.CreateItem(db.Conn, "beef sirloin").Id;
        PricesRepo.AddPricePoint(db.Conn, beef, store, 20.0, "kg", source: "manual", date: Today);

        var est = Svc(db).EstimatePlanCost([RecipeWith("A", 4, ("beef sirloin", 500, "g"), ("rice", 400, "g"))]);

        Assert.Equal(12.0, est.PricedTotal, 2);      // 0.5x20 + 0.4x5
        Assert.Equal(10.0, est.IncrementalTotal, 2); // rice's $2 discounted (pantry)
        Assert.True(est.Ingredients.Single(i => i.Name == "rice").LikelyHave);
    }

    [Fact]
    public void Unmapped_and_unpriced_ingredients_lower_coverage_honestly()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 5.0, "kg", source: "manual", date: Today);
        ItemsRepo.CreateItem(db.Conn, "saffron"); // known item, NO price anywhere

        var est = Svc(db).EstimatePlanCost([RecipeWith("A", 4,
            ("rice", 300, "g"), ("saffron", 1, "g"), ("dragon fruit puree", 100, "g"))]);

        Assert.Equal(3, est.TotalCount);
        Assert.Equal(1, est.PricedCount);
        Assert.Equal(1.0 / 3.0, est.CoverageRatio, 2);
        Assert.True(est.CoverageRatio < PlanCostService.MinCoverageForBudget); // budget-ineligible, disclosed
        Assert.Equal(PlanCostService.StatusUnpriced, est.Ingredients.Single(i => i.Name == "saffron").Status);
        Assert.Equal(PlanCostService.StatusUnmapped,
            est.Ingredients.Single(i => i.Name == "dragon fruit puree").Status);
    }

    [Fact]
    public void Recipes_without_details_contribute_no_quantity_rows()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Loblaws");
        var legacy = new Recipe(2, "Legacy", 4, ["rice"], [], []); // no Details

        var est = Svc(db).EstimatePlanCost([legacy]);

        Assert.Equal(PlanCostService.StatusUnmapped, Assert.Single(est.Ingredients).Status); // no item either
        Assert.Equal(0.0, est.PricedTotal);
    }

    [Fact]
    public void Estimating_never_writes_aliases_or_items()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var rice = ItemsRepo.CreateItem(db.Conn, "rice").Id;
        PricesRepo.AddPricePoint(db.Conn, rice, store, 5.0, "kg", source: "manual", date: Today);

        var mapper = new IngredientMappingService(db.Factory);
        var svc = new PlanCostService(db.Factory, mapper);
        var aliasesBefore = Count(db.Conn, "item_aliases");

        // "Rice." normalizes to "rice" and fuzzy-scores 1.0 — above the 0.90 learn threshold, so a
        // learning mapper would buffer an alias. The read-only path must buffer nothing.
        svc.EstimatePlanCost([RecipeWith("A", 4, ("Rice.", 300, "g"))]);
        mapper.FlushLearnedAliases(); // must be a no-op

        Assert.Equal(aliasesBefore, Count(db.Conn, "item_aliases"));
        Assert.Equal(1L, Count(db.Conn, "items")); // nothing force-created either
    }
}
