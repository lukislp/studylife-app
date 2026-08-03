using System.Runtime.InteropServices;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift "next session starts soon" Live Activity bridge
/// (native/ios-liveactivity/UpcomingSessionActivity.swift) - a separate, simpler Live Activity
/// type from TimerLiveActivity: no remote push, no interactive button. startsAt never changes
/// once the card is up, so the countdown ticks itself via Text(timerInterval:), exactly like
/// the focus timer's own countdown does while running. Local Live Activities need no
/// entitlement (works on the free tier); only NSSupportsLiveActivities in the Info.plist.
/// </summary>
public static class UpcomingSessionActivity
{
    public static void Start(string title, DateTimeOffset startsAt)
    {
#if LIVE_ACTIVITY && IOS
        try { slla_upcoming_start(title, startsAt.ToUnixTimeMilliseconds() / 1000.0); }
        catch { /* bridge unavailable - no upcoming-session card, harmless */ }
#endif
    }

    public static void End()
    {
#if LIVE_ACTIVITY && IOS
        try { slla_upcoming_end(); }
        catch { /* see Start */ }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [DllImport("__Internal")]
    private static extern void slla_upcoming_start(string title, double startsAtEpoch);

    [DllImport("__Internal")]
    private static extern void slla_upcoming_end();
#endif
}
