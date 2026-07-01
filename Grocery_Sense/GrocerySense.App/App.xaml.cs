using GrocerySense.App.Services;
using GrocerySense.Core;
using Microsoft.Extensions.Logging;

namespace GrocerySense.App;

public partial class App : Application
{
	private readonly AppStartup _startup;
	private readonly FlyerSyncScheduler _flyerSync;
	private readonly ILogger<App> _logger;

	public App(AppStartup startup, FlyerSyncScheduler flyerSync, ILogger<App> logger)
	{
		InitializeComponent();
		_startup = startup;
		_flyerSync = flyerSync;
		_logger = logger;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Grocery Sense" };
		// Sync-on-resume replaces Python's background timer (Phase 6 redesign; hook deferred to Phase 8).
		// The scheduler is single-flighted and throttled, so a chatty Resumed event is harmless.
		window.Resumed += (_, _) => _ = SyncOnResumeAsync();
		return window;
	}

	private async Task SyncOnResumeAsync()
	{
		try
		{
			await _startup.EnsureStartedAsync();
			if (_startup.Status != StartupStatus.Ready) return; // DB error is already on screen
			await _flyerSync.CheckOnResumeAsync();
		}
		catch (Exception ex)
		{
			// A failed background sync must not crash resume; the next manual sync surfaces errors in the UI.
			_logger.LogError(ex, "Flyer sync on resume failed");
		}
	}
}
