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
    public void UpdateItemDetails_persists_quantity_unit_notes_and_touches_nothing_else()
    {
        using var db = new TempDb();
        var id = ShoppingListRepo.AddItem(db.Conn, "Rice", quantity: 1, unit: "bag", category: "pantry");
        ShoppingListRepo.SetPriority(db.Conn, id, "must_have");

        ShoppingListRepo.UpdateItemDetails(db.Conn, id, quantity: 2.5, unit: "kg", notes: "brown, not white");

        var row = ShoppingListRepo.GetItem(db.Conn, id)!;
        Assert.Equal(2.5, row.Quantity);
        Assert.Equal("kg", row.Unit);
        Assert.Equal("brown, not white", row.Notes);
        Assert.Equal("Rice", row.DisplayName);      // untouched
        Assert.Equal("must_have", row.Priority);    // untouched
    }

    [Fact]
    public void UpdateItemDetails_unknown_row_throws_instead_of_pretending_to_save()
    {
        using var db = new TempDb();
        Assert.Throws<ArgumentException>(() =>
            ShoppingListRepo.UpdateItemDetails(db.Conn, 9999, 1.0, "", ""));
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
    }

    [Fact]
    public void Priority_defaults_normal_and_can_be_set()
    {
        using var db = new TempDb();
        var id = ShoppingListRepo.AddItem(db.Conn, "Steak");
        Assert.Equal("normal", ShoppingListRepo.GetItem(db.Conn, id)!.Priority);

        ShoppingListRepo.SetPriority(db.Conn, id, "wait_for_sale");
        Assert.Equal("wait_for_sale", ShoppingListRepo.GetItem(db.Conn, id)!.Priority);

        // Unknown values normalize to 'normal' rather than persisting garbage.
        ShoppingListRepo.SetPriority(db.Conn, id, "bogus");
        Assert.Equal("normal", ShoppingListRepo.GetItem(db.Conn, id)!.Priority);
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
