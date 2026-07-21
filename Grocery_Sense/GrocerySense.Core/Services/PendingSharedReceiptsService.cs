namespace GrocerySense.Core;

// Holds receipt image(s) shared into the app (Android ACTION_SEND / ACTION_SEND_MULTIPLE) until Blazor is
// ready to confirm and ingest them. The platform layer (MainActivity) copies each shared stream into the
// receipts dir through the bounded receipt-file policy, then sets the resulting paths here plus a pending
// "/receipts" route; the Receipts page drains them once, shows a confirm banner, and imports only on the
// user's OK — a share is never ingested silently.
//
// Errors from rejected shares (oversize, disallowed type, unreadable stream) ride along so the user sees
// "2 imported, 1 rejected: …" rather than a share vanishing. Lives in Core so the hand-off is
// unit-testable; the platform layer resolves it via DI. Thread-safe: the intent thread writes, the UI
// thread drains.
public sealed class PendingSharedReceiptsService
{
    private readonly object _sync = new();
    private List<string> _paths = [];
    private List<string> _errors = [];

    // Raised after a share is set so a live UI (warm share via OnNewIntent) can react at once. On a cold
    // start the data is set before anyone subscribes; the page drains it via Take on first render instead.
    public event Action? Changed;

    public void Set(IReadOnlyList<string> copiedPaths, IReadOnlyList<string> errors)
    {
        lock (_sync)
        {
            _paths = [.. copiedPaths];
            _errors = [.. errors];
        }
        Changed?.Invoke();
    }

    public bool HasPending
    {
        get { lock (_sync) return _paths.Count > 0 || _errors.Count > 0; }
    }

    // Returns the pending paths + errors once, then clears them (drained exactly once).
    public (IReadOnlyList<string> Paths, IReadOnlyList<string> Errors) Take()
    {
        lock (_sync)
        {
            var result = ((IReadOnlyList<string>)_paths, (IReadOnlyList<string>)_errors);
            _paths = [];
            _errors = [];
            return result;
        }
    }
}
