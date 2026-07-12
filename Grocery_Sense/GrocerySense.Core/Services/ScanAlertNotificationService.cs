using GrocerySense.Core.Abstractions;

namespace GrocerySense.Core;

// After a single receipt scan, opens receipt-scoped price-drop alerts and (if any opened) fires ONE summary
// local notification (A7). Receipt-scoped so backfilled recent-dated receipts can't inflate this scan's count
// (V2_FOLLOWUPS §4 misattribution landmine). Notifier failures are isolated — they never break ingest, and
// Opened is reported regardless of whether the notification actually showed (deny-path in-app visibility).
public sealed class ScanAlertNotificationService
{
    private readonly PriceDropAlertService _alerts;
    private readonly ILocalNotifier _notifier;

    public ScanAlertNotificationService(PriceDropAlertService alerts, ILocalNotifier notifier)
    {
        _alerts = alerts;
        _notifier = notifier;
    }

    public async Task<ScanAlertResult> AfterSingleScanAsync(long receiptId, CancellationToken ct = default)
    {
        var opened = _alerts.ScanReceipt(receiptId);
        if (opened <= 0) return new ScanAlertResult(0, Notified: false);

        var body = opened == 1
            ? "1 new price alert from your receipt"
            : $"{opened} new price alerts from your receipt";

        bool notified;
        try
        {
            notified = await _notifier.ShowAsync("Grocery Sense", body, ct);
        }
        catch
        {
            notified = false; // never let a notifier fault break the scan flow; the in-app line still shows Opened.
        }
        return new ScanAlertResult(opened, notified);
    }
}
