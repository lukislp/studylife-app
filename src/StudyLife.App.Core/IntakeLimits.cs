namespace StudyLife.App.Core;

/// <summary>
/// Upper bounds for what other apps can hand to this app through the system (share menu, "Open with").
/// The sender is not trusted: without a bound a single share could fill the memory of the app, the
/// offline write queue or the server. The bounds are generous for real use.
/// </summary>
public static class IntakeLimits
{
    /// <summary>Same limit as the calendar page's own .ics upload (Calendar.ImportIcs in the client).</summary>
    public const long MaxIcsBytes = 5L * 1024 * 1024;

    /// <summary>Characters of one shared text that become a note; the rest is cut off. Mirrored in
    /// native/ios-share/ShareViewController.swift.</summary>
    public const int MaxSharedTextChars = 20_000;

    /// <summary>Shared texts waiting to become notes. Further shares are dropped until the app took the
    /// waiting ones. Mirrored in native/ios-share/ShareViewController.swift.</summary>
    public const int MaxPendingSharedTexts = 20;

    /// <summary>The share extension's inbox file: every entry at its limit, as UTF-8 (at most 4 bytes per
    /// character, a UTF-16 surrogate pair counts as 2 characters) plus the JSON around it.</summary>
    public const long MaxSharedInboxBytes = (long)MaxPendingSharedTexts * MaxSharedTextChars * 4 + 64 * 1024;

    public static bool FitsIcs(long length) => length is > 0 and <= MaxIcsBytes;

    public static bool FitsSharedInbox(long length) => length is >= 0 and <= MaxSharedInboxBytes;
}
