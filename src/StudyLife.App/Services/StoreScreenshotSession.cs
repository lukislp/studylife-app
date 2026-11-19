using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using StudyLife.App.Core;
using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// Platform glue of the store screenshot mode (.github/workflows/store-screenshots.yml, README "App Store
/// screenshots"). The decisions live in StudyLife.App.Core: <see cref="StoreScreenshotLaunch"/> reads the
/// launch arguments, <see cref="StoreScreenshotReadiness"/> says when the page is ready. This class only
/// supplies the facts and carries out the steps.
///
/// Two locks keep it out of every distributed build: the code that reads the arguments is only compiled
/// with <c>-p:StoreScreenshots=true</c> (define STORE_SCREENSHOTS, set by the workflow and nowhere else), and
/// even then it only acts in the iOS Simulator. Any other build has no plan and every member is a no-op.
///
/// The sign-in itself is the shared client's: a server in demo mode signs the login page in as the demo
/// user, so the mode never handles a credential.
/// </summary>
public static class StoreScreenshotSession
{
    /// <summary>The file the workflow waits for: "ready &lt;route&gt;" or "timeout &lt;path&gt;".</summary>
    public const string StatusFileName = "store-screenshot-status.txt";

    /// <summary>The widget/Watch snapshot of the captured screen (HomeWidgetSnapshot.BuildAsync), written next to the
    /// status file: the simulator build has no App Group (no provisioning profile), so the workflow copies this file
    /// into the Watch simulator's container itself to capture the Watch app with the same demo data.</summary>
    public const string WatchSnapshotFileName = "store-screenshot-watch-snapshot.json";

    private static readonly TimeSpan LookInterval = TimeSpan.FromMilliseconds(500);

    public static StoreScreenshotPlan? Plan { get; } = ReadPlan();

    /// <summary>The app's clock (registered as TimeProvider in MauiProgram): pinned to the plan's time, so every
    /// "now" of the shared client agrees with the status bar override; the system clock in every other start.</summary>
    public static TimeProvider Clock { get; } = Plan?.CreateClock(TimeProvider.System) ?? TimeProvider.System;

    /// <summary>Points the server address at the plan's server before the first HttpClient exists.</summary>
    public static ServerUrlStore ApplyServer(ServerUrlStore store)
    {
        if (Plan is { } plan)
            store.Save(plan.Server);
        return store;
    }

    /// <summary>Looks at the app every half second until the planned page is ready, opens the route when the
    /// readiness rule says so, then writes the status file. Runs on the renderer's dispatcher (AppRoot starts it
    /// through InvokeAsync), so NavigationManager may be used directly. Ends early when AppRoot goes away (the
    /// reload after the demo sign-in); the new AppRoot starts it again.</summary>
    public static async Task RunAsync(Func<bool> signedIn, NavigationManager nav, IJSRuntime js, AppStateService state,
        CancellationToken ct)
    {
        if (Plan is not { } plan)
            return;
        var readiness = new StoreScreenshotReadiness(plan.Route);
        var clock = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await HideDemoMarkerAsync(js);
                var path = new Uri(nav.Uri).AbsolutePath;
                var look = new StoreScreenshotObservation(signedIn(), path, await IsPageBusyAsync(js), clock.Elapsed);
                switch (readiness.Next(look))
                {
                    case StoreScreenshotStep.Navigate:
                        nav.NavigateTo(plan.Route);
                        break;
                    case StoreScreenshotStep.Capture:
                        await WriteWatchSnapshotAsync(state);
                        WriteStatus($"ready {plan.Route}");
                        return;
                    case StoreScreenshotStep.GiveUp:
                        WriteStatus($"timeout {path}");
                        return;
                }
                await Task.Delay(LookInterval, ct);
            }
        }
        catch (OperationCanceledException) { /* AppRoot disposed: the next instance takes over */ }
    }

    /// <summary>The server runs in demo mode, so the shared client marks the sidebar "DEMO"; a store user never
    /// sees that marker, so the screenshots do not either. The script keeps hiding it from then on (the marker can
    /// appear after the page counts as ready); calling it on every look also covers a reload of the page.</summary>
    private static async Task HideDemoMarkerAsync(IJSRuntime js)
    {
        try { await js.InvokeAsync<bool>("storeScreenshotHideDemoBadge"); }
        catch { /* page not up yet */ }
    }

    /// <summary>Best effort: a Watch screenshot without it shows the Watch app's "open the app on your iPhone" state,
    /// which the workflow reports.</summary>
    private static async Task WriteWatchSnapshotAsync(AppStateService state)
    {
        try
        {
            var snapshot = await HomeWidgetSnapshot.BuildAsync(state, Clock);
            await File.WriteAllBytesAsync(Path.Combine(FileSystem.AppDataDirectory, WatchSnapshotFileName), snapshot.Json);
        }
        catch (Exception e) { DeveloperDiagnostics.Log("screenshots", $"watch snapshot not written: {e.GetType().Name}"); }
    }

    private static async Task<bool> IsPageBusyAsync(IJSRuntime js)
    {
        try { return await js.InvokeAsync<int>("storeScreenshotBusyCount") > 0; }
        catch { return true; /* page not up yet */ }
    }

    private static void WriteStatus(string status)
    {
        DeveloperDiagnostics.Log("screenshots", status);
        File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, StatusFileName), status);
    }

    private static StoreScreenshotPlan? ReadPlan()
    {
#if STORE_SCREENSHOTS && IOS
        var allowed = ObjCRuntime.Runtime.Arch == ObjCRuntime.Arch.SIMULATOR;
        var arguments = Foundation.NSProcessInfo.ProcessInfo.Arguments;
        var result = StoreScreenshotLaunch.Decide(arguments, allowed);
        if (result.Problem is not (StoreScreenshotLaunchProblem.None or StoreScreenshotLaunchProblem.NotRequested))
            DeveloperDiagnostics.Log("screenshots", $"launch arguments not used: {result.Problem}");
        return result.Plan;
#else
        return null;
#endif
    }
}
