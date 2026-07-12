using GrocerySense.Core.Abstractions;

namespace GrocerySense.App.Services;

// ILocalNotifier for non-Android heads (Windows dev harness; iOS until B1). Always false — there is no local
// notifier here, so callers fall back to the in-app "N new price alert(s)" line.
public sealed class NoOpLocalNotifier : ILocalNotifier
{
    public Task<bool> ShowAsync(string title, string body, CancellationToken ct = default) => Task.FromResult(false);
}
