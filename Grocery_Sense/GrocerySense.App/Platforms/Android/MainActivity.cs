using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using GrocerySense.Core;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.App;

// SingleTop so a notification tap while the app is alive routes through OnNewIntent instead of a new instance.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleRouteIntent(Intent); // cold start from a notification tap
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleRouteIntent(intent); // tap while already running
    }

    // Capture the notification's route into PendingNavigationService; MainLayout consumes it once Blazor is up.
    private static void HandleRouteIntent(Intent? intent)
    {
        var route = intent?.GetStringExtra(AndroidLocalNotifier.RouteExtra);
        if (string.IsNullOrEmpty(route)) return;
        IPlatformApplication.Current?.Services.GetService<PendingNavigationService>()?.Set(route);
    }
}
