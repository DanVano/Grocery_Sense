using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using static GrocerySense.Tests.TestSeed;

namespace GrocerySense.Tests;

public sealed class MealSuggestionMarginalCostTests
{
    

    // Receipt purchases spaced 10 days apart ending `lastDaysAgo` ago -> cadence 10d + a price baseline.
    private static void SeedItemHistory(TempDb db, int storeId, string name, double price, int lastDaysAgo)
    {
        var item = ItemsRepo.CreateItem(db.Conn, name).Id;
        foreach (var d in new[] { lastDaysAgo + 30, lastDaysAgo + 20, lastDaysAgo + 10, lastDaysAgo })
        {
            var rid = AddReceipt(db, storeId, DaysAgo(d));
            PricesRepo.AddPricePoint(db.Conn, item, storeId, price, "each", quantity: 1.0,
                source: "receipt", date: DaysAgo(d), receiptId: rid);
        }
    }

    private static MealSuggestionService Service(TempDb db) => new(
        new RecipeEngine(Fixtures.RecipesSamplePath), new PriceHistoryService(db.Factory), db.Factory);

    [Fact]
    public void Likely_have_ingredient_reduces_marginal_cost()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        SeedItemHistory(db, store, "rice", 4.0, lastDaysAgo: 2);   // 2 < 0.75*10 -> likely have
        SeedItemHistory(db, store, "beef", 10.0, lastDaysAgo: 9);  // 9 >= 0.75*10 -> NOT likely have

        var stirFry = Service(db).SuggestMealsForWeek(maxRecipes: 20)
            .First(s => s.Recipe.Name == "Beef Stir Fry"); // ingredients: beef, broccoli, soy sauce, garlic, rice

        Assert.NotNull(stirFry.CostTotal);                       // rice + beef priced = 14.0
        Assert.Equal(14.0, stirFry.CostTotal!.Value, 2);
        Assert.Equal(10.0, stirFry.MarginalCostTotal!.Value, 2); // rice's 4.0 discounted
        Assert.Equal(4, stirFry.NewIngredientCount);             // 5 ingredients - rice
        Assert.Contains("rice", stirFry.LikelyHaveIngredients!);
        Assert.Contains(stirFry.Reasons, r => r.Contains("likely already have"));
    }

    [Fact]
    public void Without_factory_marginal_fields_stay_null()
    {
        var svc = new MealSuggestionService(new RecipeEngine(Fixtures.RecipesSamplePath));
        var meal = svc.SuggestMealsForWeek(maxRecipes: 1)[0];
        Assert.Null(meal.MarginalCostTotal);
        Assert.Null(meal.NewIngredientCount);
        Assert.Null(meal.LikelyHaveIngredients);
    }

    [Fact]
    public void Suggesting_writes_nothing_to_the_db()
    {
        using var db = new TempDb();
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        SeedItemHistory(db, store, "rice", 4.0, lastDaysAgo: 2);


        var aliasesBefore = Count(db.Conn, "item_aliases");
        var itemsBefore = Count(db.Conn, "items");
        Service(db).SuggestMealsForWeek(maxRecipes: 20);
        Assert.Equal(aliasesBefore, Count(db.Conn, "item_aliases")); // regression guard: browsing must never write
        Assert.Equal(itemsBefore, Count(db.Conn, "items"));
    }
}
