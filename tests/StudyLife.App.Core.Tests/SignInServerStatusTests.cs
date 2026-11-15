using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>The one decision about the sign-in screen's server bar: never a dead end while signed out.</summary>
public class SignInServerStatusTests
{
    private static SignInServerStatus Decide(bool signedIn, Uri? server, ServerCheck? check) =>
        SignInServerStatus.Decide(signedIn, server, check, NetworkReach.Internet);

    private static readonly Uri Typo = new("https://studylfe.example.com/");
    private static readonly Uri Internal = new("https://studylife.heim.lan/");

    [Fact]
    public void SignedIn_TheBarIsHidden_TheSwitchIsInSetup() =>
        Assert.False(Decide(signedIn: true, Typo, ServerCheck.Of(ServerCheckResult.Unreachable)).ShowChangeServer);

    [Fact]
    public void BeforeAServerIsConfigured_TheBarIsHidden() =>
        Assert.False(Decide(signedIn: false, null, null).ShowChangeServer);

    [Fact]
    public void SignedOut_TheActionIsThereBeforeTheCheckAnswers()
    {
        var status = Decide(signedIn: false, Typo, null);

        Assert.True(status.ShowChangeServer);
        Assert.Equal(SignInServerNotice.None, status.Notice);
    }

    [Theory]
    [InlineData(ServerCheckResult.Unreachable, SignInServerNotice.Unreachable)]
    [InlineData(ServerCheckResult.UntrustedCertificate, SignInServerNotice.UntrustedCertificate)]
    [InlineData(ServerCheckResult.NotStudyLife, SignInServerNotice.NotStudyLife)]
    public void SignedOut_AProblemIsNamedNextToTheAction(ServerCheckResult result, SignInServerNotice expected)
    {
        var status = Decide(signedIn: false, Typo, ServerCheck.Of(result));

        Assert.True(status.ShowChangeServer);
        Assert.Equal(expected, status.Notice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ServerCheckResult.StudyLife)]
    [InlineData(ServerCheckResult.Unreachable)]
    [InlineData(ServerCheckResult.NotStudyLife)]
    public void NoNetworkAtAll_IsSaidAtOnce_WhateverAnEarlierCheckAnswered(ServerCheckResult? earlier)
    {
        var status = SignInServerStatus.Decide(false, Typo, earlier is { } r ? ServerCheck.Of(r) : null, NetworkReach.None);

        Assert.True(status.ShowChangeServer);
        Assert.Equal(SignInServerNotice.NoNetwork, status.Notice);
    }

    [Theory]
    [InlineData(NetworkReach.Constrained)]
    [InlineData(NetworkReach.Local)]
    [InlineData(NetworkReach.Unknown)]
    public void WithAnyNetwork_TheServerCheckDecides(NetworkReach reach)
    {
        // A captive portal or a LAN without the server cannot be judged from the reachability: the check decides.
        Assert.Equal(SignInServerNotice.Unreachable,
            SignInServerStatus.Decide(false, Typo, ServerCheck.Of(ServerCheckResult.Unreachable), reach).Notice);
        Assert.Equal(SignInServerNotice.None,
            SignInServerStatus.Decide(false, Typo, ServerCheck.Of(ServerCheckResult.StudyLife), reach).Notice);
    }

    [Fact]
    public void SignedIn_TheBarStaysHiddenWithoutNetwork() =>
        Assert.False(SignInServerStatus.Decide(true, Typo, null, NetworkReach.None).ShowChangeServer);

    [Fact]
    public void AHealthyServer_StillOffersTheAction()
    {
        var status = Decide(false, new Uri("https://studylife.example.com/"),
            new ServerCheck(ServerCheckResult.StudyLife, "studylife.example.com"));

        Assert.True(status.ShowChangeServer);
        Assert.Equal(SignInServerNotice.None, status.Notice);
    }

    [Fact]
    public void AnInternalAddressOutsideThePasskeyDomain_GetsTheHintAndThePublicAddress()
    {
        var status = Decide(false, Internal,
            new ServerCheck(ServerCheckResult.StudyLife, "studylife.example.org", new Uri("https://studylife.example.org")));

        Assert.Equal(SignInServerNotice.PasskeysElsewhere, status.Notice);
        Assert.Equal("studylife.example.org", status.PasskeyDomain);
        Assert.Equal(new Uri("https://studylife.example.org/"), status.SuggestedAddress);
    }

    [Fact]
    public void WithoutAPinnedOrigin_TheSuggestionIsTheDomainWithTheSamePath()
    {
        var status = Decide(false, new Uri("https://10.0.0.5:8443/studylife/"),
            new ServerCheck(ServerCheckResult.StudyLife, "example.org"));

        Assert.Equal(new Uri("https://example.org/studylife/"), status.SuggestedAddress);
    }

    [Fact]
    public void AnOriginOutsideThePasskeyDomain_IsNotSuggested()
    {
        var status = Decide(false, Internal,
            new ServerCheck(ServerCheckResult.StudyLife, "example.org", new Uri("https://evil.example.net")));

        Assert.Equal(new Uri("https://example.org/"), status.SuggestedAddress);
    }

    [Theory]
    [InlineData("studylife.example.org", "https://studylife.example.org/", true)]
    [InlineData("example.org", "https://studylife.example.org/", true)]
    [InlineData("EXAMPLE.org", "https://Studylife.Example.org/", true)]
    [InlineData("example.org", "https://notexample.org/", false)]
    [InlineData("studylife.example.org", "https://studylife.heim.lan/", false)]
    public void ThePasskeyDomainCoversItsSubdomains(string rpId, string server, bool expected) =>
        Assert.Equal(expected, PasskeyScope.Covers(rpId, new Uri(server)));

    [Fact]
    public void WithoutAPinnedDomain_NothingIsSaid() =>
        Assert.Null(PasskeyScope.Covers(null, Internal));
}
