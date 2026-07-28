using GrocerySense.Core;

namespace GrocerySense.App.Services;

// One-tap receipt capture behind the global Scan FAB: camera → bounded copy → single-scan workflow. Copying
// goes through ReceiptFilePolicy (the same path the Receipts page uses, so the FAB can't become a laxer way
// in), and the ingest + price-alert sequence is owned by ScanIngestService so the FAB and the page can't
// drift apart.
public sealed class QuickScanService
{
    private readonly ScanIngestService _scan;

    public QuickScanService(ScanIngestService scan) => _scan = scan;

    // Captured is false when the device has no camera or the user backed out of it; Error is set only for a
    // genuine capture/ingest failure. AlertError is set when the receipt imported but the price-alert refresh
    // failed — the import stands and the image is kept; the FAB just surfaces a warning. A caller must report
    // Error even when Captured is false.
    public sealed record QuickScanOutcome(
        bool Captured, long? ReceiptId, int AlertsOpened, bool WasDuplicate, string? Error, string? AlertError = null);

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

            // replaceExisting: false — the quick path never silently overwrites an existing receipt.
            var outcome = await _scan.IngestScannedFileAsync(copyPath, false, ct);

            if (outcome.Ingest.WasDuplicate)
            {
                TryDelete(copyPath); // the original receipt keeps its own image
                return new(true, outcome.Ingest.ReceiptId, 0, true, null);
            }

            // Imported — keep the copy even when the alert pass failed (the receipt is committed).
            return new(true, outcome.Ingest.ReceiptId, outcome.AlertsOpened, false, null, outcome.AlertError);
        }
        catch (Exception ex)
        {
            TryDelete(copyPath); // ingest threw → the receipt never committed → drop the unreferenced copy
            return new(true, null, 0, false, ex.Message);
        }
    }

    private static void TryDelete(string? path) => ReceiptFilePolicy.TryDelete(path);
}
