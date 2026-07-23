namespace GrocerySense.Core.Abstractions;

// P1-4: a flyer provider said "stop" (HTTP 429/403 on the unofficial Flipp endpoints). Lives in Core
// abstractions — FlyerSyncService must catch it by type and Core cannot reference a type declared in
// Integrations (dependency direction: Integrations → Core). RetryAfter carries the server's Retry-After
// when present; the sync persists it as retry_not_before and aborts the remaining stores.
public sealed class FlyerProviderThrottledException : Exception
{
    public TimeSpan? RetryAfter { get; }

    public FlyerProviderThrottledException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner) => RetryAfter = retryAfter;
}
