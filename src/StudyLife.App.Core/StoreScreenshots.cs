namespace StudyLife.App.Core;

/// <summary>What a store screenshot run asks the app to do: sign in on this server (with the demo
/// account its demo mode provides) and show this route (a path, optionally with a simple query such as
/// <c>/notes?note=3</c>). <paramref name="Clock"/> is the time the app's clock reads instead of the real one
/// ("09:41"), the same as the simulator's status bar override; null leaves the clock alone.</summary>
public sealed record StoreScreenshotPlan(Uri Server, string Route, string? Clock = null)
{
    /// <summary>The app's clock for this run: <see cref="Clock"/> pinned on today's date (see
    /// <see cref="PinnedTimeProvider"/>), or <paramref name="system"/> when the plan has no clock.</summary>
    public TimeProvider CreateClock(TimeProvider system) =>
        Clock is { } text ? new PinnedTimeProvider(TimeOnly.ParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture), system) : system;
}

/// <summary>
/// A clock that always reads the same local time of day on the real clock's current date: the store screenshot
/// mode registers it as the app's <see cref="TimeProvider"/>, so every "now" of the shared client (calendar now
/// line, countdowns, sidebar clock, which sessions are done) agrees with the 09:41 of the simulator's status
/// bar. The time zone is the real clock's, so local wall time is exactly <c>Time</c>. Timers built on this clock
/// are the real clock's: the pinned value only answers "what time is it".
/// </summary>
public sealed class PinnedTimeProvider(TimeOnly time, TimeProvider real) : TimeProvider
{
    public TimeOnly Time { get; } = time;

    public override TimeZoneInfo LocalTimeZone => real.LocalTimeZone;

    public override DateTimeOffset GetUtcNow()
    {
        var zone = real.LocalTimeZone;
        var localDate = TimeZoneInfo.ConvertTime(real.GetUtcNow(), zone).Date;
        var local = DateTime.SpecifyKind(localDate.Add(Time.ToTimeSpan()), DateTimeKind.Unspecified);
        // A pinned time inside a daylight saving gap does not exist locally; it is taken one hour later.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public override long GetTimestamp() => real.GetTimestamp();

    public override long TimestampFrequency => real.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        real.CreateTimer(callback, state, dueTime, period);
}

/// <summary>Why the launch arguments did not produce a plan.</summary>
public enum StoreScreenshotLaunchProblem
{
    None,
    /// <summary>No <c>--store-screenshots</c> argument: a normal start.</summary>
    NotRequested,
    /// <summary>The argument was given to a build that must never act on it (a store build, a device).</summary>
    NotAllowed,
    /// <summary><c>--store-screenshots</c> without a server address after it.</summary>
    MissingServer,
    /// <summary>Not an absolute https address, or plain http to anything but this machine.</summary>
    InvalidServer,
    /// <summary>A route that is not a plain in-app path such as <c>/calendar</c>.</summary>
    InvalidRoute,
    /// <summary>A clock that is not a 24-hour time such as <c>09:41</c>.</summary>
    InvalidClock,
}

public readonly record struct StoreScreenshotLaunchResult(StoreScreenshotPlan? Plan, StoreScreenshotLaunchProblem Problem);

/// <summary>
/// Reads the launch arguments of the store screenshot workflow
/// (<c>--store-screenshots &lt;server&gt; [--route /calendar] [--clock 09:41]</c>). The platform says whether this build may
/// act on them at all (<paramref name="allowedInThisBuild"/> of <see cref="Decide"/>): only a simulator build
/// compiled for screenshots does, so a store build ignores the arguments whatever they say. Pure: no device,
/// no network.
/// </summary>
public static class StoreScreenshotLaunch
{
    public const string ServerArgument = "--store-screenshots";
    public const string RouteArgument = "--route";
    public const string ClockArgument = "--clock";

    /// <summary>Longest route accepted; the app's routes are short single words.</summary>
    private const int MaxRouteLength = 64;

    public static StoreScreenshotLaunchResult Decide(IReadOnlyList<string> arguments, bool allowedInThisBuild)
    {
        var serverAt = IndexOf(arguments, ServerArgument);
        if (serverAt < 0)
            return new(null, StoreScreenshotLaunchProblem.NotRequested);
        if (!allowedInThisBuild)
            return new(null, StoreScreenshotLaunchProblem.NotAllowed);

        var serverText = ValueAfter(arguments, serverAt);
        if (serverText is null)
            return new(null, StoreScreenshotLaunchProblem.MissingServer);
        if (ParseServer(serverText) is not { } server)
            return new(null, StoreScreenshotLaunchProblem.InvalidServer);

        var route = "/";
        var routeAt = IndexOf(arguments, RouteArgument);
        if (routeAt >= 0)
        {
            var routeText = ValueAfter(arguments, routeAt);
            if (routeText is null || !IsPlainRoute(routeText))
                return new(null, StoreScreenshotLaunchProblem.InvalidRoute);
            route = routeText;
        }

        string? clock = null;
        var clockAt = IndexOf(arguments, ClockArgument);
        if (clockAt >= 0)
        {
            clock = ValueAfter(arguments, clockAt);
            if (clock is null || !IsClock(clock))
                return new(null, StoreScreenshotLaunchProblem.InvalidClock);
        }

        return new(new StoreScreenshotPlan(server, route, clock), StoreScreenshotLaunchProblem.None);
    }

