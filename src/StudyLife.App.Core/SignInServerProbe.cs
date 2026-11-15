namespace StudyLife.App.Core;

/// <summary>What one round of <see cref="SignInServerProbe"/> found out.</summary>
/// <param name="Next">How the round continues (backoff, events only, settled, or waiting for the network).</param>
/// <param name="BecameReachable">The server was unreachable before and answers now: the shell tells the shared
/// sign-in page, so it re-asks its own questions at once.</param>
public sealed record SignInServerRound(ServerRecheck Next, bool BecameReachable);

/// <summary>
/// The signed-out server check of the sign-in bar, one round at a time (the caller's retry loop decides when the
/// next round runs). Holds the last answer, which <see cref="SignInServerStatus.Decide"/> turns into the notice.
/// <list type="bullet">
/// <item>No network at all: the server is not asked (<see cref="ServerRecheckPolicy.WorthAsking"/>) and the old
/// answer is dropped, since it describes a network that is gone; the bar says "no network" from the reachability
/// alone, and the round waits for the network to come back.</item>
/// <item>A check that fails in an unexpected way (the checker reports failures as results, so this is a bug or a
/// platform surprise) counts as <see cref="ServerCheckResult.Unreachable"/>: the bar names a problem and keeps
/// retrying instead of silently showing nothing.</item>
/// <item>Cancellation (the bar goes away) is passed on, not turned into an answer.</item>
/// </list>
/// </summary>
public sealed class SignInServerProbe(IServerChecker checker)
{
    /// <summary>The answer of the last round that asked, null before the first one and while there is no network.</summary>
    public ServerCheck? LastCheck { get; private set; }

    public async Task<SignInServerRound> AskAsync(Uri server, NetworkReach reach, CancellationToken cancellationToken)
    {
        if (!ServerRecheckPolicy.WorthAsking(reach))
        {
            LastCheck = null;
            return new SignInServerRound(ServerRecheck.WaitForNetwork, BecameReachable: false);
        }
        var wasUnreachable = LastCheck?.Result == ServerCheckResult.Unreachable;
        ServerCheck check;
        try { check = await checker.CheckAsync(server, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { check = ServerCheck.Of(ServerCheckResult.Unreachable); }
        LastCheck = check;
        return new SignInServerRound(ServerRecheckPolicy.For(check.Result),
            BecameReachable: wasUnreachable && check.Result != ServerCheckResult.Unreachable);
    }
}
