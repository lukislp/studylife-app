#if IOS || MACCATALYST
using Foundation;
using UserNotifications;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// System-held copies of the signed-in account that live outside the app's own files: the URL loading
/// system's response cache (NSURLCache) and the notifications already shown in Notification Center
/// (reminders and reports with course names and topics). Removed on sign-out and account deletion
/// (<see cref="NativeLocalCopies"/>). Only iOS and Mac Catalyst have these; a no-op elsewhere.
/// Notifications that are only scheduled, not yet delivered, are left alone here.
/// </summary>
public static class PlatformCaches
{
    public static void Clear()
    {
#if IOS || MACCATALYST
        NSUrlCache.SharedCache.RemoveAllCachedResponses();
        UNUserNotificationCenter.Current.RemoveAllDeliveredNotifications();
#endif
    }
}