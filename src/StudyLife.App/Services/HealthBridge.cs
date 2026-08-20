using System.Runtime.InteropServices;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift HealthKit bridge (native/ios-liveactivity/HealthBridge.swift): logs
/// completed focus rounds as Mindful Session samples (write), and reads recent Heart Rate
/// Variability for the Dashboard's readiness-score tile (INativeHealthData, studylife repo).
/// Same no-op-by-default rules as TimerLiveActivity/WatchBridge - only active with the
/// LIVE_ACTIVITY define (static lib present at build time).
/// </summary>
public static class HealthBridge
{
    public static bool IsAvailable
    {
        get
        {
#if LIVE_ACTIVITY && IOS
            try { return slla_health_is_available() != 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

#if LIVE_ACTIVITY && IOS
    private static TaskCompletionSource<bool>? _authCompletion;
#endif

    /// <summary>Requests Mindful Session write + HRV read authorization together, once, at app
    /// startup. Denied/undetermined just means LogMindfulSession/GetRecentHrvAsync silently
    /// no-op afterward.</summary>
    public static Task<bool> RequestAuthorizationAsync()
    {
#if LIVE_ACTIVITY && IOS
        _authCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_health_request_authorization(&OnAuthorizationResult); }
            catch { _authCompletion.TrySetResult(false); }
        }
        return _authCompletion.Task;
#else
        return Task.FromResult(false);
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnAuthorizationResult(int granted) => _authCompletion?.TrySetResult(granted != 0);
#endif

    /// <summary>Logs a completed focus round as a Mindful Session.</summary>
    public static void LogMindfulSession(DateTime startUtc, DateTime endUtc)
    {
#if LIVE_ACTIVITY && IOS
        try
        {
            slla_health_log_mindful_session(
                new DateTimeOffset(startUtc).ToUnixTimeSeconds(),
                new DateTimeOffset(endUtc).ToUnixTimeSeconds());
        }
        catch { /* bridge unavailable / no authorization - session just isn't logged to Health */ }
#endif
    }

#if LIVE_ACTIVITY && IOS
    private static TaskCompletionSource<double[]>? _hrvCompletion;
#endif

    /// <summary>Daily HRV (SDNN, ms) for the last <paramref name="days"/> days, oldest first -
    /// see INativeHealthData.GetRecentHrvAsync (studylife repo) for the exact contract. Empty
    /// array (not null) on denied/undetermined authorization or a query error - the caller
    /// (Platforms/iOS's INativeHealthData implementation) treats an empty result the same way
    /// as "not enough data" either way, so no separate null case is needed here.</summary>
    public static Task<double[]> GetRecentHrvAsync(int days)
    {
#if LIVE_ACTIVITY && IOS
        _hrvCompletion = new TaskCompletionSource<double[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_health_get_recent_hrv(days, &OnHrvResult); }
            catch { _hrvCompletion.TrySetResult(Array.Empty<double>()); }
        }
        return _hrvCompletion.Task;
#else
        return Task.FromResult(Array.Empty<double>());
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static unsafe void OnHrvResult(double* values, int count)
    {
        // Copy immediately - the pointer is only valid for the duration of this call (see the
        // matching comment in HealthBridge.swift), not retained afterward.
        var managed = count > 0 && values != null ? new ReadOnlySpan<double>(values, count).ToArray() : Array.Empty<double>();
        _hrvCompletion?.TrySetResult(managed);
    }
#endif

#if LIVE_ACTIVITY && IOS
    [DllImport("__Internal")]
    private static extern int slla_health_is_available();

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_request_authorization(delegate* unmanaged<int, void> handler);

    [DllImport("__Internal")]
    private static extern void slla_health_log_mindful_session(double startEpoch, double endEpoch);

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_get_recent_hrv(int days, delegate* unmanaged<double*, int, void> handler);
#endif
}
