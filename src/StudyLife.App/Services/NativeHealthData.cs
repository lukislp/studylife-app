using System.Diagnostics;
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
/// Every call below is wrapped with a health_query telemetry event (kind/durationMs/
/// authorization/result - see the wire contract in the studylife repo's ARCHITECTURE.md).
/// Privacy: NEVER put the actual sample/night count, or any health value, into the event -
/// only the bucketed "result" category and the duration.
/// </summary>
public sealed class NativeHealthData : INativeHealthData
{
    // Matches the client's own minimums (Index.Health.razor.cs: ReadinessMinSamples /
    // SleepConsistencyMinNights, both 14) - duplicated here rather than shared because that
    // file lives in a different project (StudyLife.Client) and the exact number is the only
    // thing this class needs, not a dependency on the page itself.
    private const int HrvAndSleepMinimumEntries = 14;

    public bool IsAvailable => HealthBridge.IsAvailable;

    public async Task<IReadOnlyList<double>?> GetRecentHrvAsync(int days)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var samples = await HealthBridge.GetRecentHrvAsync(days);
            RecordArrayResult("hrv", sw, samples.Length, HrvAndSleepMinimumEntries);
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
        var sw = Stopwatch.StartNew();
        try
        {
            var samples = await HealthBridge.GetRecentSleepOnsetMinutesAsync(nights);
            RecordArrayResult("sleep", sw, samples.Length, HrvAndSleepMinimumEntries);
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
        var sw = Stopwatch.StartNew();
        try
        {
            var (onsets, durations) = await HealthBridge.GetRecentSleepNightsAsync(nights);
            RecordArrayResult("sleep", sw, onsets.Length, HrvAndSleepMinimumEntries);
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
        var sw = Stopwatch.StartNew();
        try
        {
            var steps = await HealthBridge.GetStepsSinceAsync(minutesAgo);
            sw.Stop();
            // No dashboard minimum applies to steps (unlike HRV/sleep) - any returned value
            // (including a genuine 0) counts as "sufficient".
            RecordHealthQuery("steps", sw.ElapsedMilliseconds,
                authorization: steps.HasValue ? "granted" : "undetermined",
                result: steps.HasValue ? "sufficient" : "empty");
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
        var sw = Stopwatch.StartNew();
        try
        {
            var (dates, values) = await HealthBridge.GetCardioFitnessHistoryAsync(days);
            sw.Stop();
            // No dashboard minimum applies to cardio fitness readings (sparse by nature -
            // watchOS computes them roughly monthly) - any reading counts as "sufficient".
            RecordHealthQuery("vo2max", sw.ElapsedMilliseconds,
                authorization: dates.Length > 0 ? "granted" : "undetermined",
                result: dates.Length > 0 ? "sufficient" : "empty");
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

    /// <summary>Shared classification for the array-returning queries (hrv/sleep):
    /// HealthBridge.swift never distinguishes "denied", "undetermined" and "no samples in range"
    /// from each other - all three come back as an empty array (see its doc comments) - so an
    /// empty result is reported as authorization "undetermined" here, not "denied": we genuinely
    /// don't know which of the three happened. A non-empty result at least proves authorization
    /// was granted.</summary>
    private static void RecordArrayResult(string kind, Stopwatch sw, int count, int minimumForSufficient)
    {
        sw.Stop();
        var result = count == 0 ? "empty" : count < minimumForSufficient ? "below_minimum" : "sufficient";
        RecordHealthQuery(kind, sw.ElapsedMilliseconds, authorization: count > 0 ? "granted" : "undetermined", result);
    }

    private static void RecordError(string kind, Stopwatch sw)
    {
        sw.Stop();
        RecordHealthQuery(kind, sw.ElapsedMilliseconds, authorization: "undetermined", result: "error");
    }

    private static void RecordHealthQuery(string kind, long durationMs, string authorization, string result) =>
        NativeTelemetry.Enqueue(new TelemetryEventDto
        {
            Type = "health_query",
            Kind = kind,
            DurationMs = durationMs,
            Authorization = authorization,
            Result = result,
            // HealthBridge.swift never reports whether it filtered any outliers - always null,
            // never a fabricated false, per the wire contract's privacy rules.
            OutlierFiltered = null,
        });
}
