using System.Runtime.InteropServices;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift HealthKit bridge (native/ios-liveactivity/HealthBridge.swift):
/// logs completed focus rounds as Mindful Session samples. Write-only, no read access
/// requested. Same no-op-by-default rules as TimerLiveActivity/WatchBridge - only active
/// with the LIVE_ACTIVITY define (static lib present at build time).
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

    /// <summary>Requests write-only HealthKit authorization - once, at app startup. Denied/
    /// undetermined just means LogMindfulSession silently no-ops afterward.</summary>
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
    [DllImport("__Internal")]
    private static extern int slla_health_is_available();

    [DllImport("__Internal")]
    private static extern unsafe void slla_health_request_authorization(delegate* unmanaged<int, void> handler);

    [DllImport("__Internal")]
    private static extern void slla_health_log_mindful_session(double startEpoch, double endEpoch);
#endif
}
