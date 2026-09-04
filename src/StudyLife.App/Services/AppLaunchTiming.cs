using System.Diagnostics;
using StudyLife.Shared;

namespace StudyLife.App.Services;

/// <summary>
/// App-launch timing for the app_launch telemetry event's webviewReadyMs field: a Stopwatch
/// started as early as possible in MauiProgram.CreateMauiApp and stopped once AppRoot has
/// rendered its first component (Components/AppRoot.razor's OnAfterRender(firstRender: true)) -
/// the earliest point at which the BlazorWebView has actually painted something.
///
/// coldMs/warmMs are deliberately NEVER set here: MetricKit (TelemetryBridge.swift) is the only
/// source with real "app process created" timing on iOS - a Stopwatch inside managed code
/// starts too late (after the .NET runtime and MAUI host are already up) to stand in for it, and
/// faking it would defeat the whole point of measuring cold start.
/// </summary>
public static class AppLaunchTiming
{
    private static readonly Stopwatch Stopwatch = new();
    private static bool _reported;

    /// <summary>Call first thing in MauiProgram.CreateMauiApp.</summary>
    public static void Start() => Stopwatch.Restart();

    /// <summary>Milliseconds from process creation to the call, or null when the OS does not
    /// expose the process start time. Covers the native part of a cold start (dyld, Mono runtime,
    /// assembly loading) that no managed Stopwatch can see - on the 2026-09-04 screen recording
    /// that part was ~0.5 s of dark screen the Stopwatch below reported as 200 ms.</summary>
    private static double? SinceProcessStartMs()
    {
        try
        {
            return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        }
        catch { return null; }
    }

    /// <summary>Call once, from AppRoot's first OnAfterRender - later calls are no-ops (a
    /// second/third render, e.g. after the first-run server-URL dialog, isn't a second launch).</summary>
    public static void MarkWebviewReady()
    {
        if (_reported) return;
        _reported = true;
        NativeTelemetry.Enqueue(new TelemetryEventDto
        {
            Type = "app_launch",
            // From process start when available (the honest cold-start number), else from CreateMauiApp.
            WebviewReadyMs = SinceProcessStartMs() ?? Stopwatch.Elapsed.TotalMilliseconds,
        });
    }
}
