namespace StudyLife.App.Core;

/// <summary>What the operating system says about the app's access to Health.</summary>
public enum HealthPermission
{
    NotDetermined,
    Denied,
    Authorized,
}

/// <summary>Health access as the app presents it.</summary>
public enum HealthAccess
{
    /// <summary>No Health on this device.</summary>
    Unavailable,
    /// <summary>Never asked.</summary>
    NotAsked,
    Granted,
    /// <summary>The user said no; the app explains how to change it and never asks again.</summary>
    Declined,
}

public interface IHealthPlatform
{
    bool IsAvailable { get; }

    Task<HealthPermission> GetPermissionAsync();

    /// <summary>Shows the system permission prompt.</summary>
    Task ShowPromptAsync();
}

/// <summary>
/// Whether the user opted in to Health on this device: set only by the "Connect Apple Health" flow,
/// cleared on sign-out and when the user leaves the server. HealthKit does not tell an app whether READ
/// access was granted or refused, so the app keeps its own record of the opt-in.
/// </summary>
public interface IHealthConnectFlag
{
    bool IsSet { get; }
    void Set();
    void Clear();
}

/// <summary>
/// The single decision "may the app touch Health at all": only after the user tapped "Connect Apple
/// Health". Every HealthKit query and every Mindful Minutes write asks this first; without the opt-in
/// they return the "no data" result and record nothing (privacy policy 3.5).
/// </summary>
public sealed class HealthGate(IHealthConnectFlag flag, Func<bool> isAvailable)
{
    public bool MayAccessHealth => isAvailable() && flag.IsSet;
}

/// <summary>
/// When the app asks for Health access: only when the user asked for the feature (the "Connect Apple
/// Health" button), never at start-up, and only while the system has not been asked yet. Until that
/// tap the state is "not asked", whatever the system says; the tap records the opt-in (the gate above)
/// and shows the prompt if the system has not been asked yet.
/// </summary>
public sealed class HealthAccessCoordinator(IHealthPlatform platform, IHealthConnectFlag flag)
{
    public async Task<HealthAccess> GetAccessAsync()
    {
        if (!platform.IsAvailable) return HealthAccess.Unavailable;
        if (!flag.IsSet) return HealthAccess.NotAsked;
        return await platform.GetPermissionAsync() switch
        {
            HealthPermission.Authorized => HealthAccess.Granted,
            HealthPermission.Denied => HealthAccess.Declined,
            _ => HealthAccess.NotAsked,
        };
    }

    public async Task<HealthAccess> RequestAccessAsync()
    {
        if (!platform.IsAvailable) return HealthAccess.Unavailable;
        flag.Set();
        var current = await GetAccessAsync();
        if (current != HealthAccess.NotAsked) return current;
        await platform.ShowPromptAsync();
        return await GetAccessAsync();
    }
}
