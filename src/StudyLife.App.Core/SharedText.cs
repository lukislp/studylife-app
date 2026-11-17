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
