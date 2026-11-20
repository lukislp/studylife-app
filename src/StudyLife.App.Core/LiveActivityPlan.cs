namespace StudyLife.App.Core;

/// <summary>What the Live Activity coordinator has to do after a timer event.</summary>
public enum LiveActivityAction
{
    /// <summary>Nothing changed that the card does not already show.</summary>
    None,
    /// <summary>Show or refresh the card (running or paused, see the plan's IsPaused).</summary>
    Update,
    /// <summary>Remove the card: reset/idle, mode switch, complete.</summary>
    End,
}

/// <summary>Last state pushed to the card; used to avoid redundant updates.</summary>
public readonly record struct LiveActivityShown(bool IsBreak, int Round, bool IsPaused, DateTimeOffset EndsAt);

public readonly record struct LiveActivityStep(LiveActivityAction Action, bool IsPaused, DateTimeOffset EndsAt, LiveActivityShown? Shown);

/// <summary>
/// Pure state mapping of the focus timer to Live Activity calls (kept free of the iOS
/// bridge so it can be unit tested). The countdown on the lock screen runs on its own, so an
/// update only happens on a state change (phase, round, pause) or when the computed phase end
/// drifts by more than <see cref="DriftSeconds"/>. "Idle" (not running, focus phase, round 1,
/// full focus time - what Reset and LoadMode leave behind) ends the card.
/// </summary>
public static class LiveActivityPlan
{
    public const double DriftSeconds = 3;

    public static bool IsIdle(bool isRunning, bool isBreak, int round, int secondsLeft, int focusMinutes)
        => !isRunning && !isBreak && round == 1 && secondsLeft == focusMinutes * 60;

    public static LiveActivityStep OnTick(
        bool isRunning, bool isBreak, int round, int secondsLeft, int focusMinutes,
        LiveActivityShown? shown, DateTimeOffset now)
    {
        var endsAt = now.AddSeconds(secondsLeft);

        if (!isRunning)
        {
            if (IsIdle(isRunning, isBreak, round, secondsLeft, focusMinutes))
                return new LiveActivityStep(LiveActivityAction.End, false, endsAt, null);

            // Paused: static remaining-time display, only updated on transition.
            if (shown is { IsPaused: true })
                return new LiveActivityStep(LiveActivityAction.None, true, endsAt, shown);
            return new LiveActivityStep(LiveActivityAction.Update, true, endsAt,
                new LiveActivityShown(isBreak, round, true, endsAt));
        }

        var sameState = shown is { IsPaused: false } s && s.IsBreak == isBreak && s.Round == round;
        var drifted = shown is not { } last || Math.Abs((endsAt - last.EndsAt).TotalSeconds) > DriftSeconds;
        if (sameState && !drifted)
            return new LiveActivityStep(LiveActivityAction.None, false, endsAt, shown);
        return new LiveActivityStep(LiveActivityAction.Update, false, endsAt,
            new LiveActivityShown(isBreak, round, false, endsAt));
    }

    /// <summary>Session complete, mode switch (LoadMode stops a running timer without a tick)
    /// and stop commands always end the card.</summary>
    public static LiveActivityStep OnEnded(DateTimeOffset now)
        => new(LiveActivityAction.End, false, now, null);
}
