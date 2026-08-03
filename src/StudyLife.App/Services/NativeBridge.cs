using Microsoft.JSInterop;
#if IOS || MACCATALYST
using UserNotifications;
#endif
#if ANDROID
using Android.App;
using Android.Content;
using AndroidX.Core.App;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// Counterpart to the native bridge script block in wwwroot/index.html: the JS functions
/// overridden there (Wake Lock, Notifications, Badge, Vibration) land here as static
/// [JSInvokable] methods and are mapped onto the respective platform APIs. The return values
/// mimic web API semantics ('granted'/'denied'/'default'/'unsupported') so that
/// NotificationService and friends work unchanged.
/// </summary>
public static class NativeBridge
{
#if ANDROID
    private const string ChannelId = "studylife";
    private static int _notificationId;
    private static WeakReference<global::Android.Webkit.WebView>? _webView;

    public static void SetWebView(global::Android.Webkit.WebView webView) =>
        _webView = new WeakReference<global::Android.Webkit.WebView>(webView);
#endif
#if IOS || MACCATALYST
    private static WeakReference<WebKit.WKWebView>? _webView;

    public static void SetWebView(WebKit.WKWebView webView) =>
        _webView = new WeakReference<WebKit.WKWebView>(webView);
#endif

    /// <summary>Native print dialog for the WebView content (report page): iOS via
    /// UIPrintInteractionController + ViewPrintFormatter, Android via the PrintManager.
    /// Windows never needs this (window.print works in WebView2, see index.html).</summary>
    [JSInvokable("NativePrint")]
    public static Task<bool> PrintAsync()
    {
#if IOS || MACCATALYST
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (_webView == null || !_webView.TryGetTarget(out var webView))
                {
                    _ = ShowNotificationAsync("Print diagnostics", "No WebView reference (SetWebView never called?)");
                    completion.TrySetResult(false);
                    return;
                }
                var printInfo = UIKit.UIPrintInfo.PrintInfo;
                printInfo.OutputType = UIKit.UIPrintInfoOutputType.General;
                printInfo.JobName = "StudyLife";
                var controller = UIKit.UIPrintInteractionController.SharedPrintController;
                controller.PrintInfo = printInfo;
                controller.PrintFormatter = webView.ViewPrintFormatter;
                var presented = controller.Present(true, (printController, completed, error) =>
                {
                    if (error != null)
                        _ = ShowNotificationAsync("Print diagnostics", $"Print error: {error.LocalizedDescription}");
                });
                if (!presented)
                    _ = ShowNotificationAsync("Print diagnostics", "Present() returned false - print dialog not shown");
                completion.TrySetResult(presented);
            }
            catch (Exception ex)
            {
                _ = ShowNotificationAsync("Print diagnostics", $"Exception: {ex.Message}");
                completion.TrySetResult(false);
            }
        });
        return completion.Task;
#elif ANDROID
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity == null || _webView == null || !_webView.TryGetTarget(out var webView))
                {
                    completion.TrySetResult(false);
                    return;
                }
                var printManager = (global::Android.Print.PrintManager?)activity.GetSystemService(Context.PrintService);
                var adapter = webView.CreatePrintDocumentAdapter("StudyLife");
                if (printManager == null || adapter == null)
                {
                    completion.TrySetResult(false);
                    return;
                }
                printManager.Print("StudyLife", adapter, new global::Android.Print.PrintAttributes.Builder().Build());
                completion.TrySetResult(true);
            }
            catch
            {
                completion.TrySetResult(false);
            }
        });
        return completion.Task;
#else
        return Task.FromResult(false);
