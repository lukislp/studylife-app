#if IOS
using BackgroundTasks;
using Foundation;
#endif

namespace StudyLife.App.Services;

/// <summary>
/// iOS background refresh (BGAppRefreshTask): occasionally wakes the app to warm the data
/// caches (AppStateService) so the next app launch already shows fresh data.
/// Best effort by definition - WHEN iOS grants the task is decided by the system based on
/// usage patterns; no feature may rely on it. The actual refresh code comes from AppRoot
/// (SetHandler), because that's the only place where the Blazor service scope with
/// HttpClient/session exists. Android is deliberately left out (the Chronometer notification
/// and refresh-on-open cover the need there).
/// </summary>
public static class BackgroundRefresh
{
    private static Func<Task>? _handler;

    /// <summary>Set by AppRoot after boot (null on dispose).</summary>
    public static void SetHandler(Func<Task>? handler) => _handler = handler;

#if IOS
    private const string TaskId = "app.studylife.mobile.refresh";

    /// <summary>MUST run inside FinishedLaunching (Apple's requirement for Register).</summary>
    public static void Register()
    {
        try
        {
            BGTaskScheduler.Shared.Register(TaskId, null, task => Handle((BGAppRefreshTask)task));
            Schedule();
        }
        catch { /* Identifier missing from Info.plist or similar - app keeps running without BG refresh */ }
    }

    public static void Schedule()
    {
        try
        {
            var request = new BGAppRefreshTaskRequest(TaskId)
            {
                // At the earliest in 30 minutes - iOS decides the actual timing itself.
                EarliestBeginDate = NSDate.FromTimeIntervalSinceNow(30 * 60),
            };
            BGTaskScheduler.Shared.Submit(request, out _);
        }
        catch { /* Submit can fail e.g. in the simulator - doesn't matter */ }
    }

    private static async void Handle(BGAppRefreshTask task)
    {
        Schedule(); // immediately register the next run again

        using var cancellation = new CancellationTokenSource();
        task.ExpirationHandler = cancellation.Cancel;
        try
        {
            var handler = _handler;
            if (handler != null)
                await handler().WaitAsync(TimeSpan.FromSeconds(20), cancellation.Token);
            task.SetTaskCompleted(true);
        }
        catch
        {
            task.SetTaskCompleted(false);
        }
    }
#endif
}
