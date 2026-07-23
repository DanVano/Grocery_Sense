namespace GrocerySense.Core;

// Holds receipt image(s) shared into the app (Android ACTION_SEND / ACTION_SEND_MULTIPLE) until Blazor is
// ready to confirm and ingest them. The platform layer (MainActivity) copies each shared stream into the
// receipts dir through the bounded receipt-file policy; the Receipts page renders a confirm banner and
// imports only on the user's OK — a share is never ingested silently.
//
// P0-2 hardening: this is an atomic state machine, Idle → Copying → Pending → Importing → Idle. The intent
// handler reserves Copying SYNCHRONOUSLY on the intent thread before any background copy starts, so two
// simultaneous intents can never both observe "nothing pending" and both copy; a share arriving in any
// non-Idle state is rejected loudly (zero copies, error recorded) instead of silently replacing the batch.
// Every copied file is owned by exactly one state at all times; the DB-aware startup sweep reaps anything
// a process kill orphans. Lives in Core so the hand-off is unit-testable; thread-safe throughout.
public enum ShareIntakeState { Idle, Copying, Pending, Importing }

public sealed class PendingSharedReceiptsService
{
    // Intake caps, enforced by the platform copy phase (MainActivity) and pinned here so tests can assert
    // them. The copy deadline is COOPERATIVE — a hostile content provider can stall a read; the one-batch
    // state machine is the actual containment.
    public const int MaxUrisPerShare = 10;
    public const long MaxAggregateBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan CopyDeadline = TimeSpan.FromMinutes(2);
    // Provider-supplied display names and error strings are length-bounded before storage/display.
    public const int MaxDisplayNameChars = 128;
    public const int MaxErrorChars = 300;

    private readonly object _sync = new();
    private ShareIntakeState _state = ShareIntakeState.Idle;
    private List<string> _paths = [];
    private List<string> _errors = [];

    // Raised on every state transition so a live UI (warm share via OnNewIntent) can react at once. On a
    // cold start the batch is Pending before anyone subscribes; the page Peeks on first render instead.
    public event Action? Changed;

    public ShareIntakeState State
    {
        get { lock (_sync) return _state; }
    }

    public bool HasPending
    {
        get { lock (_sync) return _state == ShareIntakeState.Pending && (_paths.Count > 0 || _errors.Count > 0); }
    }

    // Intent thread, BEFORE any Task.Run: reserve the single copy slot. False means another batch is in
    // flight — the caller must copy nothing and record the rejection via RejectShare.
    public bool TryBeginCopy()
    {
        lock (_sync)
        {
            if (_state != ShareIntakeState.Idle) return false;
            _state = ShareIntakeState.Copying;
            return true;
        }
    }

    // Ends the copy phase the caller reserved with TryBeginCopy. Errors append to any rejection recorded
    // meanwhile; an entirely empty result returns to Idle so a no-op share can't wedge the machine.
    public void CompleteCopy(IReadOnlyList<string> copiedPaths, IReadOnlyList<string> errors)
    {
        lock (_sync)
        {
            if (_state != ShareIntakeState.Copying)
                throw new InvalidOperationException($"CompleteCopy in state {_state} — TryBeginCopy owns the slot.");
            _paths = [.. copiedPaths];
            _errors.AddRange(errors.Select(e => Truncate(e, MaxErrorChars)));
            _state = _paths.Count > 0 || _errors.Count > 0 ? ShareIntakeState.Pending : ShareIntakeState.Idle;
        }
        Changed?.Invoke();
    }

    // A share arrived while another batch holds the machine: record it loudly (no copies were made). The
    // message surfaces on the banner — as part of the current batch, or as an error-only batch when Idle
    // (defensive: callers only invoke this after TryBeginCopy failed, i.e. non-Idle).
    public void RejectShare(string reason)
    {
        lock (_sync)
        {
            _errors.Add(Truncate(reason, MaxErrorChars));
            if (_state == ShareIntakeState.Idle) _state = ShareIntakeState.Pending;
        }
        Changed?.Invoke();
    }

    // Render the banner without claiming ownership.
    public (IReadOnlyList<string> Paths, IReadOnlyList<string> Errors) Peek()
    {
        lock (_sync) return (_paths.ToList(), _errors.ToList());
    }

    // Import claims exclusive ownership of the batch (Pending → Importing). Errors are handed to the caller
    // and cleared — the import summary owns disclosing them from here on.
    public bool TryBeginImport(out IReadOnlyList<string> paths, out IReadOnlyList<string> errors)
    {
        lock (_sync)
        {
            if (_state != ShareIntakeState.Pending || _paths.Count == 0)
            {
                paths = [];
                errors = [];
                return false;
            }
            _state = ShareIntakeState.Importing;
            paths = _paths.ToList();
            errors = _errors.ToList();
            _errors = [];
            return true;
        }
    }

    // Import finished (imported, failed, or cancelled — the caller already cleaned the files). Rejections
    // that arrived DURING the import stay visible as an error-only batch instead of vanishing.
    public void CompleteImport()
    {
        lock (_sync)
        {
            if (_state != ShareIntakeState.Importing)
                throw new InvalidOperationException($"CompleteImport in state {_state} — TryBeginImport owns the batch.");
            _paths = [];
            _state = _errors.Count > 0 ? ShareIntakeState.Pending : ShareIntakeState.Idle;
        }
        Changed?.Invoke();
    }

    // Discard (or dismiss an error-only batch): Pending → Idle. Returns the paths so the caller deletes the
    // now-unowned copies; an error-only batch just clears.
    public IReadOnlyList<string> Discard()
    {
        List<string> paths;
        lock (_sync)
        {
            if (_state != ShareIntakeState.Pending) return [];
            paths = _paths;
            _paths = [];
            _errors = [];
            _state = ShareIntakeState.Idle;
        }
        Changed?.Invoke();
        return paths;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
