using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift Live Activity bridge (native/ios-liveactivity): shows the running
/// focus timer as a lock screen card/Dynamic Island. The P/Invokes only exist if the static
/// library was present at build time (LIVE_ACTIVITY define, see csproj) - Windows/Android
/// builds and Mac builds without a prior native/ios-liveactivity/build.sh are automatically
/// no-ops. Local Live Activities need no entitlement (works on the free tier);
/// only NSSupportsLiveActivities in the Info.plist.
/// </summary>
public static class TimerLiveActivity
{
    public static bool IsSupported
    {
        get
        {
#if LIVE_ACTIVITY && IOS
            try { return slla_is_supported() != 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    public static void Update(string title, DateTimeOffset endsAt, bool isBreak, bool isPaused, int secondsLeft, int phaseTotalSeconds, int round, int totalRounds)
    {
#if LIVE_ACTIVITY && IOS
        try { slla_update(title, endsAt.ToUnixTimeMilliseconds() / 1000.0, isBreak ? 1 : 0, isPaused ? 1 : 0, secondsLeft, phaseTotalSeconds, round, totalRounds); }
        catch { /* bridge unavailable - timer keeps running without the lock screen card */ }
#endif
    }

    public static void End()
    {
#if LIVE_ACTIVITY && IOS
        try { slla_end(); }
        catch { /* see Update */ }
#endif
    }

    /// <summary>Fires when the pause/resume button on the Live Activity card was pressed
    /// (StudyLifeTimerToggleIntent runs in the app process and calls back here through the
    /// registered C callback). Arrives from a non-UI thread.</summary>
    // CS0067: the only raise site is OnNativeToggle, which only exists in
    // LIVE_ACTIVITY && IOS builds - the subscription in TimerLiveActivityCoordinator below
    // stays unconditional on purpose (harmless no-op elsewhere), so the compiler correctly
    // sees "never raised" on every other build target.
#pragma warning disable CS0067
    public static event Action? ToggleRequested;
#pragma warning restore CS0067

    /// <summary>Registers the native callback - once, at coordinator startup.</summary>
    public static void RegisterToggleHandler()
    {
#if LIVE_ACTIVITY && IOS
        unsafe
        {
            try { slla_set_toggle_handler(&OnNativeToggle); }
            catch { /* bridge unavailable - button stays inert */ }
        }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnNativeToggle() => ToggleRequested?.Invoke();
#endif

    /// <summary>Fires when ActivityKit has issued a new push token for the running Live
    /// Activity (step D). Arrives from a non-UI thread.</summary>
    // CS0067: see ToggleRequested above - only raised in LIVE_ACTIVITY && IOS builds.
#pragma warning disable CS0067
    public static event Action<string>? PushTokenReceived;
#pragma warning restore CS0067

    /// <summary>Registers the native callback - once, at coordinator startup; only makes
    /// sense with the push entitlement (paid profile) - free signing never gets a token.</summary>
    public static void RegisterPushTokenHandler()
    {
#if LIVE_ACTIVITY && IOS
        unsafe
        {
            try { slla_set_push_token_handler(&OnNativePushToken); }
            catch { /* bridge unavailable - no Live Activity push, local card keeps running */ }
        }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static unsafe void OnNativePushToken(byte* token)
    {
        var value = Marshal.PtrToStringUTF8((IntPtr)token);
        if (value is { Length: > 0 }) PushTokenReceived?.Invoke(value);
    }
#endif

    /// <summary>Fires on "Open Focus" from Siri/Shortcuts (StudyLifeOpenFocusIntent runs
    /// after the app is opened, in the app process). Arrives from a non-UI thread.</summary>
    // CS0067: see ToggleRequested above - only raised in LIVE_ACTIVITY && IOS builds.
#pragma warning disable CS0067
    public static event Action? OpenFocusRequested;
#pragma warning restore CS0067

    /// <summary>Registers the native callback — once, at app startup (AppRoot).</summary>
    public static void RegisterOpenFocusHandler()
    {
#if LIVE_ACTIVITY && IOS
        unsafe
        {
            try { slla_set_open_focus_handler(&OnNativeOpenFocus); }
            catch { /* bridge unavailable - Siri shortcut then just opens the app */ }
        }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnNativeOpenFocus() => OpenFocusRequested?.Invoke();
#endif

    /// <summary>Diagnostics: is an activity actually active after an update?</summary>
    public static bool HasActive
    {
        get
        {
#if LIVE_ACTIVITY && IOS
            try { return slla_has_active() != 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>Reload home screen widget timelines after HomeWidgetSnapshot has written
    /// the app group snapshot (WidgetCenter lives in the same Swift lib).</summary>
    public static void ReloadHomeWidgets()
    {
#if LIVE_ACTIVITY && IOS
        try { slla_reload_widgets(); }
        catch { /* bridge unavailable - widget updates itself on the next timeline refresh */ }
#endif
    }

    /// <summary>Diagnostics: last error from Activity.request ("" = none).</summary>
    public static string LastError
    {
        get
        {
#if LIVE_ACTIVITY && IOS
            try { return Marshal.PtrToStringUTF8(slla_last_error()) ?? ""; }
            catch { return ""; }
#else
            return "";
#endif
        }
    }

#if LIVE_ACTIVITY && IOS
    [DllImport("__Internal")]
    private static extern int slla_is_supported();

    [DllImport("__Internal")]
    private static extern void slla_update(string title, double endsAtEpoch, int isBreak, int isPaused, int secondsLeft, int phaseTotalSeconds, int round, int totalRounds);

    [DllImport("__Internal")]
    private static extern void slla_end();

    [DllImport("__Internal")]
    private static extern int slla_has_active();

    [DllImport("__Internal")]
    private static extern IntPtr slla_last_error();

    [DllImport("__Internal")]
    private static extern unsafe void slla_set_toggle_handler(delegate* unmanaged<void> handler);

    [DllImport("__Internal")]
    private static extern void slla_reload_widgets();

    [DllImport("__Internal")]
    private static extern unsafe void slla_set_open_focus_handler(delegate* unmanaged<void> handler);

    [DllImport("__Internal")]
    private static extern unsafe void slla_set_push_token_handler(delegate* unmanaged<byte*, void> handler);
#endif
}

/// <summary>
/// Translates TimerService events into Live Activity calls. OnTick fires every second -
/// but an update only happens on state changes (phase/round/pause) or when the computed
/// phase end drifts (>3s), since the countdown on the lock screen counts on its own.
/// "Idle" (reset/LoadMode base state: not running, round 1, full focus time)
/// ends the card.
/// </summary>
public sealed class TimerLiveActivityCoordinator : IDisposable
{
    private readonly TimerService _timer;
    private readonly HttpClient? _http;
    private readonly Action<string>? _diagnosticsReporter;
    private (bool IsBreak, int Round, bool IsPaused)? _lastState;
    private DateTimeOffset _lastEndsAt;
    private bool _diagnosticsShown;

    public TimerLiveActivityCoordinator(TimerService timer, HttpClient? http = null, Action<string>? diagnosticsReporter = null)
    {
        _timer = timer;
        _http = http;
        _diagnosticsReporter = diagnosticsReporter;
        _timer.OnTick += HandleTick;
        _timer.OnSessionComplete += HandleComplete;
        TimerLiveActivity.RegisterToggleHandler();
        TimerLiveActivity.ToggleRequested += HandleToggleRequested;

        // Only makes sense with the push entitlement (paid profile) - free signing never gets
        // a token from ActivityKit, so the handler would stay inert anyway.
        if (AppleSigningInfo.HasPushEntitlement)
        {
            TimerLiveActivity.RegisterPushTokenHandler();
            TimerLiveActivity.PushTokenReceived += HandlePushTokenReceived;
        }
    }

    public void Dispose()
    {
        _timer.OnTick -= HandleTick;
        _timer.OnSessionComplete -= HandleComplete;
        TimerLiveActivity.ToggleRequested -= HandleToggleRequested;
        TimerLiveActivity.PushTokenReceived -= HandlePushTokenReceived;
    }

    /// <summary>Registers the ActivityKit push token with the server (PUT api/timerstate/
    /// liveactivity-token) - best-effort like NativePush.RegisterAsync: no network/server
    /// offline must not disturb the app, the local card update keeps running independently.</summary>
    private void HandlePushTokenReceived(string token)
    {
        if (_http == null) return;
        _ = Task.Run(async () =>
        {
            try { await _http.PutAsJsonAsync("api/timerstate/liveactivity-token", new { Token = token }); }
            catch { /* offline - next token refresh or app restart tries again */ }
        });
    }

    /// <summary>Pause/resume from the Live Activity button: same semantics as the buttons on
    /// the focus page. TimerService is thread-safe; the events then update the card
    /// and UI via the normal OnTick/OnPaused flow.</summary>
    private void HandleToggleRequested()
    {
        if (_timer.IsRunning) _timer.Pause();
        else if (_timer.CurrentMode != null && _timer.SecondsLeft > 0) _timer.Start();
    }

    private void HandleTick(int secondsLeft, bool isBreak, int round, bool isRunning)
    {
#if ANDROID
        HandleTickAndroid(secondsLeft, isBreak, round, isRunning);
#else
        if (!TimerLiveActivity.IsSupported)
        {
            // Diagnostics also for the "support check fails" case - previously this branch
            // was silent and indistinguishable from "extension doesn't render".
            if (isRunning) ReportDiagnosticsOnce("is_supported=false (LIVE_ACTIVITY define missing, P/Invoke error, or areActivitiesEnabled=false)");
            return;
        }
        var mode = _timer.CurrentMode;
        if (mode == null) return;

        if (!isRunning)
        {
            var isIdle = !isBreak && round == 1 && secondsLeft == mode.FocusMinutes * 60;
            if (isIdle)
            {
                TimerLiveActivity.End();
                _lastState = null;
                return;
            }

            // Paused: static remaining-time display, only updated on transition.
            if (_lastState is not { IsPaused: true })
            {
                TimerLiveActivity.Update(mode.Name, DateTimeOffset.Now.AddSeconds(secondsLeft),
                    isBreak, isPaused: true, secondsLeft, PhaseTotalSeconds(mode, isBreak), round, mode.Rounds);
                _lastState = (isBreak, round, true);
            }
            return;
        }

        var endsAt = DateTimeOffset.Now.AddSeconds(secondsLeft);
        var state = (isBreak, round, false);
        var drifted = Math.Abs((endsAt - _lastEndsAt).TotalSeconds) > 3;
        if (_lastState == state && !drifted) return;

        TimerLiveActivity.Update(mode.Name, endsAt, isBreak, isPaused: false, secondsLeft, PhaseTotalSeconds(mode, isBreak), round, mode.Rounds);
        _lastState = state;
        _lastEndsAt = endsAt;

        // One-time self-diagnostic per app run - only reports on REAL failures
        // (activity fails to come up); the success case stays silent.
        if (!_diagnosticsShown)
        {
            _diagnosticsShown = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (TimerLiveActivity.HasActive) return;
                var error = TimerLiveActivity.LastError;
                Report(error.Length > 0
                    ? $"request error: {error}"
                    : "Live Activity did not come up (no error reported)");
            });
        }
#endif
    }

    private static int PhaseTotalSeconds(StudyLife.Client.Models.TimerMode mode, bool isBreak)
        => (isBreak ? mode.BreakMinutes : mode.FocusMinutes) * 60;

    private void ReportDiagnosticsOnce(string message)
    {
        if (_diagnosticsShown) return;
        _diagnosticsShown = true;
        Report(message);
    }

    private void Report(string message)
    {
        // Both channels: alert in the WebView (needs no permission) + notification
        // (visible if the app is currently in the background).
        _diagnosticsReporter?.Invoke(message);
        _ = NativeBridge.ShowNotificationAsync("Live Activity diagnostics", message);
    }

    private void HandleComplete()
    {
#if ANDROID
        AndroidTimerNotification.End();
        _lastState = null;
#else
        if (!TimerLiveActivity.IsSupported) return;
        TimerLiveActivity.End();
        _lastState = null;
#endif
    }

#if ANDROID
    /// <summary>Same state logic as the iOS branch, target is the chronometer notification.</summary>
    private void HandleTickAndroid(int secondsLeft, bool isBreak, int round, bool isRunning)
    {
        var mode = _timer.CurrentMode;
        if (mode == null) return;

        if (!isRunning)
        {
            var isIdle = !isBreak && round == 1 && secondsLeft == mode.FocusMinutes * 60;
            if (isIdle)
            {
                AndroidTimerNotification.End();
                _lastState = null;
                return;
            }
            if (_lastState is not { IsPaused: true })
            {
                AndroidTimerNotification.Update(mode.Name, DateTimeOffset.Now.AddSeconds(secondsLeft),
                    isBreak, isPaused: true, secondsLeft, round, mode.Rounds);
                _lastState = (isBreak, round, true);
            }
            return;
        }

        var endsAt = DateTimeOffset.Now.AddSeconds(secondsLeft);
        var state = (isBreak, round, false);
        var drifted = Math.Abs((endsAt - _lastEndsAt).TotalSeconds) > 3;
        if (_lastState == state && !drifted) return;

        AndroidTimerNotification.Update(mode.Name, endsAt, isBreak, isPaused: false, secondsLeft, round, mode.Rounds);
        _lastState = state;
        _lastEndsAt = endsAt;
    }
#endif
}
