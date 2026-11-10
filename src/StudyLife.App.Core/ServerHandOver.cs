namespace StudyLife.App.Core;

/// <summary>The pieces of leaving a server, in the order <see cref="ServerHandOver"/> runs them.</summary>
public enum HandOverStep
{
    UnregisterPush,
    SignOutOnServer,
    ClearSession,
    WipeLocalCopies,
    ForgetServer,
}

/// <summary>What each step does on this device; implemented by the MAUI app.</summary>
public interface IHandOverSteps
{
    /// <summary>Tells the old server to stop sending push notifications to this device (needs the session).</summary>
    Task UnregisterPushAsync(CancellationToken cancellationToken);

    /// <summary>Ends the session on the old server (needs the session).</summary>
    Task SignOutOnServerAsync(CancellationToken cancellationToken);

    /// <summary>Drops the session token and the account's offline caches on this device.</summary>
    Task ClearSessionAsync(CancellationToken cancellationToken);

    /// <summary>Removes what the app keeps outside the web view (widget, Watch, Spotlight).</summary>
    Task WipeLocalCopiesAsync(CancellationToken cancellationToken);

    /// <summary>Forgets the saved server address so the next start asks for one.</summary>
    Task ForgetServerAsync(CancellationToken cancellationToken);
}

public sealed record HandOverReport(IReadOnlyList<HandOverStep> Failed)
{
    public bool Completed => Failed.Count == 0;
}

/// <summary>
/// The one sequence of changing server: unregister push, sign out on the server, clear the session,
/// wipe the local copies, forget the address. The two server calls come first because they need the
/// session and the old address; they are the only steps that can hang on a dead server, so they get a
/// time limit. A step that fails or times out never stops the rest: leaving the server must always
/// work, even when the server is unreachable (the server session then simply expires on its own).
/// </summary>
public sealed class ServerHandOver(IHandOverSteps steps, TimeSpan? serverCallTimeout = null)
{
    public static readonly TimeSpan DefaultServerCallTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _serverCallTimeout = serverCallTimeout ?? DefaultServerCallTimeout;

    public async Task<HandOverReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var failed = new List<HandOverStep>();
        await RunStepAsync(HandOverStep.UnregisterPush, steps.UnregisterPushAsync, _serverCallTimeout, failed, cancellationToken);
        await RunStepAsync(HandOverStep.SignOutOnServer, steps.SignOutOnServerAsync, _serverCallTimeout, failed, cancellationToken);
        await RunStepAsync(HandOverStep.ClearSession, steps.ClearSessionAsync, null, failed, cancellationToken);
        await RunStepAsync(HandOverStep.WipeLocalCopies, steps.WipeLocalCopiesAsync, null, failed, cancellationToken);
        await RunStepAsync(HandOverStep.ForgetServer, steps.ForgetServerAsync, null, failed, cancellationToken);
        return new HandOverReport(failed);
    }

    private static async Task RunStepAsync(
        HandOverStep step,
        Func<CancellationToken, Task> run,
        TimeSpan? timeout,
        List<HandOverStep> failed,
        CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } t)
            {
                limit.CancelAfter(t);
                // WaitAsync also covers a step that ignores its token.
                await run(limit.Token).WaitAsync(t, cancellationToken);
            }
            else
            {
                await run(limit.Token);
            }
        }
        catch (Exception)
        {
            failed.Add(step);
        }
    }
}
