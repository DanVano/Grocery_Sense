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

    // The shared-batch workflow (architecture-review deepening): claim the pending batch atomically, run
    // every copy through the single-scan workflow above, decide which copies die (duplicate/conflict copies
    // immediately; on failure or cancel, the failing + remaining ones — a committed receipt keeps its image,
    // an uncommitted copy must not linger unowned), and ALWAYS release the batch. This orchestration used to
    // live in Receipts.razor — the one place the test project cannot reach. File deletion stays delegated:
    // the guarded file policy lives in the App head, Core only decides WHICH paths are handed to it.
    // Returns null when no batch was claimable (nothing pending, or another import holds it).
    public async Task<SharedImportSummary?> ImportSharedBatchAsync(
        PendingSharedReceiptsService pending, bool replaceExisting, Action<string> deleteCopy,
        CancellationToken ct = default)
    {
        if (!pending.TryBeginImport(out var paths, out var claimedErrors)) return null;

        int imported = 0, duplicates = 0, conflicts = 0, alerts = 0, alertFailures = 0;
        var index = 0;
        try
        {
            for (; index < paths.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await IngestScannedFileAsync(paths[index], replaceExisting, ct);
                if (outcome.Ingest.ReplaceConflict) { conflicts++; deleteCopy(paths[index]); }
                else if (outcome.Ingest.WasDuplicate) { duplicates++; deleteCopy(paths[index]); }
                else
                {
                    imported++;
                    alerts += outcome.AlertsOpened;
                    if (outcome.AlertError is not null) alertFailures++;
                }
            }
            return new SharedImportSummary(imported, duplicates, conflicts, claimedErrors.Count,
                alerts, alertFailures, Cancelled: false, FailureMessage: null);
        }
        catch (OperationCanceledException)
        {
            for (var i = index; i < paths.Count; i++) deleteCopy(paths[i]);
            return new SharedImportSummary(imported, duplicates, conflicts, claimedErrors.Count,
                alerts, alertFailures, Cancelled: true, FailureMessage: null);
        }
        catch (Exception ex)
        {
            for (var i = index; i < paths.Count; i++) deleteCopy(paths[i]);
            return new SharedImportSummary(imported, duplicates, conflicts, claimedErrors.Count,
                alerts, alertFailures, Cancelled: false, FailureMessage: ex.Message);
        }
        finally
        {
            pending.CompleteImport();
        }
    }
}
