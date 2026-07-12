namespace GrocerySense.Core;

// Holds a route captured from a notification tap until Blazor is ready to consume it (A7 deep link). The
// Android MainActivity sets it (OnCreate/OnNewIntent, before the WebView exists); MainLayout reads and clears
// it once on first render and navigates. Lives in Core (not App) so the hand-off is unit-testable; the
// platform layer resolves it via DI. Thread-safe: MainActivity's intent thread writes, the UI thread reads.
public sealed class PendingNavigationService
{
    private readonly object _sync = new();
    private string? _pendingRoute;

    // Raised after a route is set so a live UI (warm notification tap via OnNewIntent) can navigate at once.
    // On a cold start the route is set before anyone subscribes; the UI drains it via TakePendingRoute instead.
    public event Action? RouteSet;

    public void Set(string route)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        lock (_sync) _pendingRoute = route;
        RouteSet?.Invoke();
    }

    // Returns the pending route once, then clears it (a route is consumed exactly once).
    public string? TakePendingRoute()
    {
        lock (_sync)
        {
            var route = _pendingRoute;
            _pendingRoute = null;
            return route;
        }
    }
}
