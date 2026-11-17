namespace StudyLife.App.Core;

/// <summary>
/// Who may use the microphone inside the app's web view. Only the app's own page, in its top frame, gets
/// it (Notes' voice dictation): an iframe or any other origin the web view may show (an embedded page, a
/// link that navigated inside the view) is denied without asking. Pure, so it is unit-tested; the
/// Apple-specific WKUIDelegate only passes the request's origin and frame in.
/// </summary>
public static class MediaCapturePolicy
{
    /// <summary>The origin BlazorWebView serves the app from on iOS and Mac Catalyst (app://0.0.0.1).</summary>
    public const string AppleAppScheme = "app";
    public const string AppHost = "0.0.0.1";

    public static bool MayCapture(string? scheme, string? host, bool isMainFrame) =>
        isMainFrame
        && string.Equals(scheme, AppleAppScheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(host, AppHost, StringComparison.OrdinalIgnoreCase);
}
