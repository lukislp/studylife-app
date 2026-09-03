using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// Facade over the Swift MetricKit bridge (native/ios-liveactivity/TelemetryBridge.swift):
/// starts the MXMetricManager subscription at app launch and drains MetricKit-derived
/// crash/hang/launch/resource events - already converted to the wire's TelemetryEventDto shape
/// (JSON) by the Swift side - for NativeTelemetry.DrainAsync to merge into its own queue. Same
/// no-op-by-default rules as HealthBridge/TimerLiveActivity - only active with the LIVE_ACTIVITY
/// define (static lib present at build time).
/// </summary>
public static class TelemetryBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Subscribes to MXMetricManager - call once at app startup (Components/AppRoot.razor's
    /// OnAfterRender(firstRender), next to the other native bridge hooks). Fire-and-forget:
    /// MetricKit itself decides when to deliver (about once a day for metrics, at the next
    /// launch after a crash/hang) - there's no result here to await.</summary>
    public static void Start()
    {
#if LIVE_ACTIVITY && IOS
        try { slla_telemetry_start(); }
        catch { /* bridge unavailable - MetricKit events are simply never collected */ }
#endif
    }

#if LIVE_ACTIVITY && IOS
    private static TaskCompletionSource<string>? _drainCompletion;
#endif

    /// <summary>Drains every MetricKit-derived event queued since the last call (or app start),
    /// already JSON-shaped as TelemetryEventDto by the Swift side. Empty list (not null) when
    /// nothing is queued or the bridge is unavailable.</summary>
    public static Task<List<TelemetryEventDto>> DrainAsync()
    {
#if LIVE_ACTIVITY && IOS
        _drainCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        unsafe
        {
            try { slla_telemetry_drain(&OnDrainResult); }
            catch { _drainCompletion.TrySetResult("[]"); }
        }
        return _drainCompletion.Task.ContinueWith(ParseResult, TaskScheduler.Default);
#else
        return Task.FromResult(new List<TelemetryEventDto>());
#endif
    }

#if LIVE_ACTIVITY && IOS
    private static List<TelemetryEventDto> ParseResult(Task<string> task)
    {
        try { return JsonSerializer.Deserialize<List<TelemetryEventDto>>(task.Result, JsonOptions) ?? new(); }
        catch { return new(); } // malformed JSON from the bridge - never let it break the whole flush
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnDrainResult(byte* utf8Json, int length)
    {
        // Copy immediately - the pointer is only valid for the duration of this call (same
        // pointer-validity rule as HealthBridge.cs's callbacks / TelemetryBridge.swift's comment).
        var json = length > 0 && utf8Json != null
            ? Encoding.UTF8.GetString(utf8Json, length)
            : "[]";
        _drainCompletion?.TrySetResult(json);
    }

    [DllImport("__Internal")]
    private static extern void slla_telemetry_start();

    [DllImport("__Internal")]
    private static extern unsafe void slla_telemetry_drain(delegate* unmanaged<byte*, int, void> handler);
#endif
}
