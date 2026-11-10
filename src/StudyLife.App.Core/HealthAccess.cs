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
/// When the app asks for Health access: only when the user asked for the feature (the "Connect Apple
/// Health" button), never at start-up, and only while the system has not been asked yet. The state is
/// read from the operating system every time, so a user who allowed access before this rule existed
/// is simply "granted" and sees no change.
/// </summary>
public sealed class HealthAccessCoordinator(IHealthPlatform platform)
{
    public async Task<HealthAccess> GetAccessAsync()
    {
        if (!platform.IsAvailable) return HealthAccess.Unavailable;
        return await platform.GetPermissionAsync() switch
        {
            HealthPermission.Authorized => HealthAccess.Granted,
            HealthPermission.Denied => HealthAccess.Declined,
            _ => HealthAccess.NotAsked,
        };
    }

    public async Task<HealthAccess> RequestAccessAsync()
    {
        var current = await GetAccessAsync();
        if (current != HealthAccess.NotAsked) return current;
        await platform.ShowPromptAsync();
        return await GetAccessAsync();
    }
}
