using Microsoft.Maui.Storage;

namespace StudyLife.App.Services;

/// <summary>
/// One-off celebration notification when the study streak crosses a milestone. High-water-mark
/// semantics (like an achievement unlock): once a milestone has been celebrated it never fires
/// again, even if the streak later resets and climbs back through it - matches how e.g.
/// Duolingo badges behave, and avoids re-celebrating on every single HomeWidgetSnapshot update.
/// </summary>
public static class StreakMilestones
{
    private static readonly int[] Milestones = { 3, 7, 14, 30, 50, 100, 180, 365 };
    private const string LastCelebratedKey = "LastCelebratedStreakMilestone";

    public static async Task CheckAndCelebrateAsync(int streakDays)
    {
        try
        {
            var last = Preferences.Default.Get(LastCelebratedKey, 0);
            var reached = Milestones.Where(m => m <= streakDays && m > last).OrderByDescending(m => m).FirstOrDefault();
            if (reached == 0) return;

            Preferences.Default.Set(LastCelebratedKey, reached);
            await NativeBridge.ShowNotificationAsync(
                $"\U0001F525 {reached} Tage Lernserie!",
                $"Du hast eine {reached}-Tage-Serie in StudyLife erreicht. Weiter so!");
        }
        catch { /* best effort - a missed celebration must never break the widget update */ }
    }
}
