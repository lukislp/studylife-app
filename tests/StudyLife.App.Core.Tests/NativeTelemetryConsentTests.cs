using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public sealed class NativeTelemetryConsentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("slla-telemetry-").FullName;
    private readonly MemoryFlag _flag = new();
    private readonly RecordingNative _native = new();
    private readonly NativeTelemetryConsent _consent;
    private readonly ConsentGatedQueue<string> _queue;

    public NativeTelemetryConsentTests()
    {
        _consent = new NativeTelemetryConsent(_flag, _native);
        _queue = new ConsentGatedQueue<string>(_consent, () => QueueFile, 3);
    }

    private string QueueFile => Path.Combine(_dir, "queue.json");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Enqueue_without_any_decision_writes_nothing()
    {
        _queue.Enqueue("a");

        Assert.False(File.Exists(QueueFile));
        Assert.Empty(_queue.Drain());
    }

    [Fact]
    public async Task Enqueue_with_consent_persists_and_drain_hands_it_over_once()
    {
        await _consent.ApplyAsync(true);
        _queue.Enqueue("a");
        _queue.Enqueue("b");

        Assert.True(File.Exists(QueueFile));
        Assert.Equal(["a", "b"], _queue.Drain());
        Assert.Empty(_queue.Drain());
    }

    [Fact]
    public async Task Withdrawing_consent_deletes_the_queue_file_and_stops_writes()
    {
        await _consent.ApplyAsync(true);
        _queue.Enqueue("a");

        await _consent.ApplyAsync(false);
        _queue.Enqueue("b");

        Assert.False(File.Exists(QueueFile));
        Assert.Empty(_queue.Drain());
    }

    [Fact]
    public async Task Drain_without_consent_deletes_a_leftover_file()
    {
        await File.WriteAllTextAsync(QueueFile, "[\"old\"]");

        Assert.Empty(_queue.Drain());
        Assert.False(File.Exists(QueueFile));
    }

    [Fact]
    public async Task The_decision_survives_a_restart_through_the_flag()
    {
        await _consent.ApplyAsync(true);

        var restarted = new NativeTelemetryConsent(_flag);
        Assert.True(restarted.Granted);

        await restarted.RevokeAsync();
        Assert.False(new NativeTelemetryConsent(_flag).Granted);
    }

    [Fact]
    public async Task Revoke_on_sign_out_or_server_change_clears_flag_queue_and_native_side()
    {
        await _consent.ApplyAsync(true);
        _queue.Enqueue("a");

        await _consent.RevokeAsync();

        Assert.False(_consent.Granted);
        Assert.False(_flag.Value);
        Assert.False(File.Exists(QueueFile));
        Assert.Equal([true, false], _native.Calls);
    }

    [Fact]
    public async Task The_queue_keeps_the_newest_events_only()
    {
        await _consent.ApplyAsync(true);
        foreach (var e in new[] { "a", "b", "c", "d" }) _queue.Enqueue(e);

        Assert.Equal(["b", "c", "d"], _queue.Drain());
    }

    [Fact]
    public void An_unreadable_flag_counts_as_no_consent()
    {
        var consent = new NativeTelemetryConsent(new ThrowingFlag());

        Assert.False(consent.Granted);
    }

    [Theory]
    [InlineData("de", "Impressum")]
    [InlineData("de-DE", "Impressum")]
    [InlineData("pt_BR", "Aviso legal")]
    [InlineData("uk", "Правова інформація")]
    [InlineData("ja", "Imprint")]
    [InlineData(null, "Imprint")]
    public void The_imprint_label_follows_the_language(string? language, string expected) =>
        Assert.Equal(expected, ImprintLink.Label(language));

    private sealed class MemoryFlag : ITelemetryConsentFlag
    {
        public bool Value { get; private set; }
        public bool Read() => Value;
        public void Write(bool granted) => Value = granted;
    }

    private sealed class ThrowingFlag : ITelemetryConsentFlag
    {
        public bool Read() => throw new InvalidOperationException();
        public void Write(bool granted) => throw new InvalidOperationException();
    }

    private sealed class RecordingNative : INativeQueueConsent
    {
        public List<bool> Calls { get; } = [];
        public void Apply(bool granted) => Calls.Add(granted);
    }
}
