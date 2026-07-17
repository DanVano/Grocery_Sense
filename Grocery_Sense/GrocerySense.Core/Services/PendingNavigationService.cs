namespace GrocerySense.Core;

// Holds a route captured from a notification tap until Blazor is ready to consume it (A7 deep link). The
// Android MainActivity sets it (OnCreate/OnNewIntent, before the WebView exists); MainLayout reads and clears
// it once on first render and navigates. Lives in Core (not App) so the hand-off is unit-testable; the
// platform layer resolves it via DI. Thread-safe: MainActivity's intent thread writes, the UI thread reads.
public sealed class PendingNavigationService
{
    // Allowlist of routes a notification tap may deep-link to. This is the trust boundary: on Android the
    // launcher MainActivity is exported, so a hostile app can start it with an arbitrary `notification_route`
    // intent extra. Restricting to known in-app paths stops that from forcing navigation to an unintended
    // page or (via NavigateTo on an absolute URL) opening attacker content externally. Add a route here — the
    // one place it gets security-reviewed — when a new deep link is introduced.
    private static readonly HashSet<string> AllowedRoutes = new(StringComparer.Ordinal) { "/savings" };

    private readonly object _sync = new();
    private string? _pendingRoute;

    // Raised after a route is set so a live UI (warm notification tap via OnNewIntent) can navigate at once.
    // On a cold start the route is set before anyone subscribes; the UI drains it via TakePendingRoute instead.
    public event Action? RouteSet;

    public void Set(string route)
    {
        // Drop null/blank AND anything not on the allowlist — the incoming value is untrusted (OS intent).
        if (!AllowedRoutes.Contains(route)) return;
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
