using System.Text.Json;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// Writes the data snapshot for the home screen widget (StudyTodayWidget.swift) to the
/// App Group container and then triggers the timeline reload. The widget deliberately makes
/// no network calls of its own (no session token outside the app process) - it always shows
/// the state as of the last app contact; day/week rollover is zeroed out on the Swift side.
/// Metric semantics match the dashboard exactly: "studied" = StudyMetrics.IsStudied,
/// streak = StudyMetrics.CalcStreak.
/// </summary>
public static class HomeWidgetSnapshot
{
    public const string AppGroupId = "group.app.studylife.mobile";
    public const string SnapshotFileName = "widget-snapshot.json";
    private const int UpcomingActivityLeadMinutes = 15;

    /// <summary>Which planned session the "next session starts soon" Live Activity currently
    /// shows (null = none) - a plain static field is enough since UpdateAsync is called fresh
    /// from several AppRoot.razor call sites and this only needs to de-dup repeat calls for the
    /// SAME session, not persist across app restarts (ActivityKit itself already survives those).</summary>
    private static DateTime? _upcomingActivityStartsAt;

    public static async Task UpdateAsync(AppStateService state, TimerService? timer = null, bool justCompleted = false, bool standNudge = false)
    {
#if IOS || ANDROID
        try
        {
            var sessions = await state.GetSessionsAsync();
            var now = DateTime.Now;
            var today = now.Date;
            var weekStart = StudyMetrics.WeekStartOf(now);

            // IsStudied operates on the DTO - here the same formula is applied to the client model.
            // Deliberately ONLY completed/finished sessions: the elapsed portion of the study
            // session CURRENTLY in progress is added by the Swift side itself, minute by minute
            // (baked(_:at:) in StudyTodayWidget.swift, fed from currentStartsAt/-EndsAt)
            // - counting it here as well would double it.
            var studied = sessions.Where(s => s.IsCompleted || s.EndTime <= now).ToList();
            var todayMinutes = (int)studied
                .Where(s => s.StartTime.Date == today)
                .Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
            var weekMinutes = (int)studied
                .Where(s => s.StartTime.Date >= weekStart)
                .Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
            // Streak needs a much longer look-back than GetSessionsAsync() provides (api/sessions
            // only covers -7d/+90d, see SessionsController.GetAll) - a real streak longer than a
            // week would silently truncate to whatever fits in that window. Same long-range query
            // and HistoryDays as Index.razor.cs's own streak calculation, so both agree.
            var streakHistory = await state.GetJsonCachedAsync<List<StudySessionDto>>(
                "api/sessions/history?days=400&onlyCompleted=false") ?? new();
            var streak = StudyMetrics.CalcStreak(
                streakHistory.Where(s => StudyMetrics.IsStudied(s, now)).Select(s => s.StartTime), today);
            // Fire-and-forget: a missed/delayed celebration notification must never hold up
            // the widget snapshot write.
            _ = StreakMilestones.CheckAndCelebrateAsync(streak);
            var next = sessions
                .Where(s => !s.IsCompleted && s.StartTime > now)
                .OrderBy(s => s.StartTime)
                .FirstOrDefault();
            // Currently running planned study session (calendar session): the widget shows it
            // in the stats view as the "Now" line (takes priority over "next session").
            var current = sessions
                .Where(s => !s.IsCompleted && s.StartTime <= now && s.EndTime > now)
                .OrderBy(s => s.EndTime)
                .FirstOrDefault();
            // Watch stats view "plan for the rest of the week": every remaining planned
            // session through the end of the current week (not just the single nearest
            // "next" one) - sessions' -7d/+90d window comfortably covers this.
            var weekEnd = weekStart.AddDays(7);
            var upcomingSessions = sessions
                .Where(s => !s.IsCompleted && s.StartTime > now && s.StartTime.Date < weekEnd)
                .OrderBy(s => s.StartTime)
                .ToList();
            // Watch mode picker (Phase "alles ausser Siri"): same built-in + custom combination
            // Focus.razor uses, so picking a mode on the Watch offers exactly the same choices
            // as picking one on the phone.
            var settings = await state.GetSettingsAsync();
            var modes = CustomTimerModes.Combined(settings.CustomTimerModes);
            // Weekly goal ring: same "reference" midpoint StudyMetrics' own pace-ratio calc uses
            // for WeeklyGoalMinHours/MaxHours, so the Watch's ring agrees with the app's pacing logic.
            var weeklyGoalMinutes = (int)((settings.WeeklyGoalMinHours + settings.WeeklyGoalMaxHours) / 2.0 * 60);
            // Watch recent-sessions list: the near-term GetSessionsAsync() window (-7d/+90d) is
            // plenty for "recent" (unlike streak, which genuinely needs the long-range query above).
            var recentSessions = studied.OrderByDescending(s => s.StartTime).Take(5).ToList();

            // Course progress widget: same overall-completion metric as the Dashboard
            // (CourseCatalog.CalcTotalEcts/CalcEctsEarned), plus per-course all-time hours
            // (streakHistory's long-range window, not the narrow recentSessions one) for the
            // active (non-completed) courses studied the most.
            var allCourses = await state.GetCoursesAsync();
            var groupQuotas = await state.GetActiveGroupQuotasAsync();
            var ectsTotal = CourseCatalog.CalcTotalEcts(allCourses, groupQuotas);
            var ectsEarned = CourseCatalog.CalcEctsEarned(allCourses, settings.CompletedCourseIds, groupQuotas);
            var courseHours = streakHistory
                .Where(s => StudyMetrics.IsStudied(s, now) && !settings.CompletedCourseIds.Contains(s.CourseId))
                .GroupBy(s => s.CourseId)
                .Select(g => (CourseId: g.Key, Hours: g.Sum(s => (s.EndTime - s.StartTime).TotalHours)))
                .OrderByDescending(g => g.Hours)
                .Take(4)
                .Join(allCourses, g => g.CourseId, c => c.Id, (g, c) => (c.Name, c.Color, g.Hours))
                .ToList();

            // Watch stats view weekly bar chart: last 7 days' totals (oldest first), same
            // "completed sessions only" rule as todayMinutes/weekMinutes above - `studied`'s
            // -7d/+90d window already fully covers this range, no need for streakHistory here.
            var dailyMinutes = Enumerable.Range(-6, 7)
                .Select(offset => today.AddDays(offset))
                .Select(day => (Day: day, Minutes: (int)studied
                    .Where(s => s.StartTime.Date == day)
                    .Sum(s => (s.EndTime - s.StartTime).TotalMinutes)))
                .ToList();
            // Watch stats view "vs. last week" comparison: needs streakHistory (the 7d/90d
            // `sessions` window only reaches 7 days back, not the 7-14-days-ago range).
            var weekMinutesPrevious = (int)streakHistory
                .Where(s => StudyMetrics.IsStudied(s, now)
                    && s.StartTime.Date >= weekStart.AddDays(-7) && s.StartTime.Date < weekStart)
                .Sum(s => (s.EndTime - s.StartTime).TotalMinutes);
            // Watch stats view "insgesamt gelernt": all-time total across every studied
            // session in streakHistory's window, not just the top-4 active courses above.
            var allTimeHours = streakHistory
                .Where(s => StudyMetrics.IsStudied(s, now))
                .Sum(s => (s.EndTime - s.StartTime).TotalHours);

            // Utf8JsonWriter instead of JsonSerializer<T>: reflection-free and therefore immune to
            // the IL trimming of the iOS release build (same lesson as the capabilities
            // query in Setup.razor).
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("day", now.ToString("yyyy-MM-dd"));
                json.WriteNumber("todayMinutes", todayMinutes);
                json.WriteNumber("weekMinutes", weekMinutes);
                json.WriteNumber("weeklyGoalMinutes", weeklyGoalMinutes);
                json.WriteNumber("streakDays", streak);
                json.WriteNumber("ectsEarned", ectsEarned);
                json.WriteNumber("ectsTotal", ectsTotal);
                json.WriteNumber("weekMinutesPrevious", weekMinutesPrevious);
                json.WriteNumber("allTimeHours", Math.Round(allTimeHours, 1));
                // Watch rating prompt: an unambiguous "a focus round just completed" signal -
                // unlike the running/not-running diff (which can't tell completed apart from
                // paused/stopped), this only ever gets a fresh value from HandleSessionCompleteForWidget.
                if (justCompleted)
                    json.WriteNumber("sessionCompletedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                // Stand/stretch nudge for long focus rounds (>=45min, every 45min) - same
                // unambiguous one-shot-timestamp pattern as sessionCompletedAt, so the Watch
                // can diff it and fire a haptic/notification exactly once per nudge.
                if (standNudge)
                    json.WriteNumber("standNudgeAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (next is not null)
                {
                    var title = string.IsNullOrWhiteSpace(next.Topic) ? next.CourseName : next.Topic!;
                    json.WriteString("nextTitle", title);
                    json.WriteNumber("nextStartsAt",
                        new DateTimeOffset(next.StartTime).ToUnixTimeSeconds());
                }
                if (current is not null)
                {
                    var title = string.IsNullOrWhiteSpace(current.Topic) ? current.CourseName : current.Topic!;
                    json.WriteString("currentTitle", title);
                    json.WriteNumber("currentStartsAt",
                        new DateTimeOffset(current.StartTime).ToUnixTimeSeconds());
                    json.WriteNumber("currentEndsAt",
                        new DateTimeOffset(current.EndTime).ToUnixTimeSeconds());
                }
                // Running timer (not paused): phase end derived from SecondsLeft -
                // the wall-clock-based TimerService keeps the value exact. The widget then
                // counts down on its own via Text(timerInterval:).
                if (timer is { IsRunning: true })
                {
                    json.WriteBoolean("timerRunning", true);
                    json.WriteBoolean("timerIsBreak", timer.IsBreak);
                    json.WriteNumber("timerEndsAt",
                        DateTimeOffset.UtcNow.AddSeconds(timer.SecondsLeft).ToUnixTimeSeconds());
                }
                json.WriteStartArray("modes");
                foreach (var mode in modes)
                {
                    json.WriteStartObject();
                    json.WriteNumber("id", mode.Id);
                    json.WriteString("name", mode.Name);
                    json.WriteString("emoji", mode.Emoji);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("recentSessions");
                foreach (var session in recentSessions)
                {
                    var title = string.IsNullOrWhiteSpace(session.Topic) ? session.CourseName : session.Topic!;
                    json.WriteStartObject();
                    json.WriteString("title", title);
                    json.WriteNumber("startsAt", new DateTimeOffset(session.StartTime).ToUnixTimeSeconds());
                    json.WriteNumber("minutes", (int)(session.EndTime - session.StartTime).TotalMinutes);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("courses");
                foreach (var course in courseHours)
                {
                    json.WriteStartObject();
                    json.WriteString("name", course.Name);
                    json.WriteString("color", course.Color);
                    json.WriteNumber("hours", Math.Round(course.Hours, 1));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("upcomingSessions");
                foreach (var session in upcomingSessions)
                {
                    var title = string.IsNullOrWhiteSpace(session.Topic) ? session.CourseName : session.Topic!;
                    json.WriteStartObject();
                    json.WriteString("title", title);
                    json.WriteNumber("startsAt", new DateTimeOffset(session.StartTime).ToUnixTimeSeconds());
                    json.WriteNumber("minutes", (int)(session.EndTime - session.StartTime).TotalMinutes);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("dailyMinutes");
                foreach (var d in dailyMinutes)
                {
                    json.WriteStartObject();
                    json.WriteString("day", d.Day.ToString("yyyy-MM-dd"));
                    json.WriteNumber("minutes", d.Minutes);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
#if IOS
            // iOS: widget runs as its own extension process - handed over via App Group.
            var container = Foundation.NSFileManager.DefaultManager.GetContainerUrl(AppGroupId);
            if (container?.Path is not string dir) return; // Entitlement missing (e.g. simulator without a profile)
            await File.WriteAllBytesAsync(Path.Combine(dir, SnapshotFileName), stream.ToArray());
            TimerLiveActivity.ReloadHomeWidgets();
            // Same payload, relayed to a paired Apple Watch (if any) - see WatchBridge.cs.
            WatchBridge.PushSnapshot(stream.ToArray());

            // "Next session starts soon" Live Activity: skipped while a focus timer is
            // actively running - a countdown to a session you're already mid-way through
            // studying would just be noise on top of the timer's own Live Activity.
            var timerActive = timer is { IsRunning: true };
            if (!timerActive && next != null && next.StartTime <= now.AddMinutes(UpcomingActivityLeadMinutes))
            {
                if (_upcomingActivityStartsAt != next.StartTime)
                {
                    var upcomingTitle = string.IsNullOrWhiteSpace(next.Topic) ? next.CourseName : next.Topic!;
                    UpcomingSessionActivity.Start(upcomingTitle, new DateTimeOffset(next.StartTime));
                    _upcomingActivityStartsAt = next.StartTime;
                }
            }
            else if (_upcomingActivityStartsAt != null)
            {
                UpcomingSessionActivity.End();
                _upcomingActivityStartsAt = null;
            }
#else
            // Android: the AppWidgetProvider runs in the app process - the normal app data
            // directory is enough; afterwards re-render all widget instances directly.
            var dir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
            await File.WriteAllBytesAsync(Path.Combine(dir, SnapshotFileName), stream.ToArray());
            StudyTodayWidgetProvider.UpdateAll(global::Android.App.Application.Context);
#endif
        }
        catch { /* Widget update is best effort - app functionality doesn't depend on it */ }
#else
        await Task.CompletedTask;
#endif
    }
}
