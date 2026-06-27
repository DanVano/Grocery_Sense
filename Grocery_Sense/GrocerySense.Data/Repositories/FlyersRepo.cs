using GrocerySense.Domain;
using Microsoft.Data.Sqlite;

namespace GrocerySense.Data.Repositories;

// Port of reference-python/src/Grocery_Sense/data/repositories/flyers_repo.py
// Class-based; self-creates flyer_batches/assets/raw_json/deals in Python. Fold that DDL into the
// Database migration ledger during the port. NOTE: the Python repo applies preference filtering
// inside list_*_deals — ARCHITECTURE.md flags this as a layering leak. In C#, move preference
// filtering up into a service (PreferencesService) and keep this repo CRUD-only.
public sealed class FlyersRepo
{
    public int UpsertStore(string name) => throw new NotImplementedException();

    public IReadOnlyList<StoreRow> ListStores() => throw new NotImplementedException();

    public int CreateFlyerBatch(int storeId, string? validFrom, string? validTo, string? sourceType = null,
        string? sourceRef = null, string? note = null, string status = "active") => throw new NotImplementedException();

    public void SetBatchStatus(int flyerId, string status) => throw new NotImplementedException();

    public int AddAsset(int flyerId, string assetType, string path, string? sha256 = null) => throw new NotImplementedException();

    public int AddRawJson(int flyerId, string rawJson, string? sha256 = null) => throw new NotImplementedException();

    public int AddDeals(IReadOnlyList<Dictionary<string, object?>> deals) => throw new NotImplementedException();

    public int InsertDeals(int batchId, int storeId, IReadOnlyList<Dictionary<string, object?>> deals) => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> ListActiveDeals(int? storeId = null,
        IReadOnlyList<int>? storeIds = null, string? onDate = null, int limit = 5000) => throw new NotImplementedException();

    public IReadOnlyList<Dictionary<string, object?>> ListDealsForFlyer(int flyerId, int limit = 5000) => throw new NotImplementedException();
}
