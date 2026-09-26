using Foundation;
using ObjCRuntime;
using WebKit;

namespace StudyLife.App;

/// <summary>
/// WKWebView denies every getUserMedia() request by default unless the host app supplies a
/// WKUIDelegate implementing the iOS 15+ media-capture-permission callback - needed for Notes'
/// voice dictation (interop.js: startDictationRecording). MAUI's own BlazorWebView already
/// installs its own WKUIDelegate (Microsoft.AspNetCore.Components.WebView.Maui's internal
/// WebViewUIDelegate, handling JS alert/confirm/prompt) - replacing it outright would silently
/// break those. Instead this wraps it: only RequestMediaCapturePermission is handled here,
/// everything else is forwarded to the previously-installed delegate via the Objective-C
/// message-forwarding protocol (RespondsToSelector/ForwardingTargetForSelector), so MAUI's own
/// dialog handling keeps working unchanged.
/// </summary>
internal sealed class MicPermissionUIDelegate(IWKUIDelegate? inner) : WKUIDelegate
{
    // WKWebView.UIDelegate is typed as the IWKUIDelegate protocol interface, but the forwarding
    // machinery below (RespondsToSelector/ForwardingTargetForSelector) is plain NSObject/ObjC
    // runtime plumbing - every real WKUIDelegate implementation is backed by an NSObject, so this
    // cast is always valid for an actual delegate instance (never for, say, a mock in a unit test,
    // but nothing here runs outside a real WKWebView).
    private readonly NSObject? _inner = inner as NSObject;

    public override void RequestMediaCapturePermission(WKWebView webView, WKSecurityOrigin origin, WKFrameInfo frame,
        WKMediaCaptureType type, Action<WKPermissionDecision> decisionHandler) =>
        decisionHandler(WKPermissionDecision.Grant);

    public override bool RespondsToSelector(Selector? sel) =>
        base.RespondsToSelector(sel) || (_inner?.RespondsToSelector(sel) ?? false);

    [Export("forwardingTargetForSelector:")]
    public NSObject? ForwardingTargetForSelector(Selector sel) =>
        _inner != null && _inner.RespondsToSelector(sel) ? _inner : null;

    /// <summary>
    /// Installs the wrapper on <paramref name="webView"/>, capturing whatever delegate is
    /// currently set as the forwarding target. Called from MauiProgram.cs's "StudyLifeSafeArea"
    /// handler mapping (runs once the platform WKWebView exists) AND polled for a few seconds
    /// afterward, because Microsoft.AspNetCore.Components.WebView.Maui's own WebViewUIDelegate
    /// assignment can happen slightly LATER than that mapping callback - a one-shot install here
    /// risks being silently overwritten moments later otherwise.
    /// </summary>
    public static void Install(WKWebView webView)
    {
        if (webView.UIDelegate is MicPermissionUIDelegate) return;
        webView.UIDelegate = new MicPermissionUIDelegate(webView.UIDelegate);

        var attempts = 0;
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            attempts++;
            if (webView.UIDelegate is not MicPermissionUIDelegate)
                webView.UIDelegate = new MicPermissionUIDelegate(webView.UIDelegate);
            if (attempts >= 20) timer?.Dispose(); // ~5s at 250ms - well past MAUI's own setup.
        }, null, 250, 250);
    }
}
