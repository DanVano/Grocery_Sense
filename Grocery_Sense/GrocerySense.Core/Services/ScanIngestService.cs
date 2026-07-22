namespace GrocerySense.Core;

// The single-scan workflow, owned once in Core instead of copied across the App's scan entry points (the
// Scan FAB, the Receipts page picker/camera, and shared-receipt import). Given an already-copied receipt
// file, it ingests then runs the single-scan price-alert pass, and returns a structured outcome the UI
// renders. File capture, the bounded copy, and cleanup stay in the App (they depend on the file policy).
//
// Single-scan only. Backfill (ReceiptIngestionService.ImportBatchAsync) must NOT route here — its receipts
// are recent-dated on purpose and would inflate this scan's alert count (V2_FOLLOWUPS §4 misattribution).
//
// Partial success is the point: an ingest failure THROWS (the receipt never committed, so the caller deletes
// the copy). The alert pass runs AFTER the receipt is durably committed, so its failure must never undo the
// import — it is caught and surfaced as AlertError, with the receipt imported and its image kept.
public sealed class ScanIngestService
{
    private readonly ReceiptIngestionService _ingest;
    private readonly ScanAlertNotificationService _scanAlerts;

    public ScanIngestService(ReceiptIngestionService ingest, ScanAlertNotificationService scanAlerts)
    {
        _ingest = ingest;
        _scanAlerts = scanAlerts;
    }

    public async Task<ScanIngestOutcome> IngestScannedFileAsync(
        string copyPath, bool replaceExisting, CancellationToken ct = default)
    {
        var outcome = await _ingest.IngestReceiptFileAsync(copyPath, replaceExisting, ct); // throws → propagates
        if (outcome.WasDuplicate || outcome.ReceiptId is not { } receiptId)
            return new ScanIngestOutcome(outcome, 0);

        try
        {
            var scan = await _scanAlerts.AfterSingleScanAsync(receiptId, ct);
            return new ScanIngestOutcome(outcome, scan.Opened);
        }
        catch (Exception ex)
        {
            // The receipt is already committed; a failed (or cancelled) alert pass must not roll that back or
            // delete its image. Surface the failure so the UI can say "imported, alert refresh failed".
            return new ScanIngestOutcome(outcome, 0, ex.Message);
        }
    }
}
