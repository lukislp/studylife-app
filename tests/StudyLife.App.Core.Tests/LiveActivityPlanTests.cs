using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class LiveActivityPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private const int Focus = 25;

    [Fact]
    public void Reset_WhileShowingARunningCard_EndsIt()
    {
        var running = LiveActivityPlan.OnTick(true, false, 3, 600, Focus, null, Now).Shown;
        var step = LiveActivityPlan.OnTick(false, false, 1, Focus * 60, Focus, running, Now);
        Assert.Equal(LiveActivityAction.End, step.Action);
        Assert.Null(step.Shown);
    }

    [Fact]
    public void Reset_FromABreak_IsIdleToo()
    {
        Assert.True(LiveActivityPlan.IsIdle(false, false, 1, Focus * 60, Focus));
        var step = LiveActivityPlan.OnTick(false, false, 1, Focus * 60, Focus, new LiveActivityShown(true, 2, false, Now), Now);
        Assert.Equal(LiveActivityAction.End, step.Action);
    }

    [Theory]
    [InlineData(true, false, 1, Focus * 60)]   // running
    [InlineData(false, true, 1, Focus * 60)]   // break
    [InlineData(false, false, 2, Focus * 60)]  // later round
    [InlineData(false, false, 1, Focus * 60 - 1)] // started but paused
    public void NotIdle(bool running, bool isBreak, int round, int secondsLeft)
        => Assert.False(LiveActivityPlan.IsIdle(running, isBreak, round, secondsLeft, Focus));

    [Fact]
    public void FirstRunningTick_Updates()
    {
        var step = LiveActivityPlan.OnTick(true, false, 1, 1500, Focus, null, Now);
        Assert.Equal(LiveActivityAction.Update, step.Action);
        Assert.False(step.IsPaused);
        Assert.Equal(Now.AddSeconds(1500), step.Shown!.Value.EndsAt);
    }

    [Fact]
    public void RunningTick_WithSameStateAndNoDrift_IsIgnored()
    {
        var shown = LiveActivityPlan.OnTick(true, false, 1, 1500, Focus, null, Now).Shown;
        var step = LiveActivityPlan.OnTick(true, false, 1, 1499, Focus, shown, Now.AddSeconds(1));
        Assert.Equal(LiveActivityAction.None, step.Action);
    }

    [Fact]
    public void RunningTick_WithDrift_Updates()
    {
        var shown = LiveActivityPlan.OnTick(true, false, 1, 1500, Focus, null, Now).Shown;
        var step = LiveActivityPlan.OnTick(true, false, 1, 1400, Focus, shown, Now.AddSeconds(1));
        Assert.Equal(LiveActivityAction.Update, step.Action);
    }

    [Fact]
    public void PhaseChange_Updates()
    {
        var shown = LiveActivityPlan.OnTick(true, false, 1, 1500, Focus, null, Now).Shown;
        var step = LiveActivityPlan.OnTick(true, true, 1, 300, Focus, shown, Now.AddSeconds(1500));
        Assert.Equal(LiveActivityAction.Update, step.Action);
    }

    [Fact]
    public void Pause_UpdatesOnceThenStaysQuiet()
    {
        var shown = LiveActivityPlan.OnTick(true, false, 1, 1500, Focus, null, Now).Shown;
        var paused = LiveActivityPlan.OnTick(false, false, 1, 900, Focus, shown, Now);
        Assert.Equal(LiveActivityAction.Update, paused.Action);
        Assert.True(paused.IsPaused);
        var again = LiveActivityPlan.OnTick(false, false, 1, 900, Focus, paused.Shown, Now.AddSeconds(5));
        Assert.Equal(LiveActivityAction.None, again.Action);
    }

    [Fact]
    public void Resume_AfterPause_Updates()
    {
        var paused = LiveActivityPlan.OnTick(false, false, 1, 900, Focus, null, Now).Shown;
        var step = LiveActivityPlan.OnTick(true, false, 1, 900, Focus, paused, Now);
        Assert.Equal(LiveActivityAction.Update, step.Action);
        Assert.False(step.IsPaused);
    }

    [Fact]
    public void OnEnded_AlwaysEnds()
    {
        var step = LiveActivityPlan.OnEnded(Now);
        Assert.Equal(LiveActivityAction.End, step.Action);
        Assert.Null(step.Shown);
    }
}
