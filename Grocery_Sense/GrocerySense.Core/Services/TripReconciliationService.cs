using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// "Did the trip go as planned?" — diff a just-scanned receipt against the current shopping list:
// current flyer price below what was paid, unplanned spend, and planned-at-this-store items missing
// from the receipt. Read-only; money aggregates in C#.
// MVP scope (locked 2026-07-18, round 2): compares against CURRENT list/flyer state, so Reconcile
// REFUSES receipts older than RecentTripDays or future-dated (the UI also hides the button — this
// guards deep links and future callers). "Above usual" was cut: the receipt's own price rows already
// sit in the usual median and receipt lines carry no unit, so it can't be computed honestly here. The
// historical version (list snapshot, date-valid flyers, usual-excluding-this-receipt, unit
// normalization) is future work — see the plan doc.
public sealed class TripReconciliationService
{
    public const int RecentTripDays = 7;      // enforced here AND used by the UI to hide the button
    private const double PriceEpsilon = 0.01; // cents tolerance on flyer comparison

    private readonly SqliteConnectionFactory _factory;

    public TripReconciliationService(SqliteConnectionFactory factory) => _factory = factory;

    public TripReconciliation Reconcile(int receiptId)
    {
        using var conn = _factory.Open();
        var receipt = ReceiptsRepo.GetReceipt(conn, receiptId)
            ?? throw new InvalidOperationException($"Receipt {receiptId} not found.");

        if (!DateOnly.TryParse(receipt.PurchaseDate, out var purchased))
            throw new InvalidOperationException($"Receipt {receiptId} has no parseable purchase date.");
        var ageDays = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - purchased.DayNumber;
        if (ageDays < 0 || ageDays > RecentTripDays)
            throw new InvalidOperationException(
                $"Trip check compares against the CURRENT list and flyers — only valid within {RecentTripDays} day(s) of the trip.");

        var lines = ReceiptsRepo.ListReceiptLineItems(conn, receiptId);
        var listRows = ShoppingListRepo.ListActiveItems(conn, storeId: null, includeCheckedOff: true);

        var plannedItemIds = listRows.Where(r => r.ItemId is not null)
            .Select(r => r.ItemId!.Value).ToHashSet();
        var mapped = lines.Where(l => l.ItemId is not null).ToList();
        var unmappedCount = lines.Count - mapped.Count;

        var lineItemIds = mapped.Select(l => l.ItemId!.Value).Distinct().ToList();
        var flyer = PricesRepo.GetActiveFlyerPricesBatch(conn, lineItemIds, new[] { receipt.StoreId });
        var names = ItemsRepo.GetItemsByIds(conn, lineItemIds);

        var flags = new List<TripLineFlag>();
        var unplannedItemIds = new HashSet<int>(); // distinct items — two lines of one item count once
        var unplannedTotal = 0m;                   // ...but every line's money is summed
        var unitSkipped = 0;

        foreach (var line in mapped)
        {
            var id = line.ItemId!.Value;
            var name = names.TryGetValue(id, out var item) ? item.CanonicalName : line.CanonicalName;

            if (!plannedItemIds.Contains(id))
            {
                unplannedItemIds.Add(id);
                unplannedTotal += line.LineTotal
                    ?? (line.UnitPrice ?? 0m) * (decimal)(line.Quantity is > 0 ? line.Quantity.Value : 1.0);
            }

            if (line.UnitPrice is not decimal paidDec) continue;
            var paid = (double)paidDec;

            if (!flyer.TryGetValue((id, receipt.StoreId), out var quote)) continue;

            // Receipt lines carry no unit; a per-weight/volume flyer quote can't be honestly compared.
            var unit = (quote.Unit ?? "").Trim().ToLowerInvariant();
            if (unit is not ("" or "each" or "ea")) { unitSkipped++; continue; }

            if (paid > quote.UnitPrice + PriceEpsilon)
                flags.Add(new TripLineFlag(name, "flyer_below_paid", paidDec, quote.UnitPrice,
                    $"Current flyer shows ${quote.UnitPrice:0.00} — you paid ${paidDec:0.00}. Check the receipt."));
        }

        // Distinct planned items actually bought — NOT a per-line count (two lines of one item = 1 match).
        var receiptItemIds = mapped.Select(l => l.ItemId!.Value).ToHashSet();
        var matchedPlanned = receiptItemIds.Intersect(plannedItemIds).Count();
        var notBought = listRows
            .Where(r => r.ItemId is int id && r.PlannedStoreId == receipt.StoreId && !receiptItemIds.Contains(id))
            .Select(r => r.DisplayName).Distinct().ToList();

        var notes = new List<string>();
        if (unmappedCount > 0) notes.Add($"{unmappedCount} receipt line(s) had no item mapping and weren't checked.");
        if (unitSkipped > 0) notes.Add($"{unitSkipped} line(s) skipped: flyer price is per weight/volume unit, receipt lines carry none.");
        notes.Add("Compared against the current list and currently-active flyer prices — run right after the trip.");

        return new TripReconciliation(receiptId, receipt.StoreName, receipt.PurchaseDate,
            matchedPlanned, unplannedItemIds.Count, unplannedTotal, flags, notBought, string.Join(" ", notes));
    }
}
