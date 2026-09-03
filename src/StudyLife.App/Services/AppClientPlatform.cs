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
}
