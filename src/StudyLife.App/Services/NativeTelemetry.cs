using System.Text.Json;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// INativeTelemetry implementation (telemetry phase 2, studylife repo): collects native-only
/// telemetry (app launch timing, HealthKit query outcomes, native push lifecycle, Android crash
/// reports - iOS crash/hang/launch/resource reports come from MetricKit via TelemetryBridge
/// instead, merged in here too) into a small persisted queue, so events recorded before the
/// WebView/consent even exist (a cold-start crash handler, or a health query fired before
/// TelemetryService.InitializeAsync ever runs) survive an app relaunch. TelemetryService
/// (studylife repo) calls DrainAsync() once per flush and merges the result into its own
/// batch - this class never talks to the network itself, and it never checks consent either:
/// TelemetryService already enforces that on both the recording buffer and the actual HTTP send.
///
/// Events are recorded via the static <see cref="Enqueue"/> entry point (same "static handoff"
/// pattern as ApnsTokenStore/NativeBridge/HealthBridge) rather than only through the
/// DI-registered instance - several callers (the Android crash handler, HealthBridge/NativePush
/// wrappers, AppLaunchTiming) run outside of any DI scope, or must not depend on one being
/// resolvable at the moment they need to record something.
/// </summary>
public sealed class NativeTelemetry : INativeTelemetry
{
    public bool IsAvailable =>
        DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.Android;

    public async Task<IReadOnlyList<TelemetryEventDto>?> DrainAsync()
    {
        var events = new List<TelemetryEventDto>(Store.Drain());
#if LIVE_ACTIVITY && IOS
        try
        {
            var metricKitEvents = await TelemetryBridge.DrainAsync();
            if (metricKitEvents.Count > 0) events.AddRange(metricKitEvents);
        }
        catch { /* MetricKit bridge unavailable - the events collected here still go out */ }
#endif
        return events.Count > 0 ? events : null;
    }

    /// <summary>Records one native event into the persisted queue. Safe to call from any
    /// thread, including a crash handler about to terminate the process
    /// (Platforms/Android/MainApplication.cs) - the write to disk happens synchronously, before
    /// this call returns.</summary>
    public static void Enqueue(TelemetryEventDto ev) => Store.Enqueue(ev);

    /// <summary>Thread-safe queue, write-through to a JSON file in FileSystem.AppDataDirectory
    /// on every Enqueue so a crash handler's write is durable before the process actually dies.
    /// Capped at 200 events (oldest dropped first) - this is a fallback buffer for
    /// pre-consent/offline periods, not meant to grow unbounded.</summary>
    private static class Store
    {
        private const int MaxQueuedEvents = 200;
        private const string FileName = "native-telemetry-queue.json";
        private static readonly object Lock = new();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static List<TelemetryEventDto>? _cache;

        public static void Enqueue(TelemetryEventDto ev)
        {
            lock (Lock)
            {
                var list = LoadLocked();
                list.Add(ev);
                if (list.Count > MaxQueuedEvents)
                    list.RemoveRange(0, list.Count - MaxQueuedEvents); // drop oldest first
                SaveLocked(list);
            }
        }

        public static List<TelemetryEventDto> Drain()
        {
            lock (Lock)
            {
                var list = LoadLocked();
                if (list.Count == 0) return new List<TelemetryEventDto>();
                var copy = new List<TelemetryEventDto>(list);
                SaveLocked(new List<TelemetryEventDto>());
                return copy;
            }
        }

        private static List<TelemetryEventDto> LoadLocked()
        {
            if (_cache != null) return _cache;
            try
            {
                var path = FilePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _cache = JsonSerializer.Deserialize<List<TelemetryEventDto>>(json, JsonOptions) ?? new();
                    return _cache;
                }
            }
            catch { /* corrupt/unreadable file - start fresh rather than lose all future events */ }
            _cache = new List<TelemetryEventDto>();
            return _cache;
        }

        private static void SaveLocked(List<TelemetryEventDto> list)
        {
            _cache = list;
            try { File.WriteAllText(FilePath(), JsonSerializer.Serialize(list, JsonOptions)); }
            catch { /* best-effort persistence - the in-memory cache still has this session's events */ }
        }

        private static string FilePath() =>
            Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, FileName);
    }
}
