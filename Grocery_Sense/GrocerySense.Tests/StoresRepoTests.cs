using GrocerySense.Data.Repositories;
using Xunit;

namespace GrocerySense.Tests;

public sealed class StoresRepoTests
{
    [Fact]
    public void Create_then_get_round_trips_all_fields()
    {
        using var db = new TempDb();
        var created = StoresRepo.CreateStore(db.Conn, "No Frills", address: "1 Main St", city: "Calgary",
            postalCode: "T2X1A1", flippStoreId: "flipp-42", isFavorite: true, priority: 5, notes: "cheap");

        var got = StoresRepo.GetStoreById(db.Conn, created.Id);

        Assert.NotNull(got);
        Assert.Equal("No Frills", got!.Name);
        Assert.Equal("1 Main St", got.Address);
        Assert.Equal("Calgary", got.City);
        Assert.Equal("T2X1A1", got.PostalCode);
        Assert.Equal("flipp-42", got.FlippStoreId);
        Assert.True(got.IsFavorite);
        Assert.Equal(5, got.Priority);
        Assert.True(got.ShopHere);   // column default
        Assert.True(got.IsActive);   // column default
        Assert.Equal("cheap", got.Notes);
    }

    [Fact]
    public void List_excludes_archived_and_orders_by_priority()
    {
        using var db = new TempDb();
        StoresRepo.CreateStore(db.Conn, "Low", priority: 1);
        var high = StoresRepo.CreateStore(db.Conn, "High", priority: 9);
        var archived = StoresRepo.CreateStore(db.Conn, "Gone", priority: 5);
        StoresRepo.SetStoreActive(db.Conn, archived.Id, isActive: false);

        var active = StoresRepo.ListStores(db.Conn);
        Assert.Equal(2, active.Count);
        Assert.Equal(high.Id, active[0].Id); // priority DESC
        Assert.DoesNotContain(active, s => s.Id == archived.Id);

        Assert.Equal(3, StoresRepo.ListStores(db.Conn, includeArchived: true).Count);
    }

    [Fact]
    public void SetFavorite_and_ShopHere_persist()
    {
        using var db = new TempDb();
        var s = StoresRepo.CreateStore(db.Conn, "Save On");

        StoresRepo.SetStoreFavorite(db.Conn, s.Id, isFavorite: true, priority: 3);
        StoresRepo.SetStoreShopHere(db.Conn, s.Id, shopHere: false);

        var got = StoresRepo.GetStoreById(db.Conn, s.Id)!;
        Assert.True(got.IsFavorite);
        Assert.Equal(3, got.Priority);
        Assert.False(got.ShopHere);
    }

    [Fact]
    public void UpsertFromFlipp_creates_then_updates_existing()
    {
        using var db = new TempDb();
        var first = StoresRepo.UpsertStoreFromFlipp(db.Conn, "Sobeys", "flipp-7", city: "Edmonton");
        var second = StoresRepo.UpsertStoreFromFlipp(db.Conn, "Sobeys Urban", "flipp-7", city: "Calgary");

        Assert.Equal(first.Id, second.Id); // same flipp id -> same row
        var got = StoresRepo.GetStoreById(db.Conn, first.Id)!;
        Assert.Equal("Sobeys Urban", got.Name);
        Assert.Equal("Calgary", got.City);
        Assert.Single(StoresRepo.ListStores(db.Conn, includeArchived: true));
    }
}
