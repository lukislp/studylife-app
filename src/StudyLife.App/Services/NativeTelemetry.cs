using StudyLife.App.Core;
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
/// batch - this class never talks to the network itself. Consent is enforced twice: by
/// TelemetryService on its recording buffer and the HTTP send, and here on disk - nothing is
/// persisted unless the last ApplyConsentAsync said granted (default: not granted), and
/// withdrawing it deletes the queue files.
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

    /// <summary>Records one native event into the persisted queue (a no-op without consent). Safe to call from any
    /// thread, including a crash handler about to terminate the process
    /// (Platforms/Android/MainApplication.cs) - the write to disk happens synchronously, before
    /// this call returns.</summary>
    public static void Enqueue(TelemetryEventDto ev) => Store.Enqueue(ev);


    /// <summary>Applies the user's consent (the client's INativeTelemetry.ApplyConsentAsync; a public
    /// method rather than an interface implementation so the app also builds against client releases
    /// that do not have the method yet - once the interface has it, this is the implementation).
    /// Without consent nothing is written; withdrawing it deletes the queued events, MetricKit's
    /// included.</summary>
    public Task ApplyConsentAsync(bool granted) => Consent.ApplyAsync(granted);

    /// <summary>Withdraws consent and deletes every queued native event (sign-out, account deletion,
    /// server change - see NativeLocalCopies).</summary>
    public static Task RevokeAsync() => Consent.RevokeAsync();

    private const int MaxQueuedEvents = 200;

    private static readonly NativeTelemetryConsent Consent =
        new(new PreferencesConsentFlag(), new MetricKitConsent());

    // Capped at 200 events (oldest dropped first): a buffer for offline periods, not meant to grow.
    private static readonly ConsentGatedQueue<TelemetryEventDto> Store = new(
        Consent,
        () => Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "native-telemetry-queue.json"),
        MaxQueuedEvents);

    private sealed class PreferencesConsentFlag : ITelemetryConsentFlag
    {
        private const string Key = "studylife-native-telemetry-consent";
        public bool Read() => Preferences.Default.Get(Key, false);
        public void Write(bool granted) => Preferences.Default.Set(Key, granted);
    }

    /// <summary>The iOS MetricKit queue lives on the Swift side and is gated there too.</summary>
    private sealed class MetricKitConsent : INativeQueueConsent
    {
        public void Apply(bool granted) => TelemetryBridge.SetConsent(granted);
    }
}
