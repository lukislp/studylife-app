namespace StudyLife.App.Core;

/// <summary>
/// What the Apple store builds leave out of the shared client so that the app reads as what it is, an
/// app for the user's own server, and not as a store for add-ons (App Review 2.5.2, 3.1.1, 4.7).
/// Decided by the platform name of IClientPlatform ("ios", "maccatalyst", "android", "windows"); the
/// shared client only ever asks the platform abstraction, never the name.
/// </summary>
public static class AppStorePolicy
{
    /// <summary>
    /// Whether Setup shows the add-on registration and the marketplace browser, which reads a remote
    /// catalogue. False on iOS and Mac Catalyst: the catalogue is remote content that can change
    /// after review. The browser, Android and Windows keep the cards.
    /// </summary>
    public static bool ShowsDeveloperIntegrations(string platformName) =>
        platformName is not ("ios" or "maccatalyst");
}
