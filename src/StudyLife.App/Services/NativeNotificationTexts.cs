using StudyLife.Client.I18nText;

namespace StudyLife.App.Services;

/// <summary>
/// The texts of the notifications the native shell raises itself (stand-up nudge, streak
/// milestone), in the current UI language. The app root owns the text table (it is a component,
/// so the I18nText library keeps it current when the language changes) and hands it to code that
/// has no component, such as <see cref="StreakMilestones"/>, through <see cref="Provider"/>.
/// </summary>
public static class NativeNotificationTexts
{
    /// <summary>Set by the app root while it is alive; null before boot and after dispose.</summary>
    public static Func<NativeNotificationText>? Provider { get; set; }

    public static NativeNotificationText? Current => Provider?.Invoke();
}
