namespace StudyLife.App.Core;

/// <summary>What a store screenshot run asks the app to do: sign in on this server (with the demo
/// account its demo mode provides) and show this route.</summary>
public sealed record StoreScreenshotPlan(Uri Server, string Route);

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
}

public readonly record struct StoreScreenshotLaunchResult(StoreScreenshotPlan? Plan, StoreScreenshotLaunchProblem Problem);

/// <summary>
/// Reads the launch arguments of the store screenshot workflow
/// (<c>--store-screenshots &lt;server&gt; [--route /calendar]</c>). The platform says whether this build may
/// act on them at all (<paramref name="allowedInThisBuild"/> of <see cref="Decide"/>): only a simulator build
/// compiled for screenshots does, so a store build ignores the arguments whatever they say. Pure: no device,
/// no network.
/// </summary>
public static class StoreScreenshotLaunch
{
    public const string ServerArgument = "--store-screenshots";
    public const string RouteArgument = "--route";

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

        return new(new StoreScreenshotPlan(server, route), StoreScreenshotLaunchProblem.None);
    }

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
    /// single slashes only. Rules out other hosts ("//host"), schemes, queries and "..".</summary>
    private static bool IsPlainRoute(string route)
    {
        if (route.Length is 0 or > MaxRouteLength || route[0] != '/' || route.Contains("//", StringComparison.Ordinal))
            return false;
        return route.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '/');
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

    /// <summary>"/calendar/" and "/calendar" are the same page; the root stays "/".</summary>
    private static string Normalise(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }
}
