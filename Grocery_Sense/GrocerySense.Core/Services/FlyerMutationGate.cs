namespace GrocerySense.Core;

// P1-4: ONE single-flight gate covering every flyer write path — scheduler resume, the manual Sync
// button, and manual flyer import. Replaces the scheduler's private semaphore so no second independent
// lock exists; concurrent callers get a disclosed "busy" instead of interleaved flyer batches.
public sealed class FlyerMutationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool TryEnter() => _gate.Wait(0);
    public void Exit() => _gate.Release();
}
