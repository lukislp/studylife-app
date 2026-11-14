using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

/// <summary>The one decision about the sign-in screen's server bar: never a dead end while signed out.</summary>
public class SignInServerStatusTests
{
    private static readonly Uri Typo = new("https://studylfe.example.com/");
    private static readonly Uri Internal = new("https://studylife.heim.lan/");

    [Fact]
    public void SignedIn_TheBarIsHidden_TheSwitchIsInSetup() =>
        Assert.False(SignInServerStatus.Decide(signedIn: true, Typo, ServerCheck.Of(ServerCheckResult.Unreachable)).ShowChangeServer);

    [Fact]
    public void BeforeAServerIsConfigured_TheBarIsHidden() =>
        Assert.False(SignInServerStatus.Decide(signedIn: false, null, null).ShowChangeServer);

    [Fact]
    public void SignedOut_TheActionIsThereBeforeTheCheckAnswers()
    {
        var status = SignInServerStatus.Decide(signedIn: false, Typo, null);

        Assert.True(status.ShowChangeServer);
        Assert.Equal(SignInServerNotice.None, status.Notice);
    }

    [Theory]
    [InlineData(ServerCheckResult.Unreachable, SignInServerNotice.Unreachable)]
    [InlineData(ServerCheckResult.UntrustedCertificate, SignInServerNotice.UntrustedCertificate)]
    [InlineData(ServerCheckResult.NotStudyLife, SignInServerNotice.NotStudyLife)]
    public void SignedOut_AProblemIsNamedNextToTheAction(ServerCheckResult result, SignInServerNotice expected)
    {
        var status = SignInServerStatus.Decide(signedIn: false, Typo, ServerCheck.Of(result));

        Assert.True(status.ShowChangeServer);
        Assert.Equal(expected, status.Notice);
    }

    [Fact]
    public void AHealthyServer_StillOffersTheAction()
    {
        var status = SignInServerStatus.Decide(false, new Uri("https://studylife.example.com/"),
            new ServerCheck(ServerCheckResult.StudyLife, "studylife.example.com"));

        Assert.True(status.ShowChangeServer);
        Assert.Equal(SignInServerNotice.None, status.Notice);
    }

    [Fact]
    public void AnInternalAddressOutsideThePasskeyDomain_GetsTheHintAndThePublicAddress()
    {
        var status = SignInServerStatus.Decide(false, Internal,
            new ServerCheck(ServerCheckResult.StudyLife, "studylife.example.org", new Uri("https://studylife.example.org")));

        Assert.Equal(SignInServerNotice.PasskeysElsewhere, status.Notice);
        Assert.Equal("studylife.example.org", status.PasskeyDomain);
        Assert.Equal(new Uri("https://studylife.example.org/"), status.SuggestedAddress);
    }

    [Fact]
    public void WithoutAPinnedOrigin_TheSuggestionIsTheDomainWithTheSamePath()
    {
        var status = SignInServerStatus.Decide(false, new Uri("https://10.0.0.5:8443/studylife/"),
            new ServerCheck(ServerCheckResult.StudyLife, "example.org"));

        Assert.Equal(new Uri("https://example.org/studylife/"), status.SuggestedAddress);
    }

    [Fact]
    public void AnOriginOutsideThePasskeyDomain_IsNotSuggested()
    {
        var status = SignInServerStatus.Decide(false, Internal,
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
