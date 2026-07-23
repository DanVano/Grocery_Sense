using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using GrocerySense.App.Services;
using GrocerySense.Core;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using AWebView = Android.Webkit.WebView;
using Microsoft.Extensions.DependencyInjection;

namespace GrocerySense.App;

// SingleTop so a notification tap while the app is alive routes through OnNewIntent instead of a new instance.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Share target (Phase 5): the OS offers Grocery Sense in the share sheet for a shared image (or several),
// so a receipt photo from the camera/gallery/email app can be sent straight in. Images only — a shared
// image is unambiguously a receipt; flyers need a store + validity the share sheet can't supply.
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "image/*")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleRouteIntent(Intent);  // cold start from a notification tap
        HandleSendIntent(Intent);   // cold start from a share
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleRouteIntent(intent);  // tap while already running
        HandleSendIntent(intent);   // share while already running
    }

    // Capture the notification's route into PendingNavigationService; MainLayout consumes it once Blazor is up.
    private static void HandleRouteIntent(Intent? intent)
    {
        var route = intent?.GetStringExtra(AndroidLocalNotifier.RouteExtra);
        if (string.IsNullOrEmpty(route)) return;
        IPlatformApplication.Current?.Services.GetService<PendingNavigationService>()?.Set(route);
    }

    // Extract the shared image URI(s) and copy them into the receipts dir off the intent thread, then hand
    // the paths to Blazor to confirm + ingest. The URI(s) are untrusted external input: every copy is
    // size- and type-bounded by ReceiptFilePolicy, and a rejected/unreadable share is recorded as an error
    // rather than dropped, so the confirm banner can disclose it.
    //
    // P0-2: the single copy slot is reserved SYNCHRONOUSLY here on the intent thread, before Task.Run —
    // two simultaneous intents can never both start copying. A share arriving while a batch is in flight
    // is rejected loudly with zero copies. Caps (≤10 URIs, ≤100 MiB aggregate, 2-min cooperative deadline)
    // are enforced in the copy phase; the one-batch state machine is the actual containment.
    private void HandleSendIntent(Intent? intent)
    {
        if (intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple)) return;
        if (intent.Type is not { } type || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;

        var uris = ExtractStreamUris(intent);
        if (uris.Count == 0) return;

        var services = IPlatformApplication.Current?.Services;
        var pending = services?.GetService<PendingSharedReceiptsService>();
        if (pending is null) return;

        if (!pending.TryBeginCopy())
        {
            pending.RejectShare(
                $"A share of {uris.Count} item(s) was rejected — another shared batch is still being processed. " +
                "Import or discard it first, then share again.");
            services?.GetService<PendingNavigationService>()?.Set("/receipts");
            return;
        }

        var resolver = ContentResolver;
        _ = Task.Run(() => CopySharedReceiptsAsync(resolver, uris, pending, services));
    }

#pragma warning disable CA1422 // GetParcelable*Extra is obsolete on API 33+ but is the cross-version API here.
    private static List<Android.Net.Uri> ExtractStreamUris(Intent intent)
    {
        var result = new List<Android.Net.Uri>();
        if (intent.Action == Intent.ActionSendMultiple)
        {
            if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is { } list)
                foreach (var item in list)
                    if (item is Android.Net.Uri uri) result.Add(uri);
        }
        else if (intent.GetParcelableExtra(Intent.ExtraStream) is Android.Net.Uri uri)
        {
            result.Add(uri);
        }
        return result;
    }
#pragma warning restore CA1422

    // Copy phase for a reserved batch. Caps: at most MaxUrisPerShare copies (excess disclosed), aggregate
    // bytes ≤ MaxAggregateBytes, and a cooperative CopyDeadline — ContentResolver.Query gets a
    // CancellationSignal and the stream copy honours the token, but OpenInputStream has no cancellable
    // overload, so a hostile provider can still stall a single open; the state machine (one batch at a
    // time) is the actual containment. CompleteCopy ALWAYS runs so the slot can never leak.
    private static async Task CopySharedReceiptsAsync(ContentResolver? resolver, IReadOnlyList<Android.Net.Uri> uris,
        PendingSharedReceiptsService pending, IServiceProvider? services)
    {
        var paths = new List<string>();
        var errors = new List<string>();
        using var deadline = new CancellationTokenSource(PendingSharedReceiptsService.CopyDeadline);
        long totalBytes = 0;
        try
        {
            IReadOnlyList<Android.Net.Uri> accepted = uris;
            if (uris.Count > PendingSharedReceiptsService.MaxUrisPerShare)
            {
                accepted = uris.Take(PendingSharedReceiptsService.MaxUrisPerShare).ToList();
                errors.Add($"{uris.Count - accepted.Count} of {uris.Count} shared items were not copied — " +
                           $"at most {PendingSharedReceiptsService.MaxUrisPerShare} per share.");
            }

            foreach (var uri in accepted)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var name = PendingSharedReceiptsService.Truncate(
                        QueryDisplayName(resolver, uri, deadline.Token) ?? uri.LastPathSegment ?? "shared-receipt",
                        PendingSharedReceiptsService.MaxDisplayNameChars);
                    await using var stream = resolver?.OpenInputStream(uri)
                        ?? throw new IOException("The shared item could not be opened.");
                    var copied = await ReceiptFilePolicy.CopyStreamAsync(stream, name, deadline.Token);

                    totalBytes += new FileInfo(copied).Length;
                    if (totalBytes > PendingSharedReceiptsService.MaxAggregateBytes)
                    {
                        try { File.Delete(copied); } catch { /* best-effort; the sweep reaps it */ }
                        errors.Add("Aggregate share size exceeded " +
                                   $"{PendingSharedReceiptsService.MaxAggregateBytes / (1024 * 1024)} MiB — " +
                                   "this and any remaining items were not copied.");
                        break;
                    }
                    paths.Add(copied);
                }
                catch (OperationCanceledException)
                {
                    errors.Add("Copy deadline exceeded — remaining shared items were not copied.");
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }
        }
        finally
        {
            pending.CompleteCopy(paths, errors);
            // Land the user on the Receipts page where the confirm banner shows the batch.
            services?.GetService<PendingNavigationService>()?.Set("/receipts");
        }
    }

    // Best-effort human-readable name so the bounded copy can honour a real extension; falls back to the
    // policy's default when the provider doesn't expose one. "_display_name" is OpenableColumns.DISPLAY_NAME.
    // The CancellationSignal ties the query to the copy deadline (cancellation-aware where the platform is).
    private static string? QueryDisplayName(ContentResolver? resolver, Android.Net.Uri uri, CancellationToken ct)
    {
        if (resolver is null) return null;
        try
        {
            using var signal = new Android.OS.CancellationSignal();
            using var registration = ct.Register(signal.Cancel);
            using var cursor = resolver.Query(uri, new[] { "_display_name" }, null, null, null, signal);
            if (cursor is not null && cursor.MoveToFirst() && cursor.GetColumnIndex("_display_name") is var i && i >= 0)
                return cursor.GetString(i);
        }
        catch { /* display name is best-effort; the copy falls back to the default extension */ }
        return null;
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
