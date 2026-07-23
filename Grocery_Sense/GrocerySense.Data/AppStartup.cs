namespace GrocerySense.Data;

public enum StartupStatus { Loading, Ready, Error }

// Startup state machine (PORTING.md Phase 8 mobile requirements): DB migrations run off the UI
// thread; the layout renders loading/ready/error off this state instead of blocking the first frame.
// Errors surface verbatim — a broken DB must be visible, not silently retried.
//
// P1-5: before migrations, any staged restore is completed (cold-start swap — the only safe moment,
// no pool or consumer exists yet), and startup is RETRYABLE from the Error state so the error shell's
// recovery controls (retry after staging a restore) work without killing the process. Single-flight is
// preserved: while a run is in flight every caller awaits it; only a FAILED run can be restarted.
//
// Lives in Data (not the MAUI App head) because it depends only on SqliteConnectionFactory + Database
// — no MAUI — which keeps the state machine unit-testable (AppStartupTests) off-device.
public sealed class AppStartup
{
    private readonly SqliteConnectionFactory _factory;
    private readonly object _sync = new();
    private Task? _init;

    public AppStartup(SqliteConnectionFactory factory) => _factory = factory;

    public StartupStatus Status { get; private set; } = StartupStatus.Loading;
    public string? Error { get; private set; }
    public event Action? Changed;

    // Idempotent, single-flight: every page can await this; migrations run once.
    public Task EnsureStartedAsync()
    {
        lock (_sync) return _init ??= Task.Run(InitializeCore);
    }

    // Error-shell recovery: restart a FAILED startup (e.g. after staging a restore). A run in flight or
    // an already-Ready state is never restarted — callers just get the existing task.
    public Task RetryAsync()
    {
        Task task;
        var restarted = false;
        lock (_sync)
        {
            if (_init is { IsCompleted: false } inFlight) return inFlight;
            if (Status == StartupStatus.Ready) return _init ?? Task.CompletedTask;
            Status = StartupStatus.Loading;
            Error = null;
            restarted = true;
            task = _init = Task.Run(InitializeCore);
        }
        if (restarted) Changed?.Invoke(); // back to the loading frame while the retry runs
        return task;
    }

    private void InitializeCore()
    {
        try
        {
            // Complete any staged restore first — cold start, before any DB consumer exists (P1-5).
            RestoreStaging.CompletePendingRestore(_factory.DbPath);
            Database.Initialize(_factory);
            Status = StartupStatus.Ready;
        }
        catch (Exception ex)
        {
            Status = StartupStatus.Error;
            Error = ex.Message;
        }
        Changed?.Invoke();
    }
}
