using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StudyLife.Client.Models;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift WatchConnectivity bridge (native/ios-liveactivity/WatchBridge.swift):
/// relays the same snapshot HomeWidgetSnapshot.cs already writes to the iOS App Group
/// container to the paired Apple Watch. Same no-op-by-default rules as TimerLiveActivity -
/// only active with the LIVE_ACTIVITY define (static lib present at build time).
/// </summary>
public static class WatchBridge
{
    public static bool IsSupported
    {
        get
        {
#if LIVE_ACTIVITY && IOS
            try { return slla_watch_is_supported() != 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>Activates the WCSession - once, at app startup (AppRoot).</summary>
    public static void Activate()
    {
#if LIVE_ACTIVITY && IOS
        try { slla_watch_activate(); }
        catch { /* bridge unavailable - Watch app just never receives data */ }
#endif
    }

    /// <summary>Pushes the same snapshot bytes HomeWidgetSnapshot already wrote to the App
    /// Group container - best effort, mirrors HomeWidgetSnapshot.UpdateAsync's own try/catch
    /// swallowing (Watch sync is not required for core app functionality).</summary>
    public static void PushSnapshot(byte[] json)
    {
#if LIVE_ACTIVITY && IOS
        try
        {
            unsafe
            {
                fixed (byte* ptr = json) { slla_watch_push_context(ptr, json.Length); }
            }
        }
        catch { /* bridge unavailable / no Watch paired - snapshot stays phone-only */ }
#endif
    }

    /// <summary>Fires when a command arrives from the Watch (command, modeId - see
    /// WatchTimerCoordinator; modeId is only meaningful for command 3). Arrives from a
    /// non-UI thread.</summary>
    // CS0067: the only raise site is OnNativeCommand, which only exists in
    // LIVE_ACTIVITY && IOS builds - the subscription in WatchTimerCoordinator stays
    // unconditional on purpose (harmless no-op elsewhere), so the compiler correctly sees
    // "never raised" on every other build target.
#pragma warning disable CS0067
    public static event Action<int, int>? CommandReceived;
#pragma warning restore CS0067

    /// <summary>Registers the native callback - once, at coordinator startup.</summary>
    public static void RegisterCommandHandler()
    {
#if LIVE_ACTIVITY && IOS
        unsafe
        {
            try { slla_watch_set_command_handler(&OnNativeCommand); }
            catch { /* bridge unavailable - Watch buttons stay inert */ }
        }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnNativeCommand(int command, int modeId) => CommandReceived?.Invoke(command, modeId);
#endif

    /// <summary>Fires when a post-session rating tap arrives from the Watch. Arrives from a
    /// non-UI thread.</summary>
    // CS0067: see CommandReceived above - only raised in LIVE_ACTIVITY && IOS builds.
#pragma warning disable CS0067
    public static event Action<int>? RatingReceived;
#pragma warning restore CS0067

    /// <summary>Registers the native callback - once, at coordinator startup.</summary>
    public static void RegisterRatingHandler()
    {
#if LIVE_ACTIVITY && IOS
        unsafe
        {
            try { slla_watch_set_rating_handler(&OnNativeRating); }
            catch { /* bridge unavailable - Watch rating prompt has no effect */ }
        }
#endif
    }

#if LIVE_ACTIVITY && IOS
    [UnmanagedCallersOnly]
    private static void OnNativeRating(int rating) => RatingReceived?.Invoke(rating);
#endif

#if LIVE_ACTIVITY && IOS
    [DllImport("__Internal")]
    private static extern int slla_watch_is_supported();

    [DllImport("__Internal")]
    private static extern void slla_watch_activate();

    [DllImport("__Internal")]
    private static extern unsafe void slla_watch_push_context(byte* json, int length);

    [DllImport("__Internal")]
    private static extern unsafe void slla_watch_set_command_handler(delegate* unmanaged<int, int, void> handler);

    [DllImport("__Internal")]
    private static extern unsafe void slla_watch_set_rating_handler(delegate* unmanaged<int, void> handler);
#endif
}

/// <summary>
/// Translates start/pause/stop/loadMode commands from the Watch into TimerService calls -
/// same semantics as the focus page's own buttons, so the same PushStateAsync/Live Activity/
/// widget side effects fire automatically (the Watch never talks to the server or TimerService
/// directly, see docs/plan). Command codes: 0 = start, 1 = pause, 2 = stop, 3 = loadModeAndStart
/// (modeId = the tapped mode from the Watch's picker, resolved against the same built-in +
/// custom mode list Focus.razor uses).
/// </summary>
public sealed class WatchTimerCoordinator : IDisposable
{
    private readonly TimerService _timer;
    private readonly AppStateService _state;
    private readonly HttpClient _http;

    public WatchTimerCoordinator(TimerService timer, AppStateService state, HttpClient http)
    {
        _timer = timer;
        _state = state;
        _http = http;
        WatchBridge.RegisterCommandHandler();
        WatchBridge.CommandReceived += HandleCommandReceived;
        WatchBridge.RegisterRatingHandler();
        WatchBridge.RatingReceived += HandleRatingReceived;
    }

    public void Dispose()
    {
        WatchBridge.CommandReceived -= HandleCommandReceived;
        WatchBridge.RatingReceived -= HandleRatingReceived;
    }

    private void HandleCommandReceived(int command, int modeId)
    {
        switch (command)
        {
            case 0 when _timer is { CurrentMode: not null, IsRunning: false }:
                _timer.Start();
                break;
            case 1 when _timer.IsRunning:
                _timer.Pause();
                break;
            case 2:
                _timer.Stop();
                break;
            case 3:
                _ = LoadModeAndStartAsync(modeId);
                break;
        }
    }

    private async Task LoadModeAndStartAsync(int modeId)
    {
        var settings = await _state.GetSettingsAsync();
        var mode = CustomTimerModes.Combined(settings.CustomTimerModes).FirstOrDefault(m => m.Id == modeId);
        if (mode == null) return;
        _timer.LoadMode(mode);
        _timer.Start();
    }

    // Watch-only rating (👍/😐/👎, see ContentView.swift's RatingPromptView) - lighter-weight
    // than the phone's own text reflection prompt (typing on a watch is impractical). Reuses
    // the existing Notes infrastructure instead of a new schema/endpoint, same offline-queue
    // fallback as Focus.razor's SaveReflection.
    private void HandleRatingReceived(int rating)
    {
        var emoji = rating switch { 2 => "👍", 1 => "😐", _ => "👎" };
        var note = new NoteDto
        {
            Title = "",
            Content = $"Watch-Bewertung: {emoji}",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        _ = SaveRatingNoteAsync(note);
    }

    private async Task SaveRatingNoteAsync(NoteDto note)
    {
        try { await _http.PostAsJsonAsync("api/notes", note); }
        catch { await _state.EnqueueNoteSaveAsync(note); }
    }
}
