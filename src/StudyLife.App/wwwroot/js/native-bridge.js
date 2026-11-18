// ============================================================================
// Native bridge: replaces the functions whose web APIs don't exist (reliably)
// in native WebViews with calls into the MAUI world (NativeBridge.cs).
// The function names and signatures stay identical - the client services
// don't notice any difference. Must load after js/native-interop.js, whose
// functions it overrides. A file, not an inline <script>, for the same CSP
// reason as js/native-interop.js.
// ============================================================================
(function () {
    const invokeNative = (method, ...args) =>
        DotNet.invokeMethodAsync('StudyLife.App', method, ...args).catch(() => null);

    // Wake Lock -> DeviceDisplay.KeepScreenOn (navigator.wakeLock is missing in WKWebView)
    requestWakeLock = async function () {
        return (await invokeNative('NativeKeepScreenOn', true)) === true;
    };
    releaseWakeLock = async function () {
        await invokeNative('NativeKeepScreenOn', false);
    };

    // Notifications -> UNUserNotificationCenter / NotificationManager
    requestNotificationPermission = function () {
        return invokeNative('NativeRequestNotificationPermission');
    };
    getNotificationPermissionStatus = function () {
        return invokeNative('NativeGetNotificationPermissionStatus');
    };
    showLocalNotification = function (title, body) {
        invokeNative('NativeShowNotification', title, body);
    };
    showSessionNotification = function (title, body) {
        invokeNative('NativeShowNotification', title, body);
    };
    sendTestNotification = function (title, body) {
        invokeNative('NativeShowNotification', title, body);
    };

    // App icon badge -> native (iOS/Mac; Android/Windows: no-op in NativeBridge)
    setAppBadge = function (count) {
        invokeNative('NativeSetAppBadge', count || 0);
    };

    // Printing: try the native bridge FIRST (NativePrint - UIPrintInteractionController
    // on iOS, PrintManager on Android; a plain Task.FromResult(false) no-op on Windows,
    // where NativeBridge doesn't implement it). window.print() is only the fallback -
    // WKWebView on iOS was observed to define window.print as a silent no-op (returns
    // without throwing, no dialog), so checking it FIRST would wrongly "succeed" and
    // never reach the native dialog at all.
    printPage = function () {
        return invokeNative('NativePrint').then(function (r) {
            if (r === true) return true;
            try {
                if (typeof window.print === 'function') {
                    window.print();
                    return true;
                }
            } catch (e) { /* printing unsupported here either */ }
            return false;
        });
    };

    // Hardware arrow keys (iPad): a page that wants the bare Left/Right keys declares it with
    // data-page-arrow-keys on <html> (the calendar does). The app claims the keys natively only
    // while it is set (StudyLife.App.Core.HardwareArrowKeys), so every change is reported.
    const reportArrowIntent = () =>
        invokeNative('NativeSetPageArrowKeys', document.documentElement.getAttribute('data-page-arrow-keys'));
    new MutationObserver(reportArrowIntent)
        .observe(document.documentElement, { attributes: true, attributeFilter: ['data-page-arrow-keys'] });

    // Completion feedback: WebAudio stays, navigator.vibrate doesn't exist in the WebView
    // -> additionally native vibration/haptics.
    const playCompletionSoundWeb = playCompletionSound;
    playCompletionSound = function () {
        playCompletionSoundWeb();
        invokeNative('NativeVibrate');
    };
})();

// Developer banner - only ever called from DeveloperDiagnostics in DEBUG /
// DEVELOPER_DIAGNOSTICS builds (AppRoot wires it up only there). window.alert is a
// silent no-op in WKWebView, hence a direct DOM display. Entries are appended;
// tapping the banner removes it.
function showDiagnosticsBanner(text) {
    var el = document.getElementById('dev-diag');
    if (!el) {
        el = document.createElement('div');
        el.id = 'dev-diag';
        el.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:99999;' +
            'background:#7a1f1f;color:#fff;font:12px/1.4 monospace;padding:10px 12px;' +
            'max-height:40vh;overflow:auto;white-space:pre-wrap;word-break:break-all;';
        el.addEventListener('click', function () { el.remove(); });
        document.body.appendChild(el);
    }
    el.textContent += (el.textContent ? '\n---\n' : 'Developer diagnostics (tap to close):\n') + text;
}

// A claimed hardware arrow key (StudyAtriumApplication.SendEvent -> NativeBridge.ForwardArrowKey), handed
// to the page as the keydown/keyup it never received. Dispatched on the focused element so the
// page's own listeners see it exactly as a real key press.
function nativeHorizontalArrowKey(key) {
    var target = document.activeElement || document.body;
    var init = { key: key, code: key, bubbles: true, cancelable: true, composed: true };
    target.dispatchEvent(new KeyboardEvent('keydown', init));
    target.dispatchEvent(new KeyboardEvent('keyup', init));
}
