using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>One round of the sign-in bar's server check: when the server is asked, and what the bar is left with.</summary>
public class SignInServerProbeTests
{
    private static readonly Uri Server = new("https://studylife.example.com/");

    private sealed class ScriptedChecker(params Func<ServerCheck>[] answers) : IServerChecker
    {
        public int Calls { get; private set; }

        public Task<ServerCheck> CheckAsync(Uri baseUri, CancellationToken cancellationToken = default)
        {
            var answer = answers[Math.Min(Calls, answers.Length - 1)];
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static Func<ServerCheck> Answer(ServerCheckResult result) => () => ServerCheck.Of(result);

    [Fact]
    public async Task WithoutAnyNetwork_TheServerIsNotAsked_AndTheBarSaysSoAtOnce()
    {
        var checker = new ScriptedChecker(Answer(ServerCheckResult.StudyLife));
        var probe = new SignInServerProbe(checker);

        var round = await probe.AskAsync(Server, NetworkReach.None, CancellationToken.None);

        Assert.Equal(0, checker.Calls);
        Assert.Equal(ServerRecheck.WaitForNetwork, round.Next);
        Assert.Equal(SignInServerNotice.NoNetwork, SignInServerStatus.Decide(false, Server, probe.LastCheck, NetworkReach.None).Notice);
    }

    [Fact]
    public async Task LosingTheNetwork_DropsTheOldAnswer_SoItIsNotShownWhenTheNetworkReturns()
    {
        var probe = new SignInServerProbe(new ScriptedChecker(Answer(ServerCheckResult.StudyLife)));
        await probe.AskAsync(Server, NetworkReach.Internet, CancellationToken.None);

        await probe.AskAsync(Server, NetworkReach.None, CancellationToken.None);

        Assert.Null(probe.LastCheck);
    }

    [Fact]
    public async Task AnUnexpectedFailure_CountsAsUnreachable_SoTheBarNamesAProblemAndKeepsTrying()
    {
        var probe = new SignInServerProbe(new ScriptedChecker(() => throw new InvalidOperationException("platform surprise")));

        var round = await probe.AskAsync(Server, NetworkReach.Internet, CancellationToken.None);

        Assert.Equal(ServerCheckResult.Unreachable, probe.LastCheck?.Result);
        Assert.Equal(ServerRecheck.Poll, round.Next);
        Assert.Equal(SignInServerNotice.Unreachable, SignInServerStatus.Decide(false, Server, probe.LastCheck, NetworkReach.Internet).Notice);
    }

    [Fact]
    public async Task Cancellation_IsPassedOn_NotTurnedIntoAnAnswer()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = new SignInServerProbe(new ScriptedChecker(() => throw new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.AskAsync(Server, NetworkReach.Internet, cts.Token));
        Assert.Null(probe.LastCheck);
    }

    [Fact]
    public async Task TheServerComingBack_IsReported_Once()
    {
        var probe = new SignInServerProbe(new ScriptedChecker(
            Answer(ServerCheckResult.Unreachable), Answer(ServerCheckResult.StudyLife), Answer(ServerCheckResult.StudyLife)));

        Assert.False((await probe.AskAsync(Server, NetworkReach.Internet, CancellationToken.None)).BecameReachable);
        var back = await probe.AskAsync(Server, NetworkReach.Internet, CancellationToken.None);
        Assert.True(back.BecameReachable);
        Assert.Equal(ServerRecheck.Settled, back.Next);
        Assert.False((await probe.AskAsync(Server, NetworkReach.Internet, CancellationToken.None)).BecameReachable);
    }

    [Theory]
    [InlineData(NetworkReach.Constrained)]
    [InlineData(NetworkReach.Unknown)]
    public async Task WithAConstrainedOrUnknownNetwork_TheServerIsAsked(NetworkReach reach)
    {
        // A captive portal or a LAN-only server: only the check can tell.
        var checker = new ScriptedChecker(Answer(ServerCheckResult.Unreachable));
        var probe = new SignInServerProbe(checker);

        var round = await probe.AskAsync(Server, reach, CancellationToken.None);

        Assert.Equal(1, checker.Calls);
        Assert.Equal(ServerRecheck.Poll, round.Next);
    }
}
