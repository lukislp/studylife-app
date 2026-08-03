#if ANDROID
using Android.App;
using Android.Content;
using AndroidX.Core.App;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// Android counterpart to the iOS Live Activity: the running focus timer as a persistent
/// (ongoing) notification with a system Chronometer - the countdown is rendered by the
/// system and keeps counting even when Android freezes the app (same property as
/// Text(timerInterval:) on iOS). Its own silent channel so that state updates
/// (phase changes/pause) don't ping every time.
/// </summary>
public static class AndroidTimerNotification
{
#if ANDROID
    private const string ChannelId = "studylife-timer";
    private const int NotificationId = 4711;
#endif

    public static void Update(string title, DateTimeOffset endsAt, bool isBreak, bool isPaused, int secondsLeft, int round, int totalRounds)
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            EnsureChannel(context);

            var phase = isPaused ? "Pausiert" : isBreak ? "Pause ☕" : "Fokus";
            var text = $"{phase} · Runde {round} von {totalRounds}";

            var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
            PendingIntent? contentIntent = null;
            if (launchIntent != null)
            {
                launchIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
                contentIntent = PendingIntent.GetActivity(context, 1, launchIntent,
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            }

            var iconId = context.Resources!.GetIdentifier("appicon", "mipmap", context.PackageName);
            var builder = new NotificationCompat.Builder(context, ChannelId);
            builder.SetContentTitle(title);
            builder.SetContentText(text);
            builder.SetSmallIcon(iconId != 0 ? iconId : global::Android.Resource.Drawable.IcDialogInfo);
            builder.SetOngoing(true);
            builder.SetOnlyAlertOnce(true);
            builder.SetVisibility(NotificationCompat.VisibilityPublic);
            builder.SetCategory(NotificationCompat.CategoryProgress);
            if (contentIntent != null) builder.SetContentIntent(contentIntent);

            if (isPaused)
            {
                // Static remainder instead of Chronometer while the timer is stopped.
                builder.SetContentText($"{text} · noch {secondsLeft / 60}:{secondsLeft % 60:00}");
                builder.SetUsesChronometer(false);
            }
            else
            {
                builder.SetWhen(endsAt.ToUnixTimeMilliseconds());
                builder.SetShowWhen(false);
                builder.SetUsesChronometer(true);
                builder.SetChronometerCountDown(true);
            }

            NotificationManagerCompat.From(context)!.Notify(NotificationId, builder.Build()!);
        }
        catch { /* no permission or similar - timer keeps running without lock screen display */ }
#endif
    }

    public static void End()
    {
#if ANDROID
        try { NotificationManagerCompat.From(Android.App.Application.Context)!.Cancel(NotificationId); }
        catch { /* ignore */ }
#endif
    }

#if ANDROID
    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel(ChannelId) != null) return;
        manager?.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "Fokus-Timer", NotificationImportance.Low)
            {
                Description = "Laufender Fokus-Timer auf dem Sperrbildschirm",
            });
    }
#endif
}
