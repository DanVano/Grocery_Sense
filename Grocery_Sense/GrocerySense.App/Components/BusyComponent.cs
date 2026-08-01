using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GrocerySense.App.Components;

// Shared page state for the two alert patterns the pages use: the busy-flag + inline `_error`
// (GuardAsync) and the `_message` + severity alert (Fail). Field names keep the leading underscore
// so inheriting pages' markup reads unchanged.
public abstract class BusyComponent : ComponentBase
{
    protected bool _busy;
    protected string? _error;
    protected string? _message;
    protected Severity _messageSeverity = Severity.Info;

    // Run sync service work off the UI thread; errors land in _error, result is null on failure.
    // `after` runs inside the same guard, so its failure surfaces too.
    protected async Task<T?> GuardAsync<T>(Func<T> work, Func<Task>? after = null) where T : class
    {
        _busy = true;
        try
        {
            _error = null;
            var result = await Task.Run(work);
            if (after is not null) await after();
            return result;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            return null;
        }
        finally { _busy = false; }
    }

    protected Task GuardAsync(Action work, Func<Task>? after = null) =>
        GuardAsync<object>(() => { work(); return new object(); }, after);

    protected void Fail(string message)
    {
        _messageSeverity = Severity.Error;
        _message = message;
    }

    protected static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
