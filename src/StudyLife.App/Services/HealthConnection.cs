using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// The app-side record that the user tapped "Connect Apple Health" (see HealthGate). Persisted in
/// Preferences, cleared by NativeLocalCopies on sign-out, account deletion and server change.
/// </summary>
public static class HealthConnection
{
    private const string Key = "studylife-health-connected";

    private sealed class PreferencesFlag : IHealthConnectFlag
    {
        public bool IsSet => Preferences.Default.Get(Key, false);
        public void Set() => Preferences.Default.Set(Key, true);
        public void Clear() => Preferences.Default.Remove(Key);
    }

    public static IHealthConnectFlag Flag { get; } = new PreferencesFlag();

    /// <summary>True only on a device with Health and after the Connect tap.</summary>
    public static HealthGate Gate { get; } = new(Flag, () => HealthBridge.IsAvailable);

    public static void Clear() => Flag.Clear();
}