using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// Handoff point for text passed in via the share menu (same pattern as
/// NativeIcsIntake): Android stores it directly from the ACTION_SEND intent, iOS via the
/// share extension (native/ios-share) through an app group inbox file that gets drained on
/// app start/foreground transition. AppRoot consumes the entries and turns them into
/// notes (directly via API when online, via AppStateService's write queue when offline).
/// </summary>
public static class SharedNoteIntake
{
    // Bounded (IntakeLimits): the sender is another app and is not trusted.
    private static readonly SharedTextQueue Pending = new();

    /// <summary>Fires after SetPending - AppRoot then consumes it (warm start). On a cold
    /// start nobody is subscribed yet; AppRoot calls TakeAll itself after boot.</summary>
    public static event Action? PendingChanged;

    /// <summary>Queues the shared text (with the sender's subject as first line), cut to the note limit;
    /// dropped when nothing usable was shared or too many shares are still waiting.</summary>
    public static void SetPending(string? text, string? subject = null)
    {
        if (Pending.TryAdd(text, subject))
            PendingChanged?.Invoke();
    }

    public static List<string> TakeAll() => [.. Pending.TakeAll()];

#if IOS
    private const string InboxFileName = "shared-note-inbox.json";

    /// <summary>Drains the app group inbox filled by the share extension (a JSON array
    /// of strings) into the pending list. Called on boot and on every
    /// foreground transition (AppDelegate.WillEnterForeground).</summary>
    public static void DrainIosInbox()
    {
        try
        {
            var container = Foundation.NSFileManager.DefaultManager.GetContainerUrl(HomeWidgetSnapshot.AppGroupId);
            if (container?.Path is not string dir) return;
            var path = Path.Combine(dir, InboxFileName);
            if (!File.Exists(path)) return;
            // An inbox larger than a full queue can hold was not written by our share extension: drop it unread.
            if (!IntakeLimits.FitsSharedInbox(new FileInfo(path).Length))
            {
                File.Delete(path);
                return;
            }
            var json = File.ReadAllText(path);
            File.Delete(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var element in doc.RootElement.EnumerateArray())
                if (element.GetString() is { } text) SetPending(text);
        }
        catch { /* corrupt/missing inbox: ignore silently, sharing is best effort */ }
    }
#endif
}
