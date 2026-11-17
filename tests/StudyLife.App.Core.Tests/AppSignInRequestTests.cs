using System.Security.Cryptography;
using System.Text;
using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>2026-11-16 audit, M2: the app accepts a sign-in only as a PKCE code with its own state.</summary>
public class AppSignInRequestTests
{
    private static Dictionary<string, string> Redirect(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void TheStartQuery_CarriesAnS256Challenge_AndTheState()
    {
        var request = AppSignInRequest.Create();
        var expectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expectedChallenge, request.CodeChallenge);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", request.CodeChallenge);
        Assert.Matches("^[A-Za-z0-9_-]{16,128}$", request.State);
        Assert.Equal($"app=1&appchallenge={request.CodeChallenge}&appstate={request.State}", request.StartQuery());
        Assert.StartsWith("app=1&appret=http%3A%2F%2F127.0.0.1%3A5000%2F&", request.StartQuery("http://127.0.0.1:5000/"));
    }

    [Fact]
    public void EveryRequest_HasItsOwnSecrets()
    {
        var a = AppSignInRequest.Create();
        var b = AppSignInRequest.Create();

        Assert.NotEqual(a.CodeVerifier, b.CodeVerifier);
        Assert.NotEqual(a.State, b.State);
    }

    [Fact]
    public void ACodeWithThisRequestsState_IsAccepted()
    {
        var request = AppSignInRequest.Create();

        var result = request.Interpret(Redirect(("code", "the-code"), ("state", request.State)));

        Assert.Equal(AppSignInOutcome.Code, result.Outcome);
        Assert.Equal("the-code", result.Code);
    }

    [Fact]
    public void AFinishedDeviceLink_WithThisRequestsState_IsAccepted()
    {
        var request = AppSignInRequest.Create();

        Assert.Equal(AppSignInOutcome.Linked, request.Interpret(Redirect(("linked", "1"), ("state", request.State))).Outcome);
    }

    /// <summary>Account swap: another app fires studylife://auth?code=... (or a web page hits the
    /// loopback port) while a sign-in is pending.</summary>
    [Fact]
    public void ARedirectWithoutOrWithAnotherState_IsRefused()
    {
        var request = AppSignInRequest.Create();
        var other = AppSignInRequest.Create();

        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("code", "attacker-code"))).Outcome);
        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("code", "attacker-code"), ("state", other.State))).Outcome);
        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("code", "attacker-code"), ("state", ""))).Outcome);
        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("linked", "1"), ("state", other.State))).Outcome);
    }

    /// <summary>The removed fallback: a raw token in the redirect is never a way in, even with the right state.</summary>
    [Fact]
    public void ARawToken_IsNeverAccepted()
    {
        var request = AppSignInRequest.Create();

        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("token", "attacker-token"))).Outcome);
        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("token", "attacker-token"), ("state", request.State))).Outcome);
    }

    [Fact]
    public void AnEmptyOrCancelledRedirect_IsRefused()
    {
        var request = AppSignInRequest.Create();

        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect()).Outcome);
        Assert.Equal(AppSignInOutcome.Rejected, request.Interpret(Redirect(("code", ""), ("state", request.State))).Outcome);
    }
}
