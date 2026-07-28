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
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));

        var created = svc.AddItemsFromText("Milk, Eggs, , Bread ");

        Assert.Equal(new[] { "Milk", "Eggs", "Bread" }, created.Select(r => r.DisplayName));
        Assert.Equal(3, svc.GetActiveItems().Count);
    }

    // A watch hit lands mapped, planned at the hit's store, with the price disclosed in the note (F04).
    [Fact]
    public void AddWatchHitToList_adds_mapped_row_at_the_hit_store_with_price_note()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Butter").Id;
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var hit = new WatchlistHit(WatchId: 1, ItemId: item, ItemName: "Butter", TargetPrice: 4.00,
            BestPrice: 3.49, StoreId: store, StoreName: "Loblaws", Source: "flyer",
            UsualPrice: 5.00, PctBelowUsual: 30.0, HitReason: "target");

        var rowId = svc.AddWatchHitToList(hit);

        var row = ShoppingListRepo.GetItem(db.Conn, rowId)!;
        Assert.Equal(item, row.ItemId);
        Assert.Equal(store, row.PlannedStoreId);
        Assert.Contains("$3.49", row.Notes);
        Assert.Contains("Loblaws", row.Notes);
    }

    // Manual adds map to canonical items (match-only) so they reach the optimizer/Shop Mode intel.
    [Fact]
    public void AddSingleItem_maps_known_name_to_item_id()
    {
        using var db = new TempDb();
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));

        var rowId = svc.AddSingleItem("Milk");

        Assert.Equal(item, ShoppingListRepo.GetItem(db.Conn, rowId)!.ItemId);
    }

    [Fact]
    public void AddSingleItem_keeps_unknown_name_unmapped()
    {
        using var db = new TempDb();
        ItemsRepo.CreateItem(db.Conn, "Milk");
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));

        var rowId = svc.AddSingleItem("Zorbulon Crisps"); // no match -> stays NULL, never force-created

        Assert.Null(ShoppingListRepo.GetItem(db.Conn, rowId)!.ItemId);
    }

    [Fact]
    public void AddItemsFromText_maps_each_entry_independently()
    {
        using var db = new TempDb();
        var milk = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        var eggs = ItemsRepo.CreateItem(db.Conn, "Eggs").Id;
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));

        var created = svc.AddItemsFromText("Milk, Zorbulon Crisps, Eggs");

        Assert.Equal(new int?[] { milk, null, eggs }, created.Select(r => r.ItemId));
    }

    [Fact]
    public void CheckOff_and_SoftDelete_drop_from_active()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
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
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var deal = MakeDeal(store, title: "Fresh Basil", priceText: "$1.99", itemId: null);

        svc.AddDealToList(deal);

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal("Fresh Basil", row.DisplayName);
        Assert.Null(row.ItemId);
        Assert.Equal(1, row.Quantity);
        Assert.Contains("not price-tracked", row.Notes);
    }

    [Fact]
    public void AddAlertToList_carries_suggested_quantity_and_note()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Milk").Id;
        var alert = MakeAlert(item, "Milk", store, suggestedQty: 2, note: "You buy this ~every 21 days; buy 2");

        svc.AddAlertToList(alert);

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal("Milk", row.DisplayName);
        Assert.Equal(item, row.ItemId);
        Assert.Equal(store, row.PlannedStoreId);
        Assert.Equal(2, row.Quantity);
        Assert.Equal("You buy this ~every 21 days; buy 2", row.Notes);
    }

    [Fact]
    public void AddAlertToList_without_suggested_qty_falls_back_to_one()
    {
        using var db = new TempDb();
        var svc = new ShoppingListService(db.Factory, new IngredientMappingService(db.Factory));
        var store = StoresRepo.CreateStore(db.Conn, "Loblaws").Id;
        var item = ItemsRepo.CreateItem(db.Conn, "Eggs").Id;
        var alert = MakeAlert(item, "Eggs", store, suggestedQty: null, note: null);

        svc.AddAlertToList(alert);

        var row = Assert.Single(svc.GetActiveItems());
        Assert.Equal(1, row.Quantity);
        Assert.Equal("From price alert", row.Notes);
    }

    private static FlyerDeal MakeDeal(int storeId, string title, string priceText, int? itemId) =>
        new(Id: 0, FlyerId: 0, AssetId: null, StoreId: storeId, PageIndex: null,
            Title: title, Description: null, PriceText: priceText,
            DealQty: null, DealTotal: null, UnitPrice: null, Unit: null,
            NormUnitPrice: null, NormUnit: null, NormNote: null,
            ItemId: itemId, MappingConfidence: null, Confidence: null, CreatedAt: null);

    private static PriceDropAlert MakeAlert(int itemId, string itemName, int storeId, double? suggestedQty, string? note) =>
        new(ItemId: itemId, ItemName: itemName, StoreId: storeId, StoreName: "Loblaws",
            CurrentPrice: 5.0, UsualPrice: 10.0, PctBelowUsual: 50.0, SixMonthLow: null, PctAboveLow: null,
            AlertKind: "stock_up", IsStaple: true, ReceiptSamples: 4, Basis: "median", Source: "receipt",
            LastSeenAtOrBelow: null, Notes: "", SuggestedQty: suggestedQty, SuggestedQtyNote: note);
}
