namespace GrocerySense.Core;

// P0-3: the single gate every PAID Azure OCR call (receipt analysis + flyer layout) runs through. It must
// be an injected Core singleton — the App head constructs a NEW Azure client per call, so a lock inside
// the clients would serialize nothing. One call at a time keeps a burst of user actions from stacking
// paid requests; the service-boundary file/byte caps are the actual spend control.
//
// Deadline vs cancel are distinguished honestly: the per-call deadline surfaces TimeoutException, caller
// cancellation stays OperationCanceledException. Cancelling local polling does NOT cancel Azure's
// already-submitted server operation — a timed-out or cancelled call may still bill.
public sealed class OcrGate
{
    public static readonly TimeSpan DefaultCallDeadline = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _deadline;

    public OcrGate() : this(DefaultCallDeadline) { }
    public OcrGate(TimeSpan callDeadline) => _deadline = callDeadline; // test seam

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> paidCall, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_deadline);
            try
            {
                return await paidCall(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The OCR call exceeded its {_deadline.TotalSeconds:0}-second deadline. " +
                    "The Azure operation may still complete (and bill) server-side.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
