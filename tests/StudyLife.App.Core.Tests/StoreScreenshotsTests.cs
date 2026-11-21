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
    [InlineData("/notes?")]
    [InlineData("/notes?note")]
    [InlineData("/notes?note=")]
    [InlineData("/notes?note=1&")]
    [InlineData("/notes?note=<b>")]
    [InlineData("/notes?note=1=2")]
    [InlineData("/notes?next=//evil.example.com")]
    [InlineData("/notes?note=1#top")]
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

    [Theory]
    [InlineData("/notes?note=3")]
    [InlineData("/notes?note=3&view=detail")]
    public void ASimpleQuery_StaysPartOfTheRoute(string route) =>
        Assert.Equal(route, Decide(true, "--store-screenshots", "https://localhost:5443", "--route", route).Plan!.Route);
}

public class StoreScreenshotTimerModeTests
{
    private static StoreScreenshotPlan Plan(string route) => new(new Uri("https://localhost:5443/"), route, "09:41");

    [Theory]
    [InlineData("/focus")]
    [InlineData("/focus/")]
    [InlineData("/Focus")]
    [InlineData("/focus?x=1")]
    public void TheFocusScreen_AlwaysShowsClassic25(string route) =>
        Assert.Equal("pomodoro", Plan(route).TimerModeStyle);

    [Theory]
    [InlineData("/")]
    [InlineData("/calendar")]
    [InlineData("/notes?note=3")]
    [InlineData("/auswertung")]
    [InlineData("/focus-history")]
    public void OtherScreens_LeaveTheTimerAlone(string route) =>
        Assert.Null(Plan(route).TimerModeStyle);
}

public class StoreScreenshotClockTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    /// <summary>A real clock at a fixed instant in a given zone.</summary>
    private sealed class RealClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static StoreScreenshotPlan Plan(string? clock) => new(new Uri("https://localhost:5443/"), "/", clock);

    [Fact]
    public void WithoutAClock_ThePlanKeepsTheRealClock()
    {
        var real = new RealClock(DateTimeOffset.UtcNow, Berlin);

        Assert.Same(real, Plan(null).CreateClock(real));
    }

    [Fact]
    public void ThePinnedClock_ReadsThePlannedTimeOnTheRealDate()
    {
        // 23:30 UTC on 18 November is already 19 November in Berlin: the date is the local one.
        var real = new RealClock(new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero), Berlin);

        var now = Plan("09:41").CreateClock(real).GetLocalNow();

        Assert.Equal(new DateTime(2026, 11, 19, 9, 41, 0), now.DateTime);
        Assert.Equal(TimeSpan.FromHours(1), now.Offset);
    }

    [Fact]
    public void ThePinnedClock_StandsStill()
    {
        var real = new RealClock(new DateTimeOffset(2026, 11, 19, 15, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        var clock = Plan("09:41").CreateClock(real);

        Assert.Equal(clock.GetUtcNow(), clock.GetUtcNow());
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 41, 0, TimeSpan.Zero), clock.GetUtcNow());
    }

    [Fact]
    public void ATimeInTheSpringForwardGap_IsTakenOneHourLater()
    {
        // 29 March 2026: Berlin jumps from 02:00 to 03:00, so 02:30 does not exist that day.
        var real = new RealClock(new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero), Berlin);

        var now = new PinnedTimeProvider(new TimeOnly(2, 30), real).GetLocalNow();

        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), now.DateTime);
    }
}

public class StoreScreenshotReadinessTests
{
    private static StoreScreenshotObservation Look(bool signedIn, string path, bool busy = false, int seconds = 1) =>
        new(signedIn, path, busy, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void ARouteWithAQuery_IsReachedByItsPath()
    {
        var readiness = new StoreScreenshotReadiness("/notes?note=3", quietLooksNeeded: 1);

        Assert.Equal(StoreScreenshotStep.Navigate, readiness.Next(Look(true, "/")));
        Assert.Equal(StoreScreenshotStep.Capture, readiness.Next(Look(true, "/notes")));
    }

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
