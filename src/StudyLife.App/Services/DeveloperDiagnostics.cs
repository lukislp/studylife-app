using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// The one place that decides what happens to a developer diagnostic. Everything the app used
/// to put in front of the user for debugging (the Live Activity banner and notification, the
/// "Print diagnostics" notifications, the JS error banner with stack traces) goes through
/// here, and the same rule applies to all of it:
///
/// <list type="bullet">
/// <item>Always: one line in the platform log (visible with
/// <c>xcrun devicectl device process launch --console</c> or Console.app).</item>
/// <item>Always: an <c>error</c> event in the local telemetry queue (<see cref="NativeTelemetry"/>),
/// which TelemetryService sends to the user's own server and only after consent. Only a code,
/// the exception type and a sanitized stack go there - never the message, which may contain
/// user content.</item>
/// <item>Only in DEBUG builds, or in a build made with <c>-p:DeveloperDiagnostics=true</c>
/// (defines DEVELOPER_DIAGNOSTICS): the red banner in the web view and a system notification.
/// A release build as distributed shows nothing, ever (App Review rejects debug UI).</item>
/// </list>
///
/// "Not supported here" (no Live Activities on Mac Catalyst, iPad, iOS 15, or switched off by
/// the user) is a normal state and is not reported at all.
/// </summary>
public static class DeveloperDiagnostics
{
    /// <summary>True only in DEBUG or DEVELOPER_DIAGNOSTICS builds.</summary>
    public static bool ShowToDeveloper { get; } =
#if DEBUG || DEVELOPER_DIAGNOSTICS
        true;
#else
        false;
#endif

    /// <summary>Per event code and app run: more than this are dropped, so a JS error in a
    /// render loop cannot flood the queue (which is also written to disk on every event).</summary>
    internal const int MaxEventsPerCode = 5;

    private static readonly Dictionary<string, int> Counts = new();
    private static readonly Regex QueryString = new(@"\?[^\s:)]*", RegexOptions.Compiled);

    /// <summary>Developer-only sink for the banner (AppRoot wires it to the web view).
    /// Never set in a release build.</summary>
    public static Action<string>? DeveloperBanner { get; set; }

    /// <summary>Writes one line to the platform log, flushed - the output of a release build
    /// is otherwise lost when the process is killed.</summary>
    internal static void Log(string category, string text)
    {
        Console.WriteLine($"[Diag:{category}] {text}");
        Console.Out.Flush();
    }

    /// <summary>Reports an unexpected native-side failure. <paramref name="code"/> is a short
    /// stable identifier (e.g. "print_failed"), <paramref name="message"/> is for the log and
    /// the developer banner only.</summary>
    public static void Error(string source, string code, string message, Exception? exception = null)
    {
        Log(source, $"{code}: {message}");
        if (exception != null) Log(source, exception.ToString());
        Record("dotnet", exception == null ? code : $"{code}:{exception.GetType().Name}", exception?.StackTrace, code);
        ShowToDeveloperIfEnabled(source, message);
    }

    /// <summary>Reports an unexpected error from the web view (window error /
    /// unhandledrejection). Returns true when a developer banner showed it - the caller then
    /// leaves the page alone; false means the caller shows the app's normal error UI.</summary>
    public static bool ReportJsError(string errorType, string message, string? stack)
    {
        var type = string.IsNullOrWhiteSpace(errorType) ? "Error" : errorType;
        Log("js", $"{type}: {message}");
        if (!string.IsNullOrEmpty(stack)) Log("js", stack);
        Record("js", type, stack, $"js:{type}");
        return ShowToDeveloperIfEnabled("js", $"{type}: {message}" + (string.IsNullOrEmpty(stack) ? "" : "\n" + stack));
    }

    private static bool ShowToDeveloperIfEnabled(string source, string message)
    {
        if (!ShowToDeveloper) return false;
        var banner = DeveloperBanner;
        banner?.Invoke($"{source}: {message}");
        _ = NativeBridge.ShowNotificationAsync($"{source} diagnostics", message);
        return banner != null;
    }

    private static void Record(string kind, string errorType, string? rawStack, string countKey)
    {
        lock (Counts)
        {
            Counts.TryGetValue(countKey, out var seen);
            if (seen >= MaxEventsPerCode) return;
            Counts[countKey] = seen + 1;
        }
        try { NativeTelemetry.Enqueue(BuildEvent(kind, errorType, rawStack)); }
        catch { /* diagnostics must never take the app down */ }
    }

    /// <summary>The telemetry contract's `error` event (studylife repo, TelemetryDtos.cs):
    /// kind is one of the server's allowed values, the error type is capped at 120 characters.</summary>
    internal static TelemetryEventDto BuildEvent(string kind, string errorType, string? rawStack)
    {
        var (stack, stackHash) = SanitizeStack(rawStack);
        return new TelemetryEventDto
        {
            Type = "error",
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = kind,
            ErrorType = errorType.Length > 120 ? errorType[..120] : errorType,
            Stack = stack,
            StackHash = stackHash,
            Fatal = false,
        };
    }

    /// <summary>Same rule as the web client's TelemetryService.SanitizeAndHashStack: keep frame
    /// lines only (the first line of a stack is "Type: message"), strip query strings, cap at
    /// 4096 characters.</summary>
    internal static (string Stack, string Hash) SanitizeStack(string? rawStack)
    {
        var sanitized = "";
        if (!string.IsNullOrWhiteSpace(rawStack))
        {
            var lines = rawStack.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Contains(':') &&
                            (l.TrimStart().StartsWith("at ", StringComparison.Ordinal) || l.Contains(".cs:") || l.Contains(".js:")))
                .Select(l => QueryString.Replace(l, ""));
            sanitized = string.Join('\n', lines);
            if (sanitized.Length > 4096) sanitized = sanitized[..4096];
        }
        return (sanitized, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized))));
    }
}
