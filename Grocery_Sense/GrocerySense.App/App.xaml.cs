using GrocerySense.Core;
using GrocerySense.Data;
using Microsoft.Extensions.Logging;

namespace GrocerySense.App;

// Code-only Application: there is no App.xaml — the shell had no Application.Resources to declare
// (the BlazorWebView styles itself from wwwroot + MudBlazor).
public class App : Application
{
	private readonly AppStartup _startup;
	private readonly FlyerSyncScheduler _flyerSync;
	private readonly DbMaintenanceService _maintenance;
	private readonly ILogger<App> _logger;

	// Marker key: iOS Keychain (SecureStorage) survives an uninstall, so creds from a prior install could
	// leak into a fresh one. NSUserDefaults (Preferences) IS wiped on uninstall, so its absence = true first launch.
	private const string SecureStorageInitializedKey = "secure_storage_initialized_v1";

	public App(AppStartup startup, FlyerSyncScheduler flyerSync, DbMaintenanceService maintenance, ILogger<App> logger)
	{
		ResetIosSecretsOnFirstLaunch();
		_startup = startup;
		_flyerSync = flyerSync;
		_maintenance = maintenance;
		_logger = logger;

		// Purge plaintext backup/export copies this app shared out that are older than 24h, so they don't
		// linger in the clear in the cache dir. Must never block startup.
		try
		{
			DbMaintenanceService.CleanupShareArtifacts(
				Microsoft.Maui.Storage.FileSystem.CacheDirectory,
				DateTime.UtcNow.AddHours(-24));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Sensitive share-cache cleanup failed");
		}

		// P0-2: reap intake files a process kill orphaned (copied receipt/flyer images no DB row references,
		// older than 24 h). DB-aware, so it must wait for startup Ready; must never block or crash startup.
		_ = SweepIntakeOrphansAsync();
	}

	private async Task SweepIntakeOrphansAsync()
	{
		try
		{
			await _startup.EnsureStartedAsync();
			if (_startup.Status != StartupStatus.Ready) return; // DB error is already on screen
			await Task.Run(() => _maintenance.SweepUnreferencedIntakeFiles(
				Services.ReceiptFilePolicy.ReceiptsDir(),
				Services.FlyerFilePolicy.FlyersDir(),
				DateTime.UtcNow.AddHours(-24)));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Intake orphan sweep failed");
		}
	}

	private static void ResetIosSecretsOnFirstLaunch()
	{
#if IOS
		if (Microsoft.Maui.Storage.Preferences.Default.ContainsKey(SecureStorageInitializedKey)) return;

		Microsoft.Maui.Storage.SecureStorage.Default.RemoveAll();
		Microsoft.Maui.Storage.Preferences.Default.Set(SecureStorageInitializedKey, true);
#endif
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new MainPage()) { Title = "Grocery Sense" };
		// Sync-on-resume replaces Python's background timer (Phase 6 redesign). A sync that ran also
		// refreshes price-drop alerts via the SyncCompleted hook wired in AddGrocerySenseCore.
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
