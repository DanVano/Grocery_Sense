using Android.App;
using Android.Content;
using AndroidX.Core.App;
using GrocerySense.Core.Abstractions;
using Application = Android.App.Application;

namespace GrocerySense.App;

// Android ILocalNotifier (A7). Posts ONE summary price-alert notification that deep-links to /savings.
// COMPILE-VERIFIED ON DEVICE ONLY — the Windows/iOS heads never build this file, and there is no offline
// test (V2_FOLLOWUPS ground rule). Returns false (never throws) whenever notifications can't show, so the
// caller keeps the in-app "N new price alert(s)" line as the always-visible fallback.
public sealed class AndroidLocalNotifier : ILocalNotifier
{
    private const string ChannelId = "price_alerts";
    private const string ChannelName = "Price alerts";
    public const string RouteExtra = "notification_route";
    private const string SavingsRoute = "/savings";
    private static int _nextId = 1000;

    public async Task<bool> ShowAsync(string title, string body, CancellationToken ct = default)
    {
        var context = Application.Context;

        // API 33+ needs runtime POST_NOTIFICATIONS; request on first Show. Denied => false (in-app line still shows).
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await MainThread.InvokeOnMainThreadAsync(
                () => Permissions.RequestAsync<Permissions.PostNotifications>());
            if (status != PermissionStatus.Granted) return false;
        }

        var manager = NotificationManagerCompat.From(context);
        if (!manager.AreNotificationsEnabled()) return false; // disabled at app/channel level

        EnsureChannel(context);

        var intent = new Intent(context, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        intent.PutExtra(RouteExtra, SavingsRoute);
        var pending = PendingIntent.GetActivity(context, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Mipmap.appicon) // explicit small icon (AOT: the resource must exist)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetAutoCancel(true)
            .SetContentIntent(pending)
            .SetPriority(NotificationCompat.PriorityDefault);

        manager.Notify(Interlocked.Increment(ref _nextId), builder.Build());
        return true;
    }

    // Notification channels are API 26+; minSdk is 24, so guard. Idempotent — safe to call on every Show.
    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Default)
        {
            Description = "Price-drop alerts from your scanned receipts.",
        };
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(channel);
    }
}
