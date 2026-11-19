using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public sealed class SharedIntakeDiscardTests : IDisposable
{
    private readonly string _container = Path.Combine(Path.GetTempPath(), "sharedinbox-" + Guid.NewGuid().ToString("N"));

    public SharedIntakeDiscardTests() => Directory.CreateDirectory(_container);

    public void Dispose() => Directory.Delete(_container, recursive: true);

    [Fact]
    public void Clear_drops_waiting_texts_so_the_next_account_does_not_get_them()
    {
        var queue = new SharedTextQueue();
        Assert.True(queue.TryAdd("a note of the first account"));

        queue.Clear();

        Assert.Empty(queue.TakeAll());
    }

    [Fact]
    public void The_queue_takes_new_texts_after_a_clear()
    {
        var queue = new SharedTextQueue();
        queue.TryAdd("before");
        queue.Clear();

        Assert.True(queue.TryAdd("after"));

        Assert.Equal(["after"], queue.TakeAll());
    }

    [Fact]
    public void Delete_removes_the_inbox_file_of_the_share_extension()
    {
        var inbox = Path.Combine(_container, SharedInboxFile.FileName);
        File.WriteAllText(inbox, "[\"shared text\"]");

        Assert.True(SharedInboxFile.Delete(_container));

        Assert.False(File.Exists(inbox));
    }

    [Fact]
    public void Delete_without_an_inbox_file_reports_nothing_to_delete()
    {
        Assert.False(SharedInboxFile.Delete(_container));
    }
}