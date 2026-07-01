using GrocerySense.Data;

namespace GrocerySense.App.Services;

public enum StartupStatus { Loading, Ready, Error }

// Startup state machine (PORTING.md Phase 8 mobile requirements): DB migrations run off the UI
// thread; the layout renders loading/ready/error off this state instead of blocking the first frame.
// Errors surface verbatim — a broken DB must be visible, not silently retried.
public sealed class AppStartup
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Lazy<Task> _init;

    public AppStartup(SqliteConnectionFactory factory)
    {
        _factory = factory;
        _init = new Lazy<Task>(() => Task.Run(InitializeCore));
    }

    public StartupStatus Status { get; private set; } = StartupStatus.Loading;
    public string? Error { get; private set; }
    public event Action? Changed;

    // Idempotent, single-flight: every page can await this; migrations run once.
    public Task EnsureStartedAsync() => _init.Value;

    private void InitializeCore()
    {
        try
        {
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
