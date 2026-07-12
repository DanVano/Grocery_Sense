namespace GrocerySense.Core.Abstractions;

// A local (on-device) notification sink. ShowAsync returns false when notifications are denied, disabled, or
// unsupported on the current head — it never throws for denial (the caller still surfaces an in-app line).
public interface ILocalNotifier
{
    Task<bool> ShowAsync(string title, string body, CancellationToken ct = default);
}
