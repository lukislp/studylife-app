using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class SignOutLocalCopiesTests
{
    [Fact]
    public async Task Sign_out_runs_the_existing_purge_then_the_wipe()
    {
        var ran = new List<string>();
        var hook = SignOutLocalCopies.Compose(
            () => { ran.Add("purge"); return Task.CompletedTask; },
            () => { ran.Add("wipe"); return Task.CompletedTask; });

        await hook();

        Assert.Equal(["purge", "wipe"], ran);
    }

    [Fact]
    public async Task Works_when_the_client_has_no_hook_yet()
    {
        var wiped = 0;
        var hook = SignOutLocalCopies.Compose(null, () => { wiped++; return Task.CompletedTask; });

        await hook();

        Assert.Equal(1, wiped);
    }

    [Fact]
    public async Task A_failing_purge_does_not_stop_the_wipe()
    {
        var wiped = 0;
        var hook = SignOutLocalCopies.Compose(
            () => throw new InvalidOperationException("purge"),
            () => { wiped++; return Task.CompletedTask; });

        await hook();

        Assert.Equal(1, wiped);
    }

    [Fact]
    public async Task A_failing_wipe_never_reaches_the_sign_out()
    {
        var purged = 0;
        var hook = SignOutLocalCopies.Compose(
            () => { purged++; return Task.CompletedTask; },
            () => throw new InvalidOperationException("wipe"));

        await hook();

        Assert.Equal(1, purged);
    }

    [Fact]
    public async Task Every_sign_out_wipes_again()
    {
        var wiped = 0;
        var hook = SignOutLocalCopies.Compose(null, () => { wiped++; return Task.CompletedTask; });

        await hook();
        await hook();

        Assert.Equal(2, wiped);
    }
}
