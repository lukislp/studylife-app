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

    public Task<int?> GetStepsSinceAsync(int minutesAgo) => HealthBridge.GetStepsSinceAsync(minutesAgo);

    public async Task<IReadOnlyList<(DateTime Date, double Vo2Max)>?> GetCardioFitnessHistoryAsync(int days)
    {
        var (dates, values) = await HealthBridge.GetCardioFitnessHistoryAsync(days);
        if (dates.Length == 0) return null;

        var result = new List<(DateTime Date, double Vo2Max)>(dates.Length);
        for (var i = 0; i < dates.Length; i++)
            result.Add((DateTimeOffset.FromUnixTimeSeconds((long)dates[i]).UtcDateTime, values[i]));
        return result;
    }
}
