using System.Text.Json;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace StudyLife.App;

/// <summary>
/// Home screen widget "Study Progress" - Android counterpart to StudyTodayWidget.swift.
/// Runs in the app process (AppWidgetProvider is a BroadcastReceiver), so it reads the
/// snapshot from HomeWidgetSnapshot directly and recomputes it to "now" the same way the
/// Swift side does (day/week rollover, elapsed portion of ongoing study sessions, dropping
/// expired data). Live counting is handled by the system Chronometer (same principle as the
/// timer notification): countdown while a focus timer is running, counting up the day's study
/// time during an ongoing study session - both keep ticking without the app being awake.
/// Limitation compared to iOS: there is no precomputed timeline, so a state change (phase end)
/// only becomes visible on the next update (app contact or the 30-minute system tick from
/// studylife_widget_info.xml).
/// </summary>
[BroadcastReceiver(Label = "Lernfortschritt", Exported = true)]
[IntentFilter(new[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
[MetaData("android.appwidget.provider", Resource = "@xml/studylife_widget_info")]
public class StudyTodayWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null) return;
        foreach (var id in appWidgetIds)
        {
            try { appWidgetManager.UpdateAppWidget(id, BuildViews(context)); }
            catch { /* Widget rendering is best effort - never crash the app/the receiver */ }
        }
    }

    /// <summary>Called by HomeWidgetSnapshot after every snapshot write.</summary>
    public static void UpdateAll(Context context)
    {
        var manager = AppWidgetManager.GetInstance(context);
        if (manager is null) return;
        var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(StudyTodayWidgetProvider)));
        var ids = manager.GetAppWidgetIds(component);
        if (ids is { Length: > 0 })
            new StudyTodayWidgetProvider().OnUpdate(context, manager, ids);
    }

    private static RemoteViews BuildViews(Context context)
    {
        var views = new RemoteViews(context.PackageName, Resource.Layout.widget_study_today);

        // Tapping opens the app (like the iOS widget).
        var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (launchIntent is not null)
        {
            launchIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            var pending = PendingIntent.GetActivity(context, 4712, launchIntent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            views.SetOnClickPendingIntent(Resource.Id.widget_header, pending);
            views.SetOnClickPendingIntent(Resource.Id.widget_value, pending);
            views.SetOnClickPendingIntent(Resource.Id.widget_chrono, pending);
            views.SetOnClickPendingIntent(Resource.Id.widget_subtitle, pending);
            views.SetOnClickPendingIntent(Resource.Id.widget_footer, pending);
        }

        var snapshot = LoadBakedSnapshot();
        if (snapshot is null)
        {
            views.SetViewVisibility(Resource.Id.widget_chrono, global::Android.Views.ViewStates.Gone);
            views.SetViewVisibility(Resource.Id.widget_value, global::Android.Views.ViewStates.Gone);
            views.SetTextViewText(Resource.Id.widget_header, "✦ StudyLife");
            views.SetTextViewText(Resource.Id.widget_subtitle, "App öffnen, um Daten zu laden");
            views.SetTextViewText(Resource.Id.widget_footer, "");
            return views;
        }

        var s = snapshot.Value;
        var now = DateTimeOffset.Now;
        var footer = s.StreakDays > 0
            ? $"🔥 {s.StreakDays} {(s.StreakDays == 1 ? "Tag" : "Tage")} · Woche {FormatMinutes(s.WeekMinutes)}"
            : $"Woche {FormatMinutes(s.WeekMinutes)}";

        if (s.TimerEndsAt is { } timerEnd && timerEnd > now && s.TimerRunning)
        {
            // Focus timer is running: the system Chronometer counts down the phase itself
            // (SetChronometerCountDown from API 24 - our minimum is above that).
            views.SetTextViewText(Resource.Id.widget_header,
                s.TimerIsBreak ? "✦ Pause ☕" : "✦ Fokus läuft");
            views.SetViewVisibility(Resource.Id.widget_value, global::Android.Views.ViewStates.Gone);
            views.SetViewVisibility(Resource.Id.widget_chrono, global::Android.Views.ViewStates.Visible);
            views.SetChronometerCountDown(Resource.Id.widget_chrono, true);
            views.SetChronometer(Resource.Id.widget_chrono,
                SystemClock.ElapsedRealtime() + (long)(timerEnd - now).TotalMilliseconds, null, true);
            views.SetTextViewText(Resource.Id.widget_subtitle, $"Heute {FormatMinutes(s.TodayMinutes)} gelernt");
        }
        else if (s.CurrentTitle is not null && s.CurrentEndsAt is { } currentEnd && currentEnd > now)
        {
            // Planned study session is running: the day's study time counts up live (the
            // Chronometer base is shifted into the past by the time already studied). Keeps
            // counting after the session ends until the next update - an accepted Android limitation.
            views.SetTextViewText(Resource.Id.widget_header, $"▶ {s.CurrentTitle}");
            views.SetViewVisibility(Resource.Id.widget_value, global::Android.Views.ViewStates.Gone);
            views.SetViewVisibility(Resource.Id.widget_chrono, global::Android.Views.ViewStates.Visible);
            views.SetChronometerCountDown(Resource.Id.widget_chrono, false);
            views.SetChronometer(Resource.Id.widget_chrono,
                SystemClock.ElapsedRealtime() - (long)TimeSpan.FromMinutes(s.TodayMinutes).TotalMilliseconds,
                null, true);
            views.SetTextViewText(Resource.Id.widget_subtitle, $"Lernzeit bis {currentEnd.ToLocalTime():HH:mm}");
        }
        else
        {
            views.SetTextViewText(Resource.Id.widget_header, "✦ Heute");
            views.SetViewVisibility(Resource.Id.widget_chrono, global::Android.Views.ViewStates.Gone);
            views.SetViewVisibility(Resource.Id.widget_value, global::Android.Views.ViewStates.Visible);
            views.SetTextViewText(Resource.Id.widget_value, FormatMinutes(s.TodayMinutes));
            views.SetTextViewText(Resource.Id.widget_subtitle, "gelernt");
            if (s.NextTitle is not null && s.NextStartsAt is { } nextStart && nextStart > now)
                footer += $" · → {FormatNext(nextStart)} {s.NextTitle}";
        }
        views.SetTextViewText(Resource.Id.widget_footer, footer);
        return views;
    }

    private readonly record struct BakedSnapshot(
        int TodayMinutes, int WeekMinutes, int StreakDays,
        string? NextTitle, DateTimeOffset? NextStartsAt,
        string? CurrentTitle, DateTimeOffset? CurrentEndsAt,
        bool TimerRunning, bool TimerIsBreak, DateTimeOffset? TimerEndsAt);

    /// <summary>Counterpart to loadSnapshot+baked in StudyTodayWidget.swift: read the raw JSON,
    /// zero out on rollover, add in the elapsed portion of the ongoing study session.</summary>
    private static BakedSnapshot? LoadBakedSnapshot()
    {
        try
        {
            var path = Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory,
                StudyLife.App.Services.HomeWidgetSnapshot.SnapshotFileName);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;

            int GetInt(string name) => root.TryGetProperty(name, out var p) ? p.GetInt32() : 0;
            string? GetString(string name) => root.TryGetProperty(name, out var p) ? p.GetString() : null;
            DateTimeOffset? GetEpoch(string name) => root.TryGetProperty(name, out var p)
                ? DateTimeOffset.FromUnixTimeSeconds(p.GetInt64()) : null;

            var now = DateTimeOffset.Now;
            var today = DateTime.Now.Date;
            var todayMinutes = GetInt("todayMinutes");
            var weekMinutes = GetInt("weekMinutes");

            // Day/week rollover (week starts on Monday, like StudyMetrics.WeekStartOf).
            if (DateTime.TryParse(GetString("day"), out var snapshotDay) && snapshotDay.Date != today)
            {
                todayMinutes = 0;
                var offset = ((int)today.DayOfWeek + 6) % 7;
                if (snapshotDay.Date < today.AddDays(-offset)) weekMinutes = 0;
            }

            var currentStart = GetEpoch("currentStartsAt");
            var currentEnd = GetEpoch("currentEndsAt");
            if (currentStart is { } start && currentEnd is { } end)
            {
                var accrued = (int)(((currentEnd < now ? end : now) - start).TotalMinutes);
                if (accrued > 0) { todayMinutes += accrued; weekMinutes += accrued; }
            }

            return new BakedSnapshot(
                todayMinutes, weekMinutes, GetInt("streakDays"),
                GetString("nextTitle"), GetEpoch("nextStartsAt"),
                currentEnd is { } ce && ce > now ? GetString("currentTitle") : null, currentEnd,
                root.TryGetProperty("timerRunning", out var tr) && tr.GetBoolean(),
                root.TryGetProperty("timerIsBreak", out var tb) && tb.GetBoolean(),
                GetEpoch("timerEndsAt"));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatMinutes(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}:{minutes % 60:D2} h" : $"{minutes} min";

    private static string FormatNext(DateTimeOffset start)
    {
        var local = start.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("ddd HH:mm", new System.Globalization.CultureInfo("de-DE"));
    }
}
