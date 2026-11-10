namespace StudyLife.App.Core;

public enum ServerEntryOutcome
{
    /// <summary>The address may be saved.</summary>
    Accepted,
    Empty,
    NotAUrl,
    Unreachable,
    NotStudyLife,
}

public readonly record struct ServerEntryResult(ServerEntryOutcome Outcome, Uri? Uri)
{
    public bool IsAccepted => Outcome == ServerEntryOutcome.Accepted;

    /// <summary>The user can still go on with this address (a server that is down for a moment, an old
    /// server): the screen then offers "use anyway". An empty or malformed address cannot be used.</summary>
    public bool CanUseAnyway => Outcome is ServerEntryOutcome.Unreachable or ServerEntryOutcome.NotStudyLife;
}

/// <summary>The validation of the first-start address field, in one place: parse the text, then ask the
/// server whether it is a StudyLife server. With <c>useAnyway</c> (the user already saw the warning and
/// confirmed) the network check is skipped.</summary>
public sealed class ServerEntry(IServerChecker checker)
{
    public async Task<ServerEntryResult> SubmitAsync(string? input, bool useAnyway, CancellationToken cancellationToken = default)
    {
        var parsed = ServerAddress.Parse(input);
        switch (parsed.Problem)
        {
            case ServerAddressProblem.Empty:
                return new(ServerEntryOutcome.Empty, null);
            case ServerAddressProblem.NotAUrl:
                return new(ServerEntryOutcome.NotAUrl, null);
        }

        var uri = parsed.Uri!;
        if (useAnyway)
            return new(ServerEntryOutcome.Accepted, uri);

        return await checker.CheckAsync(uri, cancellationToken) switch
        {
            ServerCheckResult.StudyLife => new(ServerEntryOutcome.Accepted, uri),
            ServerCheckResult.NotStudyLife => new(ServerEntryOutcome.NotStudyLife, uri),
            _ => new(ServerEntryOutcome.Unreachable, uri),
        };
    }
}
