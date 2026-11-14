namespace StudyLife.App.Core;

/// <summary>What the bar next to the sign-in screen says about the configured server.</summary>
public enum SignInServerNotice
{
    /// <summary>Nothing to say (a StudyLife server whose passkeys work at this address, or not checked yet).</summary>
    None,
    Unreachable,
    UntrustedCertificate,
    NotStudyLife,
    /// <summary>The server pinned its passkeys to another domain than the configured address: a passkey
    /// sign-in cannot work here (WebAuthn binds passkeys to one domain).</summary>
    PasskeysElsewhere,
}

/// <summary>
/// The decision for the native shell's sign-in screen, in one place: whether the "change server" action is
/// shown and what it says about the server. Signed out with a configured server, the action is always there,
/// whatever the server answers (a mistyped, unreachable or untrusted address must never be a dead end; the
/// shared sign-in page cannot offer it because it needs a working server). Signed in, the switch lives in
/// Setup, so the bar is not shown.
/// </summary>
public sealed record SignInServerStatus(bool ShowChangeServer, SignInServerNotice Notice, string? PasskeyDomain, Uri? SuggestedAddress)
{
    public static readonly SignInServerStatus Hidden = new(false, SignInServerNotice.None, null, null);

    /// <param name="signedIn">A session token exists.</param>
    /// <param name="server">The configured server address, null before the first start finished.</param>
    /// <param name="check">The answer of <see cref="IServerChecker"/> for <paramref name="server"/>, null while it runs.</param>
    public static SignInServerStatus Decide(bool signedIn, Uri? server, ServerCheck? check)
    {
        if (signedIn || server is null) return Hidden;
        return check?.Result switch
        {
            ServerCheckResult.Unreachable => Shown(SignInServerNotice.Unreachable),
            ServerCheckResult.UntrustedCertificate => Shown(SignInServerNotice.UntrustedCertificate),
            ServerCheckResult.NotStudyLife => Shown(SignInServerNotice.NotStudyLife),
            ServerCheckResult.StudyLife when PasskeyScope.Covers(check!.PasskeyRpId, server) is false =>
                new(true, SignInServerNotice.PasskeysElsewhere, check.PasskeyRpId,
                    PasskeyScope.SuggestedAddress(check.PasskeyRpId!, check.PasskeyOrigin, server)),
            _ => Shown(SignInServerNotice.None),
        };
    }

    private static SignInServerStatus Shown(SignInServerNotice notice) => new(true, notice, null, null);
}

/// <summary>WebAuthn's rule for which hosts a relying-party id covers, and the address to suggest instead.</summary>
public static class PasskeyScope
{
    /// <summary>True when <paramref name="server"/>'s host is the relying-party id or a subdomain of it;
    /// null when the server pinned none (passkeys then follow whatever host the app uses).</summary>
    public static bool? Covers(string? rpId, Uri server)
    {
        if (string.IsNullOrWhiteSpace(rpId)) return null;
        var host = server.IdnHost.TrimEnd('.');
        var domain = rpId.Trim().TrimEnd('.');
        return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The pinned public origin when it lies inside the relying-party id, else https://&lt;rpId&gt;/ -
    /// with the configured address's path, so an installation under a sub-path keeps it.</summary>
    public static Uri SuggestedAddress(string rpId, Uri? origin, Uri server)
    {
        var host = origin is not null && Covers(rpId, origin) == true ? origin.Authority : rpId.Trim().TrimEnd('.');
        return ServerAddress.WithTrailingSlash(new Uri($"https://{host}{server.AbsolutePath}"));
    }
}
