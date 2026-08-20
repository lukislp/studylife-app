using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// HRV data for the native app shell (the client's INativeHealthData hook, Dashboard readiness
/// score) - backed by HealthBridge.swift's HKStatisticsCollectionQuery. IsAvailable delegates
/// straight to HealthBridge.IsAvailable, which is already false outside iOS
/// (LIVE_ACTIVITY && IOS conditional compilation) - no extra platform check needed here.
/// </summary>
public sealed class NativeHealthData : INativeHealthData
{
    public bool IsAvailable => HealthBridge.IsAvailable;

    public async Task<IReadOnlyList<double>?> GetRecentHrvAsync(int days)
    {
        var samples = await HealthBridge.GetRecentHrvAsync(days);
        return samples.Length > 0 ? samples : null;
    }
}
