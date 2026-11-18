using System.Text.Json;

namespace StudyLife.App.Core;

/// <summary>Where the consent flag is kept across launches (the app implements this with Preferences).
/// Absent means no consent.</summary>
public interface ITelemetryConsentFlag
{
    bool Read();
    void Write(bool granted);
}

/// <summary>Tells the native side that keeps its own queue (the iOS MetricKit bridge) what the user
/// decided; false makes it delete what it holds.</summary>
public interface INativeQueueConsent
{
    void Apply(bool granted);
}

/// <summary>
/// The user's consent to native telemetry as this device sees it. The shared client calls
/// INativeTelemetry.ApplyConsentAsync with the decision (null counts as false); every native queue
/// writes only while <see cref="Granted"/> is true and is emptied the moment it turns false. The flag is
/// persisted, so a crash handler that runs before the client has even started still knows the answer
/// of the last session. Default is false.
/// </summary>
public sealed class NativeTelemetryConsent(ITelemetryConsentFlag flag, INativeQueueConsent? native = null)
{
    private readonly object _lock = new();
    private readonly List<Action> _onRevoked = [];
    private bool? _granted;

    public bool Granted
    {
        get
        {
            lock (_lock) return _granted ??= SafeRead();
        }
    }

    /// <summary>Registers what must be deleted when consent is withdrawn (the queue files).</summary>
    public void OnRevoked(Action purge)
    {
        lock (_lock) _onRevoked.Add(purge);
    }

    public Task ApplyAsync(bool granted)
    {
        List<Action> purges;
        lock (_lock)
        {
            _granted = granted;
            try { flag.Write(granted); } catch { /* the in-memory value still holds for this run */ }
            purges = granted ? [] : [.. _onRevoked];
        }
        try { native?.Apply(granted); } catch { /* bridge unavailable: nothing native to gate */ }
        foreach (var purge in purges)
        {
            try { purge(); } catch { /* best effort, the flag is already false so nothing new is written */ }
        }
        return Task.CompletedTask;
    }

    /// <summary>Withdraws consent and deletes every queued event: sign-out, account deletion and server
    /// change (the next sign-in asks the client to apply the decision again).</summary>
    public Task RevokeAsync() => ApplyAsync(false);

    private bool SafeRead()
    {
        try { return flag.Read(); }
        catch { return false; }
    }
}

/// <summary>
/// A small persisted event queue (JSON file, written through on every enqueue so a crash handler's
/// write is durable) that accepts nothing unless consent is granted and deletes its file when consent
/// is withdrawn. Capped, oldest dropped first.
/// </summary>
public sealed class ConsentGatedQueue<T>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private readonly NativeTelemetryConsent _consent;
    private readonly Func<string> _filePath;
    private readonly int _max;
    private List<T>? _cache;

    public ConsentGatedQueue(NativeTelemetryConsent consent, Func<string> filePath, int maxEvents)
    {
        _consent = consent;
        _filePath = filePath;
        _max = maxEvents;
        consent.OnRevoked(Purge);
    }

    public void Enqueue(T item)
    {
        if (!_consent.Granted) return;
        lock (_lock)
        {
            var list = LoadLocked();
            list.Add(item);
            if (list.Count > _max) list.RemoveRange(0, list.Count - _max);
            SaveLocked(list);
        }
    }

    /// <summary>Hands over everything queued and empties the queue; without consent nothing is
    /// handed over and any leftover file is deleted.</summary>
    public List<T> Drain()
    {
        if (!_consent.Granted)
        {
            Purge();
            return [];
        }
        lock (_lock)
        {
            var list = LoadLocked();
            if (list.Count == 0) return [];
            var copy = new List<T>(list);
            SaveLocked([]);
            return copy;
        }
    }

    public void Purge()
    {
        lock (_lock)
        {
            _cache = [];
            try
            {
                var path = _filePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }
    }

    private List<T> LoadLocked()
    {
        if (_cache != null) return _cache;
        try
        {
            var path = _filePath();
            if (File.Exists(path))
            {
                _cache = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? [];
                return _cache;
            }
        }
        catch { /* corrupt or unreadable file: start fresh rather than lose all future events */ }
        _cache = [];
        return _cache;
    }

    private void SaveLocked(List<T> list)
    {
        _cache = list;
        try { File.WriteAllText(_filePath(), JsonSerializer.Serialize(list, JsonOptions)); }
        catch { /* best-effort persistence, the cache still has this session's events */ }
    }
}
