namespace StudyLife.App.Core;

/// <summary>Turns what another app shared (text, optional subject) into the text of a note, within
/// <see cref="IntakeLimits.MaxSharedTextChars"/>. Pure.</summary>
public static class SharedText
{
    /// <summary>Characters of the shared text shown when the user is asked to keep it.</summary>
    public const int PreviewChars = 300;

    /// <summary>The note text, or null when nothing usable was shared.</summary>
    public static string? Compose(string? text, string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var body = text.Trim();
        var combined = string.IsNullOrWhiteSpace(subject) ? body : $"{subject.Trim()}\n{body}";
        return Cap(combined, IntakeLimits.MaxSharedTextChars);
    }

    /// <summary>The beginning of the text for a confirmation, with an ellipsis when it goes on.</summary>
    public static string Preview(string text) =>
        text.Length <= PreviewChars ? text : Cap(text, PreviewChars) + "…";

    /// <summary>Cuts after <paramref name="max"/> characters without splitting a surrogate pair.</summary>
    internal static string Cap(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end];
    }
}

/// <summary>Shared texts waiting to become notes, bounded by <see cref="IntakeLimits.MaxPendingSharedTexts"/>.
/// Thread-safe: the platform hands texts in from its own thread, the web view takes them.</summary>
public sealed class SharedTextQueue
{
    private readonly object _lock = new();
    private readonly List<string> _pending = [];

    /// <summary>Adds the composed text; false when there was nothing to add or the queue is full.</summary>
    public bool TryAdd(string? text, string? subject = null)
    {
        if (SharedText.Compose(text, subject) is not { } note) return false;
        lock (_lock)
        {
            if (_pending.Count >= IntakeLimits.MaxPendingSharedTexts) return false;
            _pending.Add(note);
            return true;
        }
    }

    /// <summary>Drops every waiting text without handing it on (sign-out: it must not become a note of the next account).</summary>
    public void Clear()
    {
        lock (_lock) _pending.Clear();
    }

    public IReadOnlyList<string> TakeAll()
    {
        lock (_lock)
        {
            var result = _pending.ToArray();
            _pending.Clear();
            return result;
        }
    }
}

/// <summary>The iOS share extension's inbox file in the app group container.</summary>
public static class SharedInboxFile
{
    public const string FileName = "shared-note-inbox.json";

    /// <summary>Deletes the inbox file in <paramref name="containerDirectory"/>; true when there was one.
    /// Texts shared before a sign-out belong to the account that was signed in and are discarded with it.</summary>
    public static bool Delete(string containerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDirectory);
        var path = Path.Combine(containerDirectory, FileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}