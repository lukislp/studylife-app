using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// HRV + sleep-onset + step-count + cardio-fitness data for the native app shell (the client's
/// INativeHealthData hook, Dashboard readiness-score/sleep-consistency tiles, the Focus
/// Timer's movement-break nudge, and the Stats page's cardio fitness trend) - backed by
/// HealthBridge.swift's HKStatisticsCollectionQuery/HKSampleQuery/HKStatisticsQuery.
/// IsAvailable delegates straight to HealthBridge.IsAvailable, which is already false outside
/// iOS (LIVE_ACTIVITY && IOS conditional compilation) - no extra platform check needed here.
/// </summary>
public sealed class NativeHealthData : INativeHealthData
{
    public bool IsAvailable => HealthBridge.IsAvailable;

    public async Task<IReadOnlyList<double>?> GetRecentHrvAsync(int days)
    {
        var samples = await HealthBridge.GetRecentHrvAsync(days);
        return samples.Length > 0 ? samples : null;
    }

    public async Task<IReadOnlyList<double>?> GetRecentSleepOnsetMinutesAsync(int nights)
    {
        var samples = await HealthBridge.GetRecentSleepOnsetMinutesAsync(nights);
        return samples.Length > 0 ? samples : null;
    }

    // Plain for loop, no LINQ over the tuple - see the Mono AOT note on GetCardioFitnessPointsAsync.
    public async Task<IReadOnlyList<SleepNight>?> GetRecentSleepNightsAsync(int nights)
    {
        var (onsets, durations) = await HealthBridge.GetRecentSleepNightsAsync(nights);
        if (onsets.Length == 0) return null;

        var result = new List<SleepNight>(onsets.Length);
        for (var i = 0; i < onsets.Length; i++)
            result.Add(new SleepNight(onsets[i], durations[i]));
        return result;
    }

    public Task<int?> GetStepsSinceAsync(int minutesAgo) => HealthBridge.GetStepsSinceAsync(minutesAgo);

    // Overrides GetCardioFitnessPointsAsync (record-struct based) directly rather than the
    // older, now-[Obsolete] tuple-based GetCardioFitnessHistoryAsync: LINQ generic-instantiated
    // over a value-tuple element type reproducibly crashed Mono's iOS AOT compiler (see
    // INativeHealthData.CardioFitnessPoint's doc comment in the studylife repo for the full
    // writeup) - CardioFitnessPoint is a record struct and doesn't hit that bug.
    public async Task<IReadOnlyList<CardioFitnessPoint>?> GetCardioFitnessPointsAsync(int days)
    {
        var (dates, values) = await HealthBridge.GetCardioFitnessHistoryAsync(days);
        if (dates.Length == 0) return null;

        var result = new List<CardioFitnessPoint>(dates.Length);
        for (var i = 0; i < dates.Length; i++)
            result.Add(new CardioFitnessPoint(DateTimeOffset.FromUnixTimeSeconds((long)dates[i]).UtcDateTime, values[i]));
        return result;
    }
}
