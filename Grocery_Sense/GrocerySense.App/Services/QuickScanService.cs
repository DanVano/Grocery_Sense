using GrocerySense.Core;

namespace GrocerySense.App.Services;

// One-tap receipt capture behind the global Scan FAB: camera → bounded copy → ingest → price-alert
// scan. Copies through ReceiptFilePolicy, the same path the Receipts page uses, so the FAB cannot
// become a laxer way in than the page.
public sealed class QuickScanService
{
    private readonly ReceiptIngestionService _ingest;
    private readonly ScanAlertNotificationService _scanAlerts;

    public QuickScanService(ReceiptIngestionService ingest, ScanAlertNotificationService scanAlerts)
    {
        _ingest = ingest;
        _scanAlerts = scanAlerts;
    }

    // Captured is false when the device has no camera or the user backed out of it; Error is set only
    // for a genuine failure. A caller must report Error even when Captured is false.
    public sealed record QuickScanOutcome(
        bool Captured, long? ReceiptId, int AlertsOpened, bool WasDuplicate, string? Error);

    public async Task<QuickScanOutcome> CaptureAndIngestAsync(CancellationToken ct = default)
    {
        if (!MediaPicker.Default.IsCaptureSupported)
            return new(false, null, 0, false, "This device can't capture photos. Import from the Receipts page instead.");

        FileResult? pick;
        try { pick = await MediaPicker.Default.CapturePhotoAsync(); }
        catch (Exception ex) { return new(false, null, 0, false, ex.Message); }
        if (pick is null) return new(false, null, 0, false, null); // user backed out of the camera

        string? copyPath = null;
        try
        {
            copyPath = await ReceiptFilePolicy.CopyPickAsync(pick, ct);

            // replaceDuplicates: false — the quick path never silently overwrites an existing receipt.
            var outcome = await _ingest.IngestReceiptFileAsync(copyPath, false, ct);

            if (outcome.Error is { } error)
            {
                TryDelete(copyPath);
                return new(true, null, 0, false, error);
            }
            if (outcome.WasDuplicate)
            {
                TryDelete(copyPath); // the original receipt keeps its own image
                return new(true, outcome.ReceiptId, 0, true, null);
            }

            var opened = 0;
            if (outcome.ReceiptId is { } receiptId)
                opened = (await _scanAlerts.AfterSingleScanAsync(receiptId, ct)).Opened;
            return new(true, outcome.ReceiptId, opened, false, null);
        }
        catch (Exception ex)
        {
            TryDelete(copyPath);
            return new(true, null, 0, false, ex.Message);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null || !PathSafety.IsUnderDirectory(ReceiptFilePolicy.ReceiptsDir(), path)) return;
        try { File.Delete(path); } catch { /* best-effort cleanup of an unreferenced copy */ }
    }
}
