using GrocerySense.Data;
using GrocerySense.Data.Repositories;

namespace GrocerySense.Core;

// Zero-effort restock draft: staples (same definition as PriceDropAlertService — >=3 receipts or >=4
// lines in 90d) whose last receipt purchase is at least one full typical interval ago, and which are
// not already on the active list. Read-only: the UI adds rows via ShoppingListService on tap.
// No cadence => no suggestion (never guess). Dedupe checks item_id AND normalized display name,
// because manually typed rows can miss their item mapping and land with no item_id.
public sealed class StapleRestockService
{
    private readonly SqliteConnectionFactory _factory;

    public StapleRestockService(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<RestockSuggestion> GetSuggestions(int maxItems = 15)
    {
        using var conn = _factory.Open();
        var staples = PricesRepo.ListStapleItemIds(conn); // defaults = the alert staple thresholds
        if (staples.Count == 0) return Array.Empty<RestockSuggestion>();

        var listRows = ShoppingListRepo.ListActiveItems(conn, storeId: null, includeCheckedOff: true);
        var onListIds = listRows.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).ToHashSet();
        var onListNames = listRows.Select(r => Normalize(r.DisplayName)).Where(n => n.Length > 0).ToHashSet();

        var ids = staples.Select(s => s.ItemId).Where(id => !onListIds.Contains(id)).Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<RestockSuggestion>();

        var cadence = PricesRepo.GetPurchaseCadenceBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);
        var last = PricesRepo.GetLastReceiptPurchaseBatch(conn, ids, PriceDropAlertService.UsualLookbackDays);
        var items = ItemsRepo.GetItemsByIds(conn, ids);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new List<RestockSuggestion>();
        foreach (var id in ids)
        {
            var (interval, _) = cadence.GetValueOrDefault(id, (null, null));
            if (interval is not > 0) continue;
            if (!last.TryGetValue(id, out var lastIso) || !DateOnly.TryParse(lastIso, out var lastDate)) continue;
            if (!items.TryGetValue(id, out var item)) continue;
            if (onListNames.Contains(Normalize(item.CanonicalName))) continue; // typed-by-hand duplicate

            var daysSince = today.DayNumber - lastDate.DayNumber;
            if (daysSince < interval.Value) continue; // not due yet

            var intervalDays = (int)Math.Round(interval.Value, MidpointRounding.AwayFromZero);
            result.Add(new RestockSuggestion(id, item.CanonicalName, daysSince, intervalDays));
        }

        // Most overdue (relative to its own cadence) first.
        return result.OrderByDescending(r => (double)r.DaysSinceLast / Math.Max(1, r.IntervalDays))
            .Take(maxItems).ToList();
    }

    private static string Normalize(string name) =>
        string.Join(" ", (name ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
