using StudyLife.App.Core;

namespace StudyLife.App.Services;

/// <summary>
/// The copies this app keeps outside the web view because they belong to the device, not to a session:
/// the home screen widget snapshot (with the Watch snapshot and the Live Activity that hang off it), the
/// Spotlight index, the queued native telemetry and the consent to it (the next sign-in asks the client to
/// apply the user's decision again), the files handed to the share sheet (data export, recovery codes in
/// the cache), text shared into the app but not yet turned into a note, the HTTP response cache and the
/// notifications already delivered (they carry course names and topics). The one place that removes them,
/// used on every sign-out (SignOutLocalCopies, wired in AppRoot), when the account is deleted and when the
/// user leaves the server. Every step runs on its own: one that fails does not keep the others from running.
/// </summary>
public static class NativeLocalCopies
{
    public static async Task WipeAsync()
    {
        await RunAsync(HomeWidgetSnapshot.ClearAsync);
        await RunAsync(SpotlightIndexer.ClearAsync);
        await RunAsync(NativeTelemetry.RevokeAsync);
        await RunAsync(() => Run(() => ExportFiles.Clear(Microsoft.Maui.Storage.FileSystem.CacheDirectory)));
        await RunAsync(() => Run(SharedNoteIntake.Discard));
        await RunAsync(() => Run(PlatformCaches.Clear));
    }

    private static async Task RunAsync(Func<Task> step)
    {
        try { await step(); }
        catch { /* best effort: a copy that cannot be removed now must not block the sign-out */ }
    }

    private static Task Run(Action step)
    {
        step();
        return Task.CompletedTask;
    }
}