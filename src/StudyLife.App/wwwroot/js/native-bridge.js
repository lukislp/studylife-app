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

// Store screenshot mode (StoreScreenshotSession, simulator builds made for it only): how many loading
// placeholders the page still shows - the shared client's skeletons (.sl-skeleton), dashboard cards that
// are still loading (.is-loading) and anything marked aria-busy. Read-only; never called in any other build.
function storeScreenshotBusyCount() {
    return document.querySelectorAll('.sl-skeleton, .is-loading, [aria-busy="true"]').length;
}

// Store screenshot mode only: hides the "DEMO" marker the shared client puts next to the brand name in the
// sidebar when the server runs in demo mode (MainLayout.razor, a span without a class after .brand-name), so
// the store images show the app as a normal user sees it. The marker appears whenever the client has heard
// back from the server, possibly after the page counts as ready, so the first call also installs an observer
// that hides it each time it is rendered. Inline style property, not a <style> element: the page's CSP allows
// style attributes only. Never called in any other build.
function storeScreenshotHideDemoBadge() {
    var hide = function () {
        document.querySelectorAll('.sidebar .brand > span:not(.brand-icon):not(.brand-name)').forEach(function (el) {
            if (el.style.display !== 'none') el.style.display = 'none';
        });
    };
    hide();
    if (!window.__storeScreenshotBadgeObserver) {
        window.__storeScreenshotBadgeObserver = new MutationObserver(hide);
        window.__storeScreenshotBadgeObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
    }
    return true;
}

// Store screenshot mode only: the sidebar clock (SidebarClock.razor, .sidebar .time-display) shows the given
// time - the simulator's status bar override - instead of the real time of the run, so both clocks in a
// screenshot agree. The clock's own text node is changed, not replaced, so Blazor keeps its node; when its
// minute timer writes the real time back, the observer sets the pinned time again. The date line is today's
// date already and stays untouched. Never called in any other build.
function storeScreenshotPinClock(text) {
    var pin = function () {
        document.querySelectorAll('.sidebar .time-display').forEach(function (el) {
            var node = el.firstChild;
            if (node && node.nodeType === Node.TEXT_NODE) {
                if (node.nodeValue !== text) node.nodeValue = text;
            } else if (el.textContent !== text) {
                el.textContent = text;
            }
        });
    };
    pin();
    if (!window.__storeScreenshotClockObserver) {
        window.__storeScreenshotClockObserver = new MutationObserver(pin);
        window.__storeScreenshotClockObserver.observe(document.body, { childList: true, subtree: true, characterData: true });
    }
    return true;
}
