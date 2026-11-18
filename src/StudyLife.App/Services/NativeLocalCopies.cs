namespace StudyLife.App.Services;

/// <summary>
/// The copies this app keeps outside the web view because they belong to the device, not to a session:
/// the home screen widget snapshot (with the Watch snapshot and the Live Activity that hang off it) and
/// the Spotlight index, plus the queued native telemetry and the consent to it (the next sign-in asks the
/// client to apply the user's decision again). The one place that removes them, used on every sign-out (SignOutLocalCopies, wired
/// in AppRoot), when the account is deleted and when the user leaves the server.
/// </summary>
public static class NativeLocalCopies
{
    public static async Task WipeAsync()
    {
        await HomeWidgetSnapshot.ClearAsync();
        await SpotlightIndexer.ClearAsync();
        await NativeTelemetry.RevokeAsync();
    }
}
