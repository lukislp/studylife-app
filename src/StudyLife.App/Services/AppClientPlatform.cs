using StudyLife.Client.Services;

namespace StudyLife.App.Services;

/// <summary>
/// IClientPlatform implementation (telemetry phase 2, studylife repo): tells TelemetryService
/// which OS this native app shell is actually running on, straight from MAUI's own DeviceInfo -
/// same if/else-over-DeviceInfo.Platform style as NativeAppAuth/NativePush (DevicePlatform isn't
/// an enum, so it can't be used in a switch/case pattern).
/// </summary>
public sealed class AppClientPlatform : IClientPlatform
{
    public string Name
    {
        get
        {
            if (DeviceInfo.Platform == DevicePlatform.iOS) return "ios";
            if (DeviceInfo.Platform == DevicePlatform.Android) return "android";
            if (DeviceInfo.Platform == DevicePlatform.MacCatalyst) return "maccatalyst";
            if (DeviceInfo.Platform == DevicePlatform.WinUI) return "windows";
            return "web"; // shouldn't happen in a native build, but keeps the contract's enum valid
        }
    }

    /// <summary>
    /// Display version of this app build (ApplicationDisplayVersion: the release scripts pass the
    /// release number). The About section in Setup shows it and links the source at its tag,
    /// because the compiled-in client has no release stamp of its own.
    /// </summary>
    public string? AppVersion => AppInfo.Current.VersionString;

    /// <summary>
    /// The THIRD-PARTY-NOTICES.md that ships inside this app bundle (MauiAsset, see the csproj and
    /// scripts/licences/check.py), so the About section shows the notices of the binary on the
    /// device. Null when the file is not in the bundle: the client then falls back to the notices
    /// the server publishes. No new interop and no new DI registration: the client reads this
    /// through IClientPlatform, which MauiProgram already registers.
    /// </summary>
    /// <summary>The app's language is the system's language setting for the app (SystemAppLanguage): the
    /// shared UI shows a hint that leads there instead of its language picker.</summary>
    public bool LanguageFromSystem => true;

    public Task OpenLanguageSettingsAsync() => SystemAppLanguage.OpenSettingsAsync();

    public async Task<string?> ReadBundledNoticesAsync()
    {
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync("THIRD-PARTY-NOTICES.md");
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