#endif
    }

    [JSInvokable("NativeKeepScreenOn")]
    public static Task<bool> KeepScreenOnAsync(bool on)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { DeviceDisplay.Current.KeepScreenOn = on; }
            catch { /* Platform without support - timer keeps running regardless */ }
        });
        return Task.FromResult(true);
    }

    [JSInvokable("NativeVibrate")]
    public static Task VibrateAsync()
    {
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(200)); }
        catch { /* e.g. Windows/Mac without a vibration motor */ }
        return Task.CompletedTask;
    }

    [JSInvokable("NativeRequestNotificationPermission")]
    public static async Task<string> RequestNotificationPermissionAsync()
    {
#if IOS || MACCATALYST
        var center = UNUserNotificationCenter.Current;
        var settings = await center.GetNotificationSettingsAsync();
        switch (settings.AuthorizationStatus)
        {
            case UNAuthorizationStatus.Authorized:
            case UNAuthorizationStatus.Provisional:
                return "granted";
            case UNAuthorizationStatus.Denied:
                return "denied";
        }
        var (granted, _) = await center.RequestAuthorizationAsync(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        return granted ? "granted" : "denied";
#elif ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            return status == PermissionStatus.Granted ? "granted" : "denied";
        }
        return "granted"; // before Android 13 there's no runtime permission
#else
        return await Task.FromResult("unsupported");
#endif
    }

    [JSInvokable("NativeGetNotificationPermissionStatus")]
    public static async Task<string> GetNotificationPermissionStatusAsync()
    {
#if IOS || MACCATALYST
        var settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync();
        return settings.AuthorizationStatus switch
        {
            UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional => "granted",
            UNAuthorizationStatus.Denied => "denied",
            _ => "default",
        };
#elif ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            return status switch
            {
                PermissionStatus.Granted => "granted",
                PermissionStatus.Denied => "default", // re-requesting is allowed on Android
                _ => "default",
            };
        }
        return "granted";
#else
        return await Task.FromResult("unsupported");
#endif
    }

    [JSInvokable("NativeShowNotification")]
    public static async Task ShowNotificationAsync(string title, string body)
    {
#if IOS || MACCATALYST
        var content = new UNMutableNotificationContent
        {
            Title = title ?? "",
            Body = body ?? "",
            Sound = UNNotificationSound.Default,
        };
        // Trigger null = deliver immediately. The foreground banner is handled by the
        // NotificationDelegate in the AppDelegate (WillPresentNotification).
        var request = UNNotificationRequest.FromIdentifier(
            Guid.NewGuid().ToString("N"), content, trigger: null);
        try { await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request); }
        catch { /* no permission - same silence as the web variant */ }
#elif ANDROID
        var context = Android.App.Application.Context;
        EnsureChannel(context);

        var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        PendingIntent? contentIntent = null;
        if (launchIntent != null)
        {
            launchIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            contentIntent = PendingIntent.GetActivity(context, 0, launchIntent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }

        // Icon via GetIdentifier instead of the Resource designer class - the launcher
        // icon generated by MAUI is always named "appicon".
        var iconId = context.Resources!.GetIdentifier("appicon", "mipmap", context.PackageName);
        // No fluent chaining: the AndroidX bindings annotate the builder return values as
        // nullable, each chained step would produce a CS8602 warning.
        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetContentTitle(title ?? "");
        builder.SetContentText(body ?? "");
        builder.SetSmallIcon(iconId != 0 ? iconId : global::Android.Resource.Drawable.IcDialogInfo);
        builder.SetAutoCancel(true);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        if (contentIntent != null)
            builder.SetContentIntent(contentIntent);

        try { NotificationManagerCompat.From(context)!.Notify(Interlocked.Increment(ref _notificationId), builder.Build()!); }
        catch { /* Notifications disabled - silent no-op like on the web */ }
        await Task.CompletedTask;
#else
        await Task.CompletedTask;
#endif
    }

    [JSInvokable("NativeSetAppBadge")]
    public static Task SetAppBadgeAsync(int count)
    {
#if IOS || MACCATALYST
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
                    UNUserNotificationCenter.Current.SetBadgeCount(count, completionHandler: null);
#pragma warning disable CA1422 // Fallback for iOS 15 (SupportedOSPlatformVersion)
                else
                    UIKit.UIApplication.SharedApplication.ApplicationIconBadgeNumber = count;
#pragma warning restore CA1422
            }
            catch { /* no badge permission */ }
        });
#endif
        // Android: launcher-dependent, no standardized API - deliberately a no-op (like on
        // the web on platforms without a badging API). Windows: dev target only.
        return Task.CompletedTask;
    }

#if ANDROID
    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) != null) return;
        manager?.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "StudyLife", NotificationImportance.High)
            {
                Description = "Timer- und Session-Benachrichtigungen",
            });
    }
#endif
}
