using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ShoppingListServiceTests
{
    [Fact]
    public void AddItemsFromText_splits_and_skips_blanks()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);

        var created = svc.AddItemsFromText("Milk, Eggs, , Bread ");

        Assert.Equal(new[] { "Milk", "Eggs", "Bread" }, created.Select(r => r.DisplayName));
        Assert.Equal(3, svc.GetActiveItems().Count);
    }

    [Fact]
    public void CheckOff_and_SoftDelete_drop_from_active()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);
        var keep = svc.AddSingleItem("Milk");
        var check = svc.AddSingleItem("Eggs");
        var del = svc.AddSingleItem("Bread");

        svc.CheckOffItem(check);
        svc.SoftDeleteItem(del);

        var active = svc.GetActiveItems();
        Assert.Equal(new[] { keep }, active.Select(r => r.Id));
        Assert.Equal(2, svc.GetActiveItems(includeCheckedOff: true).Count); // checked-off still present, deleted gone
    }

    [Fact]
    public void ClearAllCheckedOff_removes_only_checked()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);
        var keep = svc.AddSingleItem("Milk");
        var check = svc.AddSingleItem("Eggs");
        svc.CheckOffItem(check);

        svc.ClearAllCheckedOff();

        Assert.Equal(new[] { keep }, svc.GetActiveItems(includeCheckedOff: true).Select(r => r.Id));
    }

    [Fact]
    public void AddSingleItem_persists_quantity_unit_and_store()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id; // planned_store_id is a FK
        svc.AddSingleItem("Apples", quantity: 3, unit: "kg", plannedStoreId: store, notes: "fuji");

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal("Apples", row.DisplayName);
        Assert.Equal(3, row.Quantity);
        Assert.Equal("kg", row.Unit);
        Assert.Equal(store, row.PlannedStoreId);
        Assert.Equal("fuji", row.Notes);
    }
}
