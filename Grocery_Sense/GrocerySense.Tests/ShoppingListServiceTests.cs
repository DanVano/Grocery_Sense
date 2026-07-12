using GrocerySense.Core;
using GrocerySense.Data.Repositories;
using GrocerySense.Domain;
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

    [Fact]
    public void AddDealToList_maps_to_canonical_name_and_keeps_item_link()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "2% Milk").Id;
        var deal = MakeDeal(store, title: "MILK 2L", priceText: "2/$5", itemId: item);

        var rowId = svc.AddDealToList(deal);

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal(rowId, row.Id);
        Assert.Equal("2% Milk", row.DisplayName);   // canonical name, not the flyer title
        Assert.Equal(item, row.ItemId);
        Assert.Equal(store, row.PlannedStoreId);
        Assert.Equal(1, row.Quantity);              // never inferred from the promo phrase
        Assert.Contains("From deal: 2/$5", row.Notes);
        Assert.DoesNotContain("not price-tracked", row.Notes);
    }

    [Fact]
    public void AddDealToList_unmapped_deal_is_a_disclosed_text_row()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory);
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var deal = MakeDeal(store, title: "Fresh Basil", priceText: "$1.99", itemId: null);

        svc.AddDealToList(deal);

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal("Fresh Basil", row.DisplayName);
        Assert.Null(row.ItemId);
        Assert.Equal(1, row.Quantity);
        Assert.Contains("not price-tracked", row.Notes);
    }

    private static FlyerDeal MakeDeal(int storeId, string title, string priceText, int? itemId) =>
        new(Id: 0, FlyerId: 0, AssetId: null, StoreId: storeId, PageIndex: null,
            Title: title, Description: null, PriceText: priceText,
            DealQty: null, DealTotal: null, UnitPrice: null, Unit: null,
            NormUnitPrice: null, NormUnit: null, NormNote: null,
            ItemId: itemId, MappingConfidence: null, Confidence: null, CreatedAt: null);
}
