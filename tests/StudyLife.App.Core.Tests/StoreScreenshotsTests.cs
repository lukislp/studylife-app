using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class StoreScreenshotLaunchTests
{
    private static StoreScreenshotLaunchResult Decide(bool allowed, params string[] arguments) =>
        StoreScreenshotLaunch.Decide(arguments, allowed);

    [Fact]
    public void NoArgument_IsANormalStart()
    {
        var result = Decide(true, "/path/StudyLife.App", "-AppleLanguages", "(de)");

        Assert.Equal(StoreScreenshotLaunchProblem.NotRequested, result.Problem);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void ABuildThatMayNotUseIt_IgnoresTheArgument()
    {
        var result = Decide(false, "--store-screenshots", "https://localhost:5443", "--route", "/calendar");

        Assert.Equal(StoreScreenshotLaunchProblem.NotAllowed, result.Problem);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void ServerAndRoute_BecomeThePlan()
    {
        var result = Decide(true, "/path/StudyLife.App", "-AppleLanguages", "(de)",
            "--store-screenshots", "https://localhost:5443", "--route", "/calendar");

        Assert.Equal(StoreScreenshotLaunchProblem.None, result.Problem);
        Assert.Equal(new Uri("https://localhost:5443/"), result.Plan!.Server);
        Assert.Equal("/calendar", result.Plan.Route);
    }

    [Fact]
    public void WithoutARoute_TheDashboardIsShown()
    {
        var result = Decide(true, "--store-screenshots", "https://demo.example.com/studylife");

        Assert.Equal("/", result.Plan!.Route);
        Assert.Equal(new Uri("https://demo.example.com/studylife/"), result.Plan.Server);
    }

    [Theory]
    [InlineData("--store-screenshots")]
    [InlineData("--store-screenshots", "")]
    [InlineData("--store-screenshots", "--route", "/notes")]
    public void MissingServer_IsReported(params string[] arguments) =>
        Assert.Equal(StoreScreenshotLaunchProblem.MissingServer, StoreScreenshotLaunch.Decide(arguments, true).Problem);

    [Theory]
    [InlineData("localhost:5443")]
    [InlineData("studylife.example.com")]
    [InlineData("ftp://localhost")]
    [InlineData("http://studylife.example.com")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("file:///etc/passwd")]
    public void ServersThatAreNotHttpsOrLocal_AreRefused(string server) =>
        Assert.Equal(StoreScreenshotLaunchProblem.InvalidServer, Decide(true, "--store-screenshots", server).Problem);

    [Theory]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080")]
    public void PlainHttp_IsAcceptedForThisMachineOnly(string server) =>
        Assert.Equal(StoreScreenshotLaunchProblem.None, Decide(true, "--store-screenshots", server).Problem);

    [Theory]
    [InlineData("calendar")]
    [InlineData("//evil.example.com")]
    [InlineData("/../setup")]
    [InlineData("/notes?id=1")]
    [InlineData("/Calendar")]
    [InlineData("https://example.com/")]
    [InlineData("/a b")]
    public void RoutesThatAreNotPlainPaths_AreRefused(string route) =>
        Assert.Equal(StoreScreenshotLaunchProblem.InvalidRoute,
            Decide(true, "--store-screenshots", "https://localhost:5443", "--route", route).Problem);

    [Fact]
    public void ARouteFlagWithoutValue_IsRefused() =>
        Assert.Equal(StoreScreenshotLaunchProblem.InvalidRoute,
            Decide(true, "--store-screenshots", "https://localhost:5443", "--route").Problem);

    [Fact]
    public void WithoutAClock_TheClockIsLeftAlone() =>
        Assert.Null(Decide(true, "--store-screenshots", "https://localhost:5443").Plan!.Clock);

    [Theory]
    [InlineData("09:41")]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void A24HourClock_IsPartOfThePlan(string clock) =>
        Assert.Equal(clock, Decide(true, "--store-screenshots", "https://localhost:5443", "--route", "/", "--clock", clock).Plan!.Clock);

    [Theory]
    [InlineData("9:41")]
    [InlineData("24:00")]
    [InlineData("09:60")]
    [InlineData("09-41")]
    [InlineData("+9:41")]
    [InlineData("<b>1</b>")]
    public void OtherClocks_AreRefused(string clock) =>
        Assert.Equal(StoreScreenshotLaunchProblem.InvalidClock,
            Decide(true, "--store-screenshots", "https://localhost:5443", "--clock", clock).Problem);

    [Fact]
    public void AClockFlagWithoutValue_IsRefused() =>
        Assert.Equal(StoreScreenshotLaunchProblem.InvalidClock,
            Decide(true, "--store-screenshots", "https://localhost:5443", "--clock").Problem);

    [Theory]
    [InlineData("/")]
    [InlineData("/auswertung")]
    [InlineData("/connect/focus-guard_2")]
    public void PlainRoutes_AreAccepted(string route) =>
        Assert.Equal(route, Decide(true, "--store-screenshots", "https://localhost:5443", "--route", route).Plan!.Route);
}

public class StoreScreenshotReadinessTests
{
    private static StoreScreenshotObservation Look(bool signedIn, string path, bool busy = false, int seconds = 1) =>
        new(signedIn, path, busy, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void SignedOut_Waits()
    {
        var readiness = new StoreScreenshotReadiness("/calendar");

        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(false, "/login")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(false, "/login")));
    }

    [Fact]
    public void SignedInElsewhere_OpensTheRouteOnce_ThenGivesTheNavigationTime()
    {
        var readiness = new StoreScreenshotReadiness("/calendar");

        Assert.Equal(StoreScreenshotStep.Navigate, readiness.Next(Look(true, "/")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/")));
        // Still not there (a reload after the demo sign-in took the app back to the dashboard): open it again.
        Assert.Equal(StoreScreenshotStep.Navigate, readiness.Next(Look(true, "/")));
    }

    [Fact]
    public void OnTheRoute_CapturesOnlyAfterEnoughQuietLooks()
    {
        var readiness = new StoreScreenshotReadiness("/notes", quietLooksNeeded: 3);

        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/notes")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/notes")));
        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "/notes")));
    }

    [Fact]
    public void LoadingPlaceholders_RestartTheQuietCount()
    {
        var readiness = new StoreScreenshotReadiness("/notes", quietLooksNeeded: 2);

        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/notes")));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/notes", busy: true)));
        Assert.Equal(StoreScreenshotStep.Wait, readiness.Next(Look(true, "/notes")));
        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "/notes")));
    }

    [Fact]
    public void TheDashboard_MatchesTheRoot()
    {
        var readiness = new StoreScreenshotReadiness("/", quietLooksNeeded: 1);

        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "")));
    }

    [Fact]
    public void TrailingSlashAndCase_DoNotMatter()
    {
        var readiness = new StoreScreenshotReadiness("/calendar", quietLooksNeeded: 1);

        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "/Calendar/")));
    }

    [Fact]
    public void AfterTheTimeout_ItGivesUp()
    {
        var readiness = new StoreScreenshotReadiness("/calendar", timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(StoreScreenshotStep.GiveUp, readiness.Next(Look(true, "/calendar", busy: true, seconds: 10)));
    }

    [Fact]
    public void OnceCaptured_ItStaysCaptured()
    {
        var readiness = new StoreScreenshotReadiness("/focus", quietLooksNeeded: 1, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "/focus")));
        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(false, "/login", seconds: 60)));
    }

    [Fact]
    public void AtLeastOneQuietLook_IsRequired() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoreScreenshotReadiness("/", quietLooksNeeded: 0));
}
