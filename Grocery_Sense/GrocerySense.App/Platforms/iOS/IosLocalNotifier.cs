using Foundation;
using GrocerySense.Core;
using GrocerySense.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using UserNotifications;

namespace GrocerySense.App;

// iOS ILocalNotifier (B1). COMPILE-VERIFIED ON MAC ONLY (B2) — the Windows/Android heads never build this
// file, and there is no offline test. Requests authorization on first Show; denial => false (the in-app
// "N new price alert(s)" line is the fallback). A tap routes to /savings via PendingNavigationService.
public sealed class IosLocalNotifier : UNUserNotificationCenterDelegate, ILocalNotifier
{
    private const string RouteKey = "notification_route";
    private const string SavingsRoute = "/savings";

    public IosLocalNotifier()
    {
        // Safe to set here: a notification can only exist to tap AFTER a Show, and Show requires this instance,
        // so the delegate is always in place before any response arrives.
        UNUserNotificationCenter.Current.Delegate = this;
    }

    public async Task<bool> ShowAsync(string title, string body, CancellationToken ct = default)
    {
        var center = UNUserNotificationCenter.Current;
        var (granted, _) = await center.RequestAuthorizationAsync(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        if (!granted) return false; // denied/disabled -> in-app line still shows

        var content = new UNMutableNotificationContent
        {
            Title = title,
            Body = body,
            UserInfo = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { new NSString(SavingsRoute) },
                new NSObject[] { new NSString(RouteKey) }),
        };
        var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString("N"), content, trigger: null);
        var error = await center.AddNotificationRequestAsync(request);
        return error is null;
    }

    // Foreground: still present the banner + sound.
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler)
        => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);

    // Tap -> capture the route for Blazor (MainLayout) to consume.
    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center,
        UNNotificationResponse response, Action completionHandler)
    {
        var route = response.Notification.Request.Content.UserInfo.ObjectForKey(new NSString(RouteKey))?.ToString();
        if (!string.IsNullOrEmpty(route))
            IPlatformApplication.Current?.Services.GetService<PendingNavigationService>()?.Set(route);
        completionHandler();
    }
}
