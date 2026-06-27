namespace GrocerySense.Core;

// Port of reference-python/.../services/flyer_sync_service.py — throttled (twice-weekly) provider sync.
// Provider is the Flipp stub today; produces nothing real until wired. Don't fake deals.
public sealed class FlyerSyncService
{
    public bool NeedsSync() => throw new NotImplementedException();

    public FlyerSyncResult RunSync(bool force = false) => throw new NotImplementedException();
}
