using System.Security.Cryptography;
using System.Text;

namespace StudyLife.App.Core;

/// <summary>What came back from the system-browser sign-in, after the checks.</summary>
public enum AppSignInOutcome
{
    /// <summary>A handoff code for this request: redeem it with <see cref="AppSignInRequest.CodeVerifier"/>.</summary>
    Code,
    /// <summary>The device-link page finished (the new passkey waits for approval); no session.</summary>
    Linked,
    /// <summary>Nothing usable: cancelled, a wrong or missing state, or no code.</summary>
    Rejected,
}

public readonly record struct AppSignInResult(AppSignInOutcome Outcome, string? Code = null);

/// <summary>
/// One sign-in of the native app through the system browser (2026-11-16 audit, M2): the PKCE pair
/// (RFC 7636, S256) and a random state, both kept in this process only. The auth page is opened with
/// <see cref="StartQuery"/>; the redirect back (studylife://auth on mobile, the loopback listener on
/// Windows) is accepted by <see cref="Interpret"/> only when it carries this request's state, so a
/// redirect the app did not start (another app firing studylife://auth?code=..., a web page hitting
/// the loopback port) is refused. A raw token in the redirect is never accepted: the server hands a
/// sign-in back only as a code that needs <see cref="CodeVerifier"/>.
/// </summary>
public sealed class AppSignInRequest
{
    private AppSignInRequest(string codeVerifier, string state)
    {
        CodeVerifier = codeVerifier;
        CodeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        State = state;
    }

    public string CodeVerifier { get; }
    public string CodeChallenge { get; }
    public string State { get; }

    public static AppSignInRequest Create() =>
        new(Base64Url(RandomNumberGenerator.GetBytes(32)), Base64Url(RandomNumberGenerator.GetBytes(24)));

    /// <summary>The query of the auth page: "app=1&amp;appchallenge=...&amp;appstate=..." plus the
    /// loopback return URL on Windows. Without the leading "?".</summary>
    public string StartQuery(string? loopbackReturnUrl = null) =>
        "app=1"
        + (loopbackReturnUrl is null ? "" : "&appret=" + Uri.EscapeDataString(loopbackReturnUrl))
        + "&appchallenge=" + Uri.EscapeDataString(CodeChallenge)
        + "&appstate=" + Uri.EscapeDataString(State);

    /// <summary>Checks the parameters of the redirect back. The state must match exactly
    /// (constant-time); a "token" parameter is ignored, it is never a way in.</summary>
    public AppSignInResult Interpret(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("state", out var state) || !StateMatches(state))
            return new AppSignInResult(AppSignInOutcome.Rejected);
        if (parameters.TryGetValue("code", out var code) && code is { Length: > 0 })
            return new AppSignInResult(AppSignInOutcome.Code, code);
        if (parameters.ContainsKey("linked"))
            return new AppSignInResult(AppSignInOutcome.Linked);
        return new AppSignInResult(AppSignInOutcome.Rejected);
    }

    private bool StateMatches(string? candidate) =>
        candidate is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(State));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
