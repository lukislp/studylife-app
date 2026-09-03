using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Runtime;
using StudyLife.App.Services;
using StudyLife.Shared;

namespace StudyLife.App;

[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
        // Catches managed crashes (AppDomain) and crashes that cross into the JVM via
        // Android.Runtime.AndroidEnvironment - both fire before the process actually dies.
        // Written synchronously to the persisted telemetry queue (NativeTelemetry.Enqueue writes
        // through to disk on every call) so the NEXT launch's first flush picks it up
        // (TelemetryService.DrainAsync) - the same "survives a relaunch" contract as the iOS
        // MetricKit crash path (also only delivered at the next launch, just via the OS itself
        // instead of a handler here). There is no ANR watchdog: Android's own ANR detection
        // doesn't hand the app a callback before killing/dialoguing it, and there is no existing
        // trivial hook in this repo to build one on top of - left out of scope deliberately
        // rather than adding a bespoke main-thread heartbeat thread for this alone.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RecordCrash(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            RecordCrash(e.Exception, fatal: true);
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void RecordCrash(Exception? ex, bool fatal)
    {
        if (ex == null) return;
        try
        {
            var (stack, stackHash) = SanitizeStack(ex);
            NativeTelemetry.Enqueue(new TelemetryEventDto
            {
                Type = "error",
                Kind = "native_crash",
                ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
                Stack = stack,
                StackHash = stackHash,
                Fatal = fatal,
            });
        }
        catch { /* must never throw from a crash handler - worst case this one report is lost */ }
    }

    // Same sanitization rule as TelemetryService.SanitizeAndHashStack (studylife repo, web/JS
    // paths): the exception TYPE already travels as its own field above, so only frame lines are
    // kept here, never the exception message (may contain user content/PII).
    private static (string Stack, string StackHash) SanitizeStack(Exception ex)
    {
        var raw = ex.StackTrace ?? "";
        if (raw.Length > 4096) raw = raw[..4096];
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return (raw, hash);
    }
}
