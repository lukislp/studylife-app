using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class IntakeLimitsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(IntakeLimits.MaxIcsBytes, true)]
    [InlineData(IntakeLimits.MaxIcsBytes + 1, false)]
    public void AnIcsFile_IsTakenOnlyUpToTheUploadLimit(long length, bool expected) =>
        Assert.Equal(expected, IntakeLimits.FitsIcs(length));

    [Fact]
    public void TheShareInbox_HoldsAFullQueueButNotMore()
    {
        Assert.True(IntakeLimits.FitsSharedInbox(0));
        Assert.True(IntakeLimits.FitsSharedInbox(
            (long)IntakeLimits.MaxPendingSharedTexts * IntakeLimits.MaxSharedTextChars * 4));
        Assert.False(IntakeLimits.FitsSharedInbox(IntakeLimits.MaxSharedInboxBytes + 1));
    }
}

public class SharedTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void NothingShared_IsNoNote(string? text) =>
        Assert.Null(SharedText.Compose(text, "subject"));

    [Fact]
    public void TheSubject_BecomesTheFirstLine()
    {
        Assert.Equal("Title\nhttps://example.com", SharedText.Compose("  https://example.com ", " Title "));
        Assert.Equal("just text", SharedText.Compose("just text", "  "));
    }

    [Fact]
    public void AHugeShare_IsCutAtTheLimit()
    {
        var note = SharedText.Compose(new string('a', IntakeLimits.MaxSharedTextChars * 3));

        Assert.Equal(IntakeLimits.MaxSharedTextChars, note!.Length);
    }

    [Fact]
    public void TheCut_NeverSplitsASurrogatePair()
    {
        var text = new string('a', IntakeLimits.MaxSharedTextChars - 1) + "😀" + "tail";

        var note = SharedText.Compose(text)!;

        Assert.Equal(IntakeLimits.MaxSharedTextChars - 1, note.Length);
        Assert.False(char.IsHighSurrogate(note[^1]));
    }

    [Fact]
    public void ThePreview_IsShortAndMarkedWhenItGoesOn()
    {
        Assert.Equal("short", SharedText.Preview("short"));

        var preview = SharedText.Preview(new string('b', 1000));
        Assert.Equal(SharedText.PreviewChars + 1, preview.Length);
        Assert.EndsWith("…", preview);
    }
}

public class SharedTextQueueTests
{
    [Fact]
    public void TextsAreTakenOnce()
    {
        var queue = new SharedTextQueue();
        Assert.True(queue.TryAdd("one"));
        Assert.True(queue.TryAdd("two", "subject"));
        Assert.False(queue.TryAdd("   "));

        Assert.Equal(["one", "subject\ntwo"], queue.TakeAll());
        Assert.Empty(queue.TakeAll());
    }

    [Fact]
    public void AFloodOfShares_StopsAtTheLimitUntilTheAppTookThem()
    {
        var queue = new SharedTextQueue();
        for (var i = 0; i < IntakeLimits.MaxPendingSharedTexts; i++)
            Assert.True(queue.TryAdd($"note {i}"));

        Assert.False(queue.TryAdd("one too many"));
        Assert.Equal(IntakeLimits.MaxPendingSharedTexts, queue.TakeAll().Count);
        Assert.True(queue.TryAdd("room again"));
    }
}

public class MediaCapturePolicyTests
{
    [Fact]
    public void TheAppsOwnPage_MayUseTheMicrophone() =>
        Assert.True(MediaCapturePolicy.MayCapture("app", "0.0.0.1", isMainFrame: true));

    [Theory]
    [InlineData("app", "0.0.0.1", false)]
    [InlineData("https", "0.0.0.1", true)]
    [InlineData("https", "evil.example", true)]
    [InlineData("app", "evil.example", true)]
    [InlineData(null, null, true)]
    public void AnyOtherOriginOrAnIframe_IsDenied(string? scheme, string? host, bool isMainFrame) =>
        Assert.False(MediaCapturePolicy.MayCapture(scheme, host, isMainFrame));
}
