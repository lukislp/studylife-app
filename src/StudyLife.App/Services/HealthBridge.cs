using System.Runtime.InteropServices;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift HealthKit bridge (native/ios-liveactivity/HealthBridge.swift): logs
/// completed focus rounds as Mindful Session samples (write), and reads recent Heart Rate
/// Variability + Sleep Analysis + Step Count + VO2max for the Dashboard's readiness-score/
/// sleep-consistency tiles, the Focus Timer's movement-break nudge, and the Stats page's
/// cardio fitness trend (INativeHealthData, studylife repo). Same no-op-by-default rules as
/// TimerLiveActivity/WatchBridge - only active with the LIVE_ACTIVITY define (static lib
/// present at build time).
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

    /// <summary>Requests Mindful Session write + HRV/Sleep Analysis/Step Count/VO2max read
    /// authorization together, once, at app startup. Denied/undetermined just means
    /// LogMindfulSession/GetRecentHrvAsync/GetRecentSleepOnsetMinutesAsync/GetStepsSinceAsync/
    /// GetCardioFitnessHistoryAsync silently no-op afterward.</summary>
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
    private static TaskCompletionSource<double[]>? _sleepOnsetCompletion;
#endif

    /// <summary>Sleep onset time (minutes after 6pm, wrapping at 24h) for the last
    /// <paramref name="nights"/> nights, oldest first - see
    /// INativeHealthData.GetRecentSleepOnsetMinutesAsync (studylife repo) for the exact
    /// contract. Empty array (not null) on denied/undetermined authorization or a query
    /// error, same "caller treats empty as not-enough-data" convention as GetRecentHrvAsync.</summary>
    public static Task<double[]> GetRecentSleepOnsetMinutesAsync(int nights)
    {
#if LIVE_ACTIVITY && IOS
        _sleepOnsetCompletion = new TaskCompletionSource<double[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_health_get_recent_sleep_onsets(nights, &OnSleepOnsetResult); }
            catch { _sleepOnsetCompletion.TrySetResult(Array.Empty<double>()); }
        }
        return _sleepOnsetCompletion.Task;
#else
        return Task.FromResult(Array.Empty<double>());
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static unsafe void OnSleepOnsetResult(double* values, int count)
    {
        var managed = count > 0 && values != null ? new ReadOnlySpan<double>(values, count).ToArray() : Array.Empty<double>();
        _sleepOnsetCompletion?.TrySetResult(managed);
    }
#endif

#if LIVE_ACTIVITY && IOS
    private static TaskCompletionSource<int?>? _stepsCompletion;
#endif

    /// <summary>Cumulative step count over the last <paramref name="minutesAgo"/> minutes up
    /// to now - see INativeHealthData.GetStepsSinceAsync (studylife repo) for the exact
    /// contract. Null (not 0) on denied/undetermined authorization or a query error, so the
    /// Focus Timer's movement-break nudge (Focus.razor) can tell "we don't know" apart from a
    /// genuine zero steps and skip the nudge in the former case.</summary>
    public static Task<int?> GetStepsSinceAsync(int minutesAgo)
    {
#if LIVE_ACTIVITY && IOS
        _stepsCompletion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_health_get_steps_since(minutesAgo, &OnStepsResult); }
            catch { _stepsCompletion.TrySetResult(null); }
        }
        return _stepsCompletion.Task;
#else
        return Task.FromResult<int?>(null);
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnStepsResult(int success, int steps) => _stepsCompletion?.TrySetResult(success != 0 ? steps : null);
#endif

#if LIVE_ACTIVITY && IOS
    private static TaskCompletionSource<(double[] Dates, double[] Values)>? _cardioFitnessCompletion;
#endif

    /// <summary>Cardio Fitness (VO2max, ml/(kg·min)) history for the last <paramref name="days"/>
    /// days, oldest first, as two parallel arrays (Unix-seconds dates, values) - see
    /// INativeHealthData.GetCardioFitnessHistoryAsync (studylife repo) for the exact contract,
    /// which zips these into (DateTime, double) tuples. Both arrays empty (not null) on
    /// denied/undetermined authorization, a query error, or simply no readings in the window
    /// (e.g. no Apple Watch outdoor workout history) - the caller treats all three the same.</summary>
    public static Task<(double[] Dates, double[] Values)> GetCardioFitnessHistoryAsync(int days)
    {
#if LIVE_ACTIVITY && IOS
        _cardioFitnessCompletion = new TaskCompletionSource<(double[], double[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_health_get_cardio_fitness_history(days, &OnCardioFitnessResult); }
            catch { _cardioFitnessCompletion.TrySetResult((Array.Empty<double>(), Array.Empty<double>())); }
        }
        return _cardioFitnessCompletion.Task;
#else
        return Task.FromResult((Array.Empty<double>(), Array.Empty<double>()));
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static unsafe void OnCardioFitnessResult(double* dates, double* values, int count)
    {
        // Copy both immediately - same pointer-validity rule as OnHrvResult/OnSleepOnsetResult.
        var managedDates = count > 0 && dates != null ? new ReadOnlySpan<double>(dates, count).ToArray() : Array.Empty<double>();
        var managedValues = count > 0 && values != null ? new ReadOnlySpan<double>(values, count).ToArray() : Array.Empty<double>();
        _cardioFitnessCompletion?.TrySetResult((managedDates, managedValues));
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

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_get_recent_sleep_onsets(int nights, delegate* unmanaged<double*, int, void> handler);

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_get_steps_since(int minutesAgo, delegate* unmanaged<int, int, void> handler);

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_get_cardio_fitness_history(int days, delegate* unmanaged<double*, double*, int, void> handler);
#endif
}
