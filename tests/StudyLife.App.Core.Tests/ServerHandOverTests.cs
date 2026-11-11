using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class ServerHandOverTests
{
    private sealed class RecordingSteps : IHandOverSteps
    {
        public List<HandOverStep> Ran { get; } = [];
        public Dictionary<HandOverStep, Func<CancellationToken, Task>> Behaviour { get; } = [];

        private Task Step(HandOverStep step, CancellationToken token)
        {
            Ran.Add(step);
            return Behaviour.TryGetValue(step, out var run) ? run(token) : Task.CompletedTask;
        }

        public Task UnregisterPushAsync(CancellationToken c) => Step(HandOverStep.UnregisterPush, c);
        public Task SignOutOnServerAsync(CancellationToken c) => Step(HandOverStep.SignOutOnServer, c);
        public Task ClearSessionAsync(CancellationToken c) => Step(HandOverStep.ClearSession, c);
        public Task WipeLocalCopiesAsync(CancellationToken c) => Step(HandOverStep.WipeLocalCopies, c);
        public Task ForgetServerAsync(CancellationToken c) => Step(HandOverStep.ForgetServer, c);
    }

    private static readonly HandOverStep[] FullOrder =
    [
        HandOverStep.UnregisterPush,
        HandOverStep.SignOutOnServer,
        HandOverStep.ClearSession,
        HandOverStep.WipeLocalCopies,
        HandOverStep.ForgetServer,
    ];

    [Fact]
    public async Task RunsTheStepsInOrder_ServerCallsBeforeTheSessionIsGone()
    {
        var steps = new RecordingSteps();

        var report = await new ServerHandOver(steps).RunAsync();

        Assert.Equal(FullOrder, steps.Ran);
        Assert.True(report.Completed);
    }

    [Theory]
    [InlineData(HandOverStep.UnregisterPush)]
    [InlineData(HandOverStep.SignOutOnServer)]
    [InlineData(HandOverStep.ClearSession)]
    [InlineData(HandOverStep.WipeLocalCopies)]
    [InlineData(HandOverStep.ForgetServer)]
    public async Task AFailingStep_NeverBlocksTheRest(HandOverStep failing)
    {
        var steps = new RecordingSteps();
        steps.Behaviour[failing] = _ => throw new InvalidOperationException("boom");

        var report = await new ServerHandOver(steps).RunAsync();

        Assert.Equal(FullOrder, steps.Ran);
        Assert.Equal([failing], report.Failed);
    }

    [Fact]
    public async Task AnUnreachableServer_DoesNotBlockTheSwitch()
    {
        var steps = new RecordingSteps();
        steps.Behaviour[HandOverStep.UnregisterPush] = _ => throw new HttpRequestException("no route");
        steps.Behaviour[HandOverStep.SignOutOnServer] = _ => throw new HttpRequestException("no route");

        var report = await new ServerHandOver(steps).RunAsync();

        Assert.Equal(FullOrder, steps.Ran);
        Assert.Equal([HandOverStep.UnregisterPush, HandOverStep.SignOutOnServer], report.Failed);
    }

    [Fact]
    public async Task AServerCallThatNeverAnswers_IsGivenUpOnEvenWhenItIgnoresItsToken()
    {
        var steps = new RecordingSteps();
        steps.Behaviour[HandOverStep.SignOutOnServer] = _ => new TaskCompletionSource().Task;

        var report = await new ServerHandOver(steps, TimeSpan.FromMilliseconds(100)).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(FullOrder, steps.Ran);
        Assert.Equal([HandOverStep.SignOutOnServer], report.Failed);
    }

    [Fact]
    public async Task TheServerCallsGetACancellationTokenThatFiresAtTheTimeLimit()
    {
        var steps = new RecordingSteps();
        CancellationToken seen = default;
        steps.Behaviour[HandOverStep.UnregisterPush] = async token =>
        {
            seen = token;
            await Task.Delay(Timeout.Infinite, token);
        };

        var report = await new ServerHandOver(steps, TimeSpan.FromMilliseconds(100)).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal([HandOverStep.UnregisterPush], report.Failed);
    }

    [Fact]
    public async Task TheSessionIsStillAvailableToTheServerCalls()
    {
        // Mirrors the real order requirement: nothing may clear the session before both server calls ran.
        var steps = new RecordingSteps();
        var sessionCleared = false;
        var pushSawSession = false;
        var signOutSawSession = false;
        steps.Behaviour[HandOverStep.UnregisterPush] = _ => { pushSawSession = !sessionCleared; return Task.CompletedTask; };
        steps.Behaviour[HandOverStep.SignOutOnServer] = _ => { signOutSawSession = !sessionCleared; return Task.CompletedTask; };
        steps.Behaviour[HandOverStep.ClearSession] = _ => { sessionCleared = true; return Task.CompletedTask; };

        await new ServerHandOver(steps).RunAsync();

        Assert.True(pushSawSession);
        Assert.True(signOutSawSession);
    }
}
