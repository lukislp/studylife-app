using System.Diagnostics;
using StudyLife.App.Core;
using StudyLife.Client.Services;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// HRV + sleep-onset + step-count + cardio-fitness data for the native app shell (the client's
/// INativeHealthData hook, Dashboard readiness-score/sleep-consistency tiles, the Focus
/// Timer's movement-break nudge, and the Stats page's cardio fitness trend) - backed by
/// HealthBridge.swift's HKStatisticsCollectionQuery/HKSampleQuery/HKStatisticsQuery.
/// IsAvailable delegates straight to HealthBridge.IsAvailable, which is already false outside
/// iOS (LIVE_ACTIVITY &amp;&amp; IOS conditional compilation) - no extra platform check needed here.
///
/// Every call below first asks HealthConnection.Gate: until the user tapped "Connect Apple Health"
/// nothing is queried and no telemetry event is recorded (the UI treats null as "no data").
/// After that each query records a health_query event with kind, durationMs and result, where result
/// is only "ok" or "error". Nothing derived from Health leaves the device: no values, no counts, no
/// "was there data" signal and no authorization guess.
/// </summary>
public sealed class NativeHealthData : INativeHealthData
{
    private readonly HealthAccessCoordinator _access = new(new BridgeHealthPlatform(), HealthConnection.Flag);

    public bool IsAvailable => HealthBridge.IsAvailable;

    public async Task<NativeHealthAccess> GetAccessAsync() => ToClient(await _access.GetAccessAsync());

    public async Task<NativeHealthAccess> RequestAccessAsync() => ToClient(await _access.RequestAccessAsync());

    private static NativeHealthAccess ToClient(HealthAccess access) => access switch
    {
        HealthAccess.NotAsked => NativeHealthAccess.NotAsked,
        HealthAccess.Granted => NativeHealthAccess.Granted,
        HealthAccess.Declined => NativeHealthAccess.Declined,
        _ => NativeHealthAccess.Unavailable,
    };

    /// <summary>The Swift HealthKit bridge as the platform behind HealthAccessCoordinator.</summary>
    private sealed class BridgeHealthPlatform : IHealthPlatform
    {
        public bool IsAvailable => HealthBridge.IsAvailable;
        public Task<HealthPermission> GetPermissionAsync() => Task.FromResult(HealthBridge.GetPermission());
        public Task ShowPromptAsync() => HealthBridge.RequestAuthorizationAsync();
    }

    public async Task<IReadOnlyList<double>?> GetRecentHrvAsync(int days)
    {
        if (!HealthConnection.Gate.MayAccessHealth) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var samples = await HealthBridge.GetRecentHrvAsync(days);
            RecordOk("hrv", sw);
            return samples.Length > 0 ? samples : null;
        }
        catch
        {
            RecordError("hrv", sw);
            return null;
        }
    }

    public async Task<IReadOnlyList<double>?> GetRecentSleepOnsetMinutesAsync(int nights)
    {
        if (!HealthConnection.Gate.MayAccessHealth) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var samples = await HealthBridge.GetRecentSleepOnsetMinutesAsync(nights);
            RecordOk("sleep", sw);
            return samples.Length > 0 ? samples : null;
        }
        catch
        {
            RecordError("sleep", sw);
            return null;
        }
    }

    // Plain for loop, no LINQ over the tuple - see the Mono AOT note on GetCardioFitnessPointsAsync.
    public async Task<IReadOnlyList<SleepNight>?> GetRecentSleepNightsAsync(int nights)
    {
        if (!HealthConnection.Gate.MayAccessHealth) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var (onsets, durations) = await HealthBridge.GetRecentSleepNightsAsync(nights);
            RecordOk("sleep", sw);
            if (onsets.Length == 0) return null;

            var result = new List<SleepNight>(onsets.Length);
            for (var i = 0; i < onsets.Length; i++)
                result.Add(new SleepNight(onsets[i], durations[i]));
            return result;
        }
        catch
        {
            RecordError("sleep", sw);
            return null;
        }
    }

    public async Task<int?> GetStepsSinceAsync(int minutesAgo)
    {
        if (!HealthConnection.Gate.MayAccessHealth) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var steps = await HealthBridge.GetStepsSinceAsync(minutesAgo);
            RecordOk("steps", sw);
            return steps;
        }
        catch
        {
            RecordError("steps", sw);
            return null;
        }
    }

    // Overrides GetCardioFitnessPointsAsync (record-struct based) directly rather than the
    // older, now-[Obsolete] tuple-based GetCardioFitnessHistoryAsync: LINQ generic-instantiated
    // over a value-tuple element type reproducibly crashed Mono's iOS AOT compiler (see
    // INativeHealthData.CardioFitnessPoint's doc comment in the studylife repo for the full
    // writeup) - CardioFitnessPoint is a record struct and doesn't hit that bug.
    public async Task<IReadOnlyList<CardioFitnessPoint>?> GetCardioFitnessPointsAsync(int days)
    {
        if (!HealthConnection.Gate.MayAccessHealth) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var (dates, values) = await HealthBridge.GetCardioFitnessHistoryAsync(days);
            RecordOk("vo2max", sw);
            if (dates.Length == 0) return null;

            var result = new List<CardioFitnessPoint>(dates.Length);
            for (var i = 0; i < dates.Length; i++)
                result.Add(new CardioFitnessPoint(DateTimeOffset.FromUnixTimeSeconds((long)dates[i]).UtcDateTime, values[i]));
            return result;
        }
        catch
        {
            RecordError("vo2max", sw);
            return null;
        }
    }

    private static void RecordOk(string kind, Stopwatch sw) => Record(kind, sw, "ok");

    private static void RecordError(string kind, Stopwatch sw) => Record(kind, sw, "error");

    // Only the kind, the duration and ok/error: whether data existed, how much, and whether the user
    // allowed access are Health-derived and never leave the device.
    private static void Record(string kind, Stopwatch sw, string result)
    {
        sw.Stop();
        NativeTelemetry.Enqueue(new TelemetryEventDto
        {
            Type = "health_query",
            Kind = kind,
            DurationMs = sw.ElapsedMilliseconds,
            Result = result,
        });
    }
}
