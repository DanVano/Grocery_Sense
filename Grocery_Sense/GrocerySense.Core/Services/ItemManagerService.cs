using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// The Item Manager's destructive mutations, with their transaction boundary owned in Core instead of a Razor
// @code block. The repo ops (ItemsAdminRepo.MergeItems / CorrectLineMapping) require a caller-owned
// transaction because they touch many tables; this is that owner. Reads (search/rename) stay direct in the
// UI — only these multi-table writes earn the seam, and only they become unit-testable off-device.
public sealed class ItemManagerService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IngredientMappingService _mapper;

    public ItemManagerService(SqliteConnectionFactory factory, IngredientMappingService mapper)
    {
        _factory = factory;
        _mapper = mapper;
    }

    // Merge source -> target in one transaction; the source name is always kept as an alias (the UI promises
    // it and no caller varies it, so the repo's flag is not exposed here). Merge deletes/repoints the source
    // item id, so the mapper's cached fuzzy-choice list is dropped afterwards — a later scan this session must
    // not fuzzy-match the now-deleted id and abort ingest on an FK failure. That invariant used to live in a
    // Razor comment where any caller could forget it; it belongs with the merge.
    public void MergeItems(int targetItemId, int sourceItemId)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        ItemsAdminRepo.MergeItems(conn, tx, targetItemId, sourceItemId);
        tx.Commit();
        _mapper.InvalidateChoices();
    }

    // Re-point one mis-mapped receipt line (and the price row it produced) to newItemId, and learn the alias.
    // receipt_id / description / old item_id are loaded from the line row by the repo — the caller supplies
    // only the line and the corrected item.
    public void CorrectLineMapping(int lineItemId, int newItemId)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();
        ItemsAdminRepo.CorrectLineMapping(conn, tx, lineItemId, newItemId);
        tx.Commit();
    }
}
