using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using GrocerySense.Core;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using AWebView = Android.Webkit.WebView;
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

    // Hardware Back (Phase 2d). This is a Blazor WebView SPA, not native fragments, so there is no
    // native back stack to pop — Blazor client-side navigation pushes real History API entries, so the
    // WebView's own history IS the in-app back stack. Walk it when we can; otherwise fall through to the
    // default (leave the app), the Android norm at a top-level destination.
    //
    // Deliberate v1 limitation: a MudBlazor dialog/drawer does not create a history entry, so Back with
    // one open navigates the page behind it rather than dismissing it first. Closing overlays on Back
    // needs a managed-side bridge (open-overlay state + JS interop) — deferred, tracked in V2_FOLLOWUPS.
#pragma warning disable CA1422 // OnBackPressed is obsolete on API 33+ but remains the supported override here.
    public override void OnBackPressed()
    {
        if (FindWebView(Window?.DecorView) is { } webView && webView.CanGoBack())
        {
            webView.GoBack();
            return;
        }
        base.OnBackPressed();
    }
#pragma warning restore CA1422

    // The MAUI BlazorWebView is backed by a single Android.Webkit.WebView somewhere in the view tree.
    private static AWebView? FindWebView(AView? view) => view switch
    {
        AWebView webView => webView,
        AViewGroup group => FindWebViewInChildren(group),
        _ => null,
    };

    private static AWebView? FindWebViewInChildren(AViewGroup group)
    {
        for (var i = 0; i < group.ChildCount; i++)
            if (FindWebView(group.GetChildAt(i)) is { } found)
                return found;
        return null;
    }
}