    /// <summary>"HH:mm", 00:00 to 23:59: the text goes straight into the page, so nothing else is accepted.</summary>
    private static bool IsClock(string text) =>
        text.Length == 5 && text[2] == ':'
        && int.TryParse(text.AsSpan(0, 2), System.Globalization.NumberStyles.None, null, out var hours) && hours < 24
        && int.TryParse(text.AsSpan(3, 2), System.Globalization.NumberStyles.None, null, out var minutes) && minutes < 60;

    /// <summary>The scheme has to be written out: https to any host, plain http only to this machine
    /// (the simulator's loopback), so the mode can never send a session over plain http across a network.</summary>
    private static Uri? ParseServer(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return null;
        if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            return ServerAddress.WithTrailingSlash(uri);
        return null;
    }

    /// <summary>A path inside the app: starts with one slash, lower-case letters, digits, '-', '_' and
    /// single slashes only. Rules out other hosts ("//host"), schemes and "..". It may end in a simple query of
    /// <c>name=value</c> pairs made of the same characters (<c>/notes?note=3</c>, a link the page itself
    /// understands); nothing else can be smuggled in.</summary>
    private static bool IsPlainRoute(string route)
    {
        if (route.Length is 0 or > MaxRouteLength || route[0] != '/' || route.Contains("//", StringComparison.Ordinal))
            return false;
        var parts = route.Split('?', 2);
        if (!parts[0].All(c => IsRouteChar(c) || c == '/'))
            return false;
        return parts.Length == 1 || parts[1].Split('&').All(IsQueryPair);
    }

    private static bool IsRouteChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    private static bool IsQueryPair(string pair)
    {
        var nameValue = pair.Split('=');
        return nameValue.Length == 2 && nameValue.All(p => p.Length > 0 && p.All(IsRouteChar));
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i < arguments.Count; i++)
            if (string.Equals(arguments[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static string? ValueAfter(IReadOnlyList<string> arguments, int index)
    {
        if (index + 1 >= arguments.Count)
            return null;
        var value = arguments[index + 1].Trim();
        return value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
    }
}

/// <summary>What the app does next while it works towards a screenshot.</summary>
public enum StoreScreenshotStep
{
    /// <summary>Not there yet (signing in, loading): look again shortly.</summary>
    Wait,
    /// <summary>Signed in but on another page: open the planned route.</summary>
    Navigate,
    /// <summary>The route is shown and has stopped loading: the screen can be captured.</summary>
    Capture,
    /// <summary>The time is up without a settled page; the run reports it instead of waiting forever.</summary>
    GiveUp,
}

/// <summary>One look at the app: signed in or not, which path is shown, whether the page still shows
/// loading placeholders, and how long the run has been going.</summary>
public readonly record struct StoreScreenshotObservation(bool SignedIn, string CurrentPath, bool PageBusy, TimeSpan Elapsed);

/// <summary>
/// Decides, look by look, when the planned page is ready for its screenshot. Signing in on a demo server
/// reloads the app once and lands on the dashboard, so the route is opened only once a session exists and
/// is opened again if a reload took the app elsewhere. "Ready" means: on the route, signed in, and no
/// loading placeholder for <see cref="QuietLooksNeeded"/> looks in a row (data that arrives late would
/// otherwise be captured half-drawn).
/// </summary>
public sealed class StoreScreenshotReadiness
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    public const int DefaultQuietLooksNeeded = 3;

    /// <summary>Looks to wait after opening the route before opening it again (the navigation is in flight).</summary>
    private const int LooksBetweenNavigations = 4;

    private readonly string _route;
    private readonly TimeSpan _timeout;
    private int _quietLooks;
    private int _looksSinceNavigation = LooksBetweenNavigations;
    private bool _captured;

    public StoreScreenshotReadiness(string route, int quietLooksNeeded = DefaultQuietLooksNeeded, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quietLooksNeeded, 1);
        _route = Normalise(route);
        QuietLooksNeeded = quietLooksNeeded;
        _timeout = timeout ?? DefaultTimeout;
    }

    public int QuietLooksNeeded { get; }

    public StoreScreenshotStep Next(StoreScreenshotObservation look)
    {
        if (_captured)
            return StoreScreenshotStep.Capture;
        if (look.Elapsed >= _timeout)
            return StoreScreenshotStep.GiveUp;

        _looksSinceNavigation++;
        if (!look.SignedIn)
        {
            _quietLooks = 0;
            return StoreScreenshotStep.Wait;
        }

        if (!string.Equals(Normalise(look.CurrentPath), _route, StringComparison.OrdinalIgnoreCase))
        {
            _quietLooks = 0;
            if (_looksSinceNavigation < LooksBetweenNavigations)
                return StoreScreenshotStep.Wait;
            _looksSinceNavigation = 0;
            return StoreScreenshotStep.Navigate;
        }

        if (look.PageBusy)
        {
            _quietLooks = 0;
            return StoreScreenshotStep.Wait;
        }

        if (++_quietLooks < QuietLooksNeeded)
            return StoreScreenshotStep.Wait;
        _captured = true;
        return StoreScreenshotStep.Capture;
    }

    /// <summary>"/calendar/" and "/calendar" are the same page, so are "/notes?note=3" and "/notes" (the query
    /// is for the page, the address check only compares paths); the root stays "/".</summary>
    private static string Normalise(string path)
    {
        var trimmed = (path ?? "").Split('?', 2)[0].Trim().TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
