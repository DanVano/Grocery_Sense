namespace GrocerySense.Core;

// Mobile redesign of reference-python/.../services/flyer_sync_scheduler.py. Python armed a long-lived
// threading.Timer; iOS/Android restrict background execution, so v1 instead syncs on app-resume + a manual
// button (locked decision 2026-06-24). The App project calls CheckOnResumeAsync from its lifecycle hook and
// RequestSyncAsync from the Sync button; there is no background timer to start/stop.
//
// Single-flight: a resume tick and a button press can race; the in-flight run already covers the work, so a
// second caller is dropped (returns "busy") rather than double-inserting flyer batches.
public sealed class FlyerSyncScheduler
{
    private readonly FlyerSyncService _sync;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Fired after a sync that actually ran (skipped/too-soon syncs do not fire it), so the UI can kick the
    // price-drop alert check — the C# analog of Python's on_sync_complete callback.
    public event Action<FlyerSyncResult>? SyncCompleted;

    public FlyerSyncScheduler(FlyerSyncService sync) => _sync = sync;

    // Call from the app's resume lifecycle event. Runs a sync only if the throttle says one is due.
    public Task<FlyerSyncResult> CheckOnResumeAsync(CancellationToken ct = default) => RunGuardedAsync(force: false, ct);

    // Call from the manual "Sync Flyers" button. Bypasses the throttle.
    public Task<FlyerSyncResult> RequestSyncAsync(CancellationToken ct = default) => RunGuardedAsync(force: true, ct);

    private async Task<FlyerSyncResult> RunGuardedAsync(bool force, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new FlyerSyncResult(0, 0, "busy", Array.Empty<string>());
        try
        {
            var result = await _sync.RunSyncAsync(force, ct);
            if (result.Ran && SyncCompleted is { } handlers)
            {
                // Disclosed degrade: the sync succeeded; a failed post-sync hook must not be reported as a
                // sync failure, but must not vanish either. Deals.razor renders Errors. The scheduler stays
                // generic — it doesn't know what subscribers do. Single-subscriber assumption: a multicast
                // invoke stops at the first throwing handler; only the DI alert-refresh hook subscribes today,
                // so no per-handler dispatch until a second subscriber exists.
                try { handlers(result); }
                catch (Exception ex)
                {
                    result = result with { Errors = [.. result.Errors, $"Post-sync hook failed: {ex.Message}"] };
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
