using Foundation;
using StudyLife.App.Services;
using UIKit;
using UserNotifications;
using WebKit;

namespace StudyLife.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        ClearStaleWebViewHttpCacheOnce();

        // BGTask registration MUST happen before FinishedLaunching returns.
        BackgroundRefresh.Register();

        var result = base.FinishedLaunching(application, launchOptions);
        // Without a delegate, iOS doesn't show notifications while the app is in the
        // foreground - but that's exactly when the focus timer fires ("session done").
        UNUserNotificationCenter.Current.Delegate = new ForegroundNotificationDelegate();
        return result;
    }

    // Spotlight hit tapped (SpotlightIndexer): navigate to the matching list.
    [Export("application:continueUserActivity:restorationHandler:")]
    public bool ContinueUserActivity(UIApplication application, NSUserActivity userActivity, UIApplicationRestorationHandler completionHandler)
    {
        if (userActivity.ActivityType == CoreSpotlight.CSSearchableItem.ActionType
            && userActivity.UserInfo?[CoreSpotlight.CSSearchableItem.ActivityIdentifier] is NSString identifier)
        {
            var route = identifier.ToString().StartsWith(SpotlightIndexer.CoursePrefix, StringComparison.Ordinal)
                ? "/setup"
                : "/notes";
            IPlatformApplication.Current?.Services.GetService<DeepLinkService>()?.Dispatch(route);
            return true;
        }
        return false;
    }

    // Shared/opened .ics files ("Open with StudyLife", CFBundleDocumentTypes):
    // read the content, put it in the intake, navigate to the calendar page - which picks up
    // the file and starts the existing import review flow. Other URLs are passed through to MAUI.
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (url.IsFileUrl && url.Path is { } path && path.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var scoped = url.StartAccessingSecurityScopedResource();
                try
                {
                    var bytes = System.IO.File.ReadAllBytes(path);
                    NativeIcsIntake.SetPending(System.IO.Path.GetFileName(path), bytes);
                }
                finally
                {
                    if (scoped) url.StopAccessingSecurityScopedResource();
                }
                IPlatformApplication.Current?.Services.GetService<DeepLinkService>()?.Dispatch("/calendar");
                return true;
            }
            catch { /* unreadable - better to ignore silently than to crash */ }
        }

        // Widget taps (widgetURL/link in StudyTodayWidget.swift) arrive as
        // studylife://shortcut/<route> - same routes as the quick actions.
        // studylife://auth (WebAuthenticator return) falls through to base.
        if (string.Equals(url.Scheme, "studylife", StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.Host, "shortcut", StringComparison.OrdinalIgnoreCase))
        {
            var route = url.Path switch
            {
                "/focus" => "/focus",
                "/calendar" => "/calendar",
                "/notes" => "/notes?new=1",
                _ => null,
            };
            if (route != null)
            {
                IPlatformApplication.Current?.Services.GetService<DeepLinkService>()?.Dispatch(route);
                return true;
            }
        }

        return base.OpenUrl(application, url, options);
    }

    // APNs registration (only ever reachable with the aps-environment entitlement, see
    // NativePush/ApnsTokenStore): pass the token as hex through to the store. Export selectors
    // instead of override - MauiUIApplicationDelegate doesn't expose these delegate methods virtually.
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
        => ApnsTokenStore.SetToken(Convert.ToHexString(deviceToken.ToArray()).ToLowerInvariant());

    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
        => ApnsTokenStore.SetFailed();

    // Home screen quick action (long-press the icon). iOS also calls this on a cold start
    // (FinishedLaunching returns true); DeepLinkService then buffers it until Blazor is ready.
    public override void PerformActionForShortcutItem(UIApplication application,
        UIApplicationShortcutItem shortcutItem, UIOperationHandler completionHandler)
    {
        var route = shortcutItem.Type switch
        {
            "app.studylife.mobile.focus" => "/focus",
            "app.studylife.mobile.newnote" => "/notes?new=1",
            "app.studylife.mobile.calendar" => "/calendar",
            "app.studylife.mobile.stats" => "/auswertung",
            "app.studylife.mobile.weekplan" => "/weekplan",
            _ => null,
        };

        if (route != null)
            IPlatformApplication.Current?.Services.GetService<DeepLinkService>()?.Dispatch(route);

        completionHandler(route != null);
    }

    /// <summary>
    /// Bug found live: a Blazor Hybrid app's C# HttpClient calls (AppStateService, SessionHandler)
    /// do NOT go through the WebView's own resource loader - they run natively via
    /// NSUrlSessionHandler (NSURLSession), which has its OWN separate HTTP cache
    /// (NSUrlCache.SharedCache). WKWebsiteDataStore (cleared here first, epoch 1) only covers
    /// page/asset loads INSIDE the WebView (index.html, static assets) - it does nothing for API
    /// responses fetched via HttpClient, which is exactly the data path the stats heatmap uses.
    /// This survives every devicectl reinstall unchanged (same app container, which is also why
    /// login persists across the 7-day free-signing renewal in sign-and-install.sh), with no
    /// user-facing way to clear it short of fully deleting the app. Confirmed live: the exact
    /// same shared Blazor code showed correct, current data on Windows (different HttpClient
    /// transport entirely) but stayed frozen on iOS through a full devicectl reinstall AND an
    /// epoch-1 WKWebsiteDataStore clear - proving the WebView-level cache was never the real
    /// culprit for this specific data path.
    /// Cleared once per epoch bump (NSUserDefaults marker) rather than on every launch, to avoid
    /// discarding a warm cache pointlessly on every cold start. Deliberately NOT
    /// LocalStorage/Cookies/IndexedDB - those hold the session token and the offline read cache
    /// (AppStateService), which must survive (see SessionTokenStore's "no pointless logout"
    /// comment).
    /// </summary>
    private static void ClearStaleWebViewHttpCacheOnce()
    {
        const string epochKey = "webkit-http-cache-epoch";
        const int currentEpoch = 2;
        var defaults = NSUserDefaults.StandardUserDefaults;
        if (defaults.IntForKey(epochKey) >= currentEpoch) return;

        var types = new NSSet<NSString>(WKWebsiteDataType.DiskCache, WKWebsiteDataType.MemoryCache);
        WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypes(types, NSDate.DistantPast, () =>
        {
            defaults.SetInt(currentEpoch, epochKey);
        });

        NSUrlCache.SharedCache.RemoveAllCachedResponses();
    }
}

/// <summary>Shows banner + sound even while the app is in the foreground (default would be: nothing).</summary>
internal sealed class ForegroundNotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(UNUserNotificationCenter center,
        UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
    {
        completionHandler(UNNotificationPresentationOptions.Banner
            | UNNotificationPresentationOptions.Sound
            | UNNotificationPresentationOptions.Badge);
    }
}
