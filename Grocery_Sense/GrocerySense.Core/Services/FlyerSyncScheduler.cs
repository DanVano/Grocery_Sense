namespace GrocerySense.Core;

// Port of reference-python/.../services/flyer_sync_scheduler.py — hourly poll via threading.Timer.
// On mobile, prefer a sync-on-resume / manual-button model over a long-lived background timer
// (iOS/Android background execution is restricted). Wire to app lifecycle in the App project.
public sealed class FlyerSyncScheduler
{
    public void Start() => throw new NotImplementedException();
    public void RequestSync() => throw new NotImplementedException();
    public void Stop() => throw new NotImplementedException();
}
