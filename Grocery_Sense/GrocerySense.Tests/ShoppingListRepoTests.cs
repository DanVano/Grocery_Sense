using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class ShoppingListRepoTests
{
    [Fact]
    public void Add_then_get_round_trips_with_coalesced_defaults()
    {
        using var db = new TempDb();
        var id = ShoppingListRepo.AddItem(db.Conn, "Bananas", quantity: 2, unit: "bunch", category: "produce");

        var row = ShoppingListRepo.GetItem(db.Conn, id)!;
        Assert.Equal("Bananas", row.DisplayName);
        Assert.Equal(2, row.Quantity);
        Assert.Equal("bunch", row.Unit);
        Assert.Equal("produce", row.Category);
        Assert.Equal("", row.Notes);     // coalesced
        Assert.Null(row.AddedBy);        // empty -> null
        Assert.True(row.IsActive);
        Assert.False(row.IsCheckedOff);
    }

    [Fact]
    public void ListActive_excludes_checked_off_and_deleted()
    {
        using var db = new TempDb();
        var keep = ShoppingListRepo.AddItem(db.Conn, "Keep");
        var check = ShoppingListRepo.AddItem(db.Conn, "Checked");
        var del = ShoppingListRepo.AddItem(db.Conn, "Deleted");
        ShoppingListRepo.SetCheckedOff(db.Conn, check, checkedOff: true);
        ShoppingListRepo.DeleteItem(db.Conn, del);

        var active = ShoppingListRepo.ListActiveItems(db.Conn);
        Assert.Single(active);
        Assert.Equal(keep, active[0].Id);

        Assert.Equal(2, ShoppingListRepo.ListActiveItems(db.Conn, includeCheckedOff: true).Count);
        Assert.Equal(2, ShoppingListRepo.ListAllItems(db.Conn).Count); // deleted excluded, checked kept
    }

    [Fact]
    public void Planned_store_assignment_by_item_id_and_clear()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Yogurt");
        var store = StoresRepo.CreateStore(db.Conn, "Costco");
        var rowId = ShoppingListRepo.AddItem(db.Conn, "Yogurt", itemId: item.Id);

        var updated = ShoppingListRepo.BulkSetPlannedStoreIdsByItemId(
            db.Conn, new[] { (item.Id, (int?)store.Id) });
        Assert.Equal(1, updated);
        Assert.Equal(store.Id, ShoppingListRepo.GetItem(db.Conn, rowId)!.PlannedStoreId);

        ShoppingListRepo.ClearPlannedStoreIdsForActiveItems(db.Conn);
        Assert.Null(ShoppingListRepo.GetItem(db.Conn, rowId)!.PlannedStoreId);
    }
}
