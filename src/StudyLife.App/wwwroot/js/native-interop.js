// ============================================================================
// 1:1 copy of the interop functions from StudyLife.Client/wwwroot/js/interop.js -
// the client services call exactly these function names (scripts/check-native-parity.sh
// keeps the two in step). The omissions are the web-only functions listed in that
// script's ALLOWLIST (service worker, cross-tab locks, boot telemetry). js/native-bridge.js,
// loaded right after this file, then overrides the functions whose browser APIs don't
// exist in native WebViews (Wake Lock, Notifications, Badge, Print, Vibration).
//
// A file, not an inline <script>: index.html's Content-Security-Policy allows scripts
// from the app bundle only ('self'), never inline script (audit 2026-11-16, finding H1).
// ============================================================================

// Unexpected JS errors (window error / unhandledrejection) are handed to the native
// side (NativeBridge.ReportJsError -> DeveloperDiagnostics), which logs them and queues
// a sanitized telemetry event. It answers true only in a DEBUG/DEVELOPER_DIAGNOSTICS
// build, where it shows the developer banner (showDiagnosticsBanner, in
// js/native-bridge.js) instead; in every other build the page shows the app's normal error UI (the
// #blazor-error-ui bar, no stack trace, no message). This replaces the former red
// banner, which every user saw.
(function () {
    function showErrorUi() {
        var ui = document.getElementById('blazor-error-ui');
        if (ui) ui.style.display = 'block';
    }
    function report(errorType, message, stack) {
        try {
            DotNet.invokeMethodAsync('StudyLife.App', 'NativeReportJsError',
                String(errorType || 'Error'), String(message || ''), stack ? String(stack) : null)
                .then(function (shownToDeveloper) { if (!shownToDeveloper) showErrorUi(); },
                    showErrorUi);
        } catch (ex) {
            showErrorUi(); // bridge not up yet - still no raw details on screen
        }
    }
    window.addEventListener('error', function (e) {
        // Benign browser notification, not a failure: nothing to show or report.
        if (e.message && e.message.indexOf('ResizeObserver loop') === 0) return;
        report(e.error && e.error.name, e.message || e,
            (e.error && e.error.stack) || ((e.filename || '') + ':' + (e.lineno || '')));
    });
    window.addEventListener('unhandledrejection', function (e) {
        var reason = e.reason;
        report(reason && reason.name || 'UnhandledRejection',
            reason && reason.message ? reason.message : reason,
            reason && reason.stack);
    });
})();

function scrollElementToTop(id) {
    var el = document.getElementById(id);
    if (el) el.scrollTop = 0;
}

// 1:1 copy from StudyLife.Client/wwwroot/js/interop.js: keys scroll the focused element's
// nearest scroll container and html/body are clipped, so after navigation focus moves
// into the scroll region (tabindex=-1, no Tab stop) unless something meaningful holds it.
function focusScrollRegion(id) {
    var el = document.getElementById(id);
    if (!el) return;
    var a = document.activeElement;
    var idle = !a || a === document.body || a === document.documentElement ||
        (a.id === id) || a.id === 'main-scroll' || (a.closest && a.closest('nav'));
    if (!idle || a === el) return;
    el.focus({ preventScroll: true });
}

function scrollElementToBottom(id) {
    var el = document.getElementById(id);
    if (el) el.scrollTop = el.scrollHeight;
}

function scrollElementToRight(id) {
    var el = document.getElementById(id);
    if (el) el.scrollLeft = el.scrollWidth;
}

function initCalendarSwipe(elementId, dotnetRef) {
    // Week-swipe gesture on the calendar grid. Passive listeners only (never
    // preventDefault), so native horizontal/vertical scrolling stays untouched.
    // A week change only fires when the horizontal scroll container was already
    // at the corresponding edge when the gesture STARTED (carousel pattern:
    // panning within scrollable content scrolls; a swipe at the edge pages).
    var el = document.getElementById(elementId);
    if (!el) return;
    if (el._calendarSwipeCleanup) el._calendarSwipeCleanup();
    var startX = null, startY = 0, wasAtLeft = false, wasAtRight = false;
    var EDGE = 8, MIN_DIST = 60, AXIS_RATIO = 1.5;
    var onStart = function (e) {
        if (e.touches.length !== 1) { startX = null; return; } // multi-touch (pinch) => not a swipe
        startX = e.touches[0].clientX;
        startY = e.touches[0].clientY;
        var max = el.scrollWidth - el.clientWidth;
        wasAtLeft = el.scrollLeft <= EDGE;
        wasAtRight = el.scrollLeft >= max - EDGE;
    };
    var onEnd = function (e) {
        if (startX === null || e.changedTouches.length === 0) return;
        var dx = e.changedTouches[0].clientX - startX;
        var dy = e.changedTouches[0].clientY - startY;
        startX = null;
        // Minimum distance + dominant-axis check: vertical scrolling through the
        // day (or a simple tap on a session) must never trigger a week change.
        if (Math.abs(dx) < MIN_DIST || Math.abs(dx) <= Math.abs(dy) * AXIS_RATIO) return;
        if (dx < 0 && wasAtRight) {
            el.scrollLeft = 0; // next week: snap back so Monday is visible
            dotnetRef.invokeMethodAsync('SwipeNextWeek').catch(function () { });
        } else if (dx > 0 && wasAtLeft) {
            el.scrollLeft = el.scrollWidth - el.clientWidth; // prev week: land on Sunday (continuous timeline)
            dotnetRef.invokeMethodAsync('SwipePrevWeek').catch(function () { });
        }
    };
    el.addEventListener('touchstart', onStart, { passive: true });
    el.addEventListener('touchend', onEnd, { passive: true });
    el._calendarSwipeCleanup = function () {
        el.removeEventListener('touchstart', onStart);
        el.removeEventListener('touchend', onEnd);
        delete el._calendarSwipeCleanup;
    };
}

function disposeCalendarSwipe(elementId) {
    var el = document.getElementById(elementId);
    if (el && el._calendarSwipeCleanup) el._calendarSwipeCleanup();
}

function printPage() {
    // window.print() is undefined or throws in some standalone/installed-PWA webviews
    // (notably on some Android/iOS home-screen setups) - never let that surface as an
    // unhandled JS interop exception in Blazor, just no-op if printing isn't available.
    try {
        if (typeof window.print === 'function') {
            window.print();
            return true;
        }
    } catch (e) { /* printing unsupported in this webview, ignore */ }
    return false;
}

function setPageTitle(title) {
    document.title = title;
}

function isPageHidden() {
    return document.hidden;
}

// Read by the Setup page's "Report a problem" link (reduced to browser family, major
// version and OS there; the raw string is never sent anywhere).
function getUserAgent() {
    return navigator.userAgent;
}

function playCompletionSound() {
    try {
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.type = 'sine';
        osc.frequency.setValueAtTime(880, ctx.currentTime);
        osc.frequency.exponentialRampToValueAtTime(1318.5, ctx.currentTime + 0.15);
        gain.gain.setValueAtTime(0.001, ctx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.5);
        osc.start(ctx.currentTime);
        osc.stop(ctx.currentTime + 0.55);
    } catch (e) { /* audio blocked or unsupported, ignore */ }

    try {
        if (navigator.vibrate) navigator.vibrate(200);
    } catch (e) { /* vibration unsupported, ignore */ }
}

// Screen Wake Lock: keeps the display on while a focus timer is running.
// The browser auto-releases the lock when the tab is hidden, so we track
// intent in wakeLockWanted and re-acquire on visibilitychange.
let wakeLockSentinel = null;
let wakeLockWanted = false;

async function requestWakeLock() {
    wakeLockWanted = true;
    if (!('wakeLock' in navigator)) return false;
    try {
        wakeLockSentinel = await navigator.wakeLock.request('screen');
        return true;
    } catch (e) {
        // Can reject e.g. on low battery or when the document is hidden.
        return false;
    }
}

async function releaseWakeLock() {
    wakeLockWanted = false;
    try {
        if (wakeLockSentinel) await wakeLockSentinel.release();
    } catch (e) { /* already released, ignore */ }
    wakeLockSentinel = null;
}

document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && wakeLockWanted) {
        requestWakeLock();
    }
});

function applyTheme(theme) {
    // theme: "dark" | "light" | "system". "system" removes the override so the
    // prefers-color-scheme media query in base.css takes over again.
    if (theme === 'dark' || theme === 'light') {
        document.documentElement.setAttribute('data-theme', theme);
    } else {
        document.documentElement.removeAttribute('data-theme');
    }
}

function setDocumentLanguage(lang) {
    // Keeps <html lang> in sync with the app's own i18n language - Chromium browsers
    // base the formatting of native form controls (especially <input type="datetime-local">,
    // AM/PM vs. 24h) on this, NOT on navigator.language.
    document.documentElement.setAttribute('lang', lang);
}

async function requestNotificationPermission() {
    if (!('Notification' in window)) return 'unsupported';
    if (Notification.permission === 'granted') return 'granted';
    if (Notification.permission === 'denied') return 'denied';
    const result = await Notification.requestPermission();
    return result;
}

function getNotificationPermissionStatus() {
    const isIos = /iphone|ipad|ipod/i.test(navigator.userAgent);
    const isStandalone = window.navigator.standalone === true
        || window.matchMedia('(display-mode: standalone)').matches;

    if (isIos && !isStandalone) return 'ios-browser';
    if (!('Notification' in window)) return 'unsupported';
    return Notification.permission; // 'default' | 'granted' | 'denied'
}

// title/body come from the caller (Setup.razor passes the SetupText table text, mirrors
// interop.js): the notification is shown locally, so it must be in the UI language.
async function sendTestNotification(title, body) {
    if (!('serviceWorker' in navigator)) return;
    const reg = await navigator.serviceWorker.ready;
    reg.showNotification(title, {
        body: body,
        icon: '/icons/icon-192.png',
        badge: '/icons/icon-192.png',
        tag: 'studylife-test',
        renotify: true
    });
}

function showLocalNotification(title, body) {
    if (!('serviceWorker' in navigator)) return;
    navigator.serviceWorker.ready.then(reg => {
        reg.showNotification(title, {
            body: body,
            icon: '/icons/icon-192.png',
            badge: '/icons/icon-192.png',
            tag: 'studylife-timer',
            renotify: true
        });
    });
}

function showSessionNotification(title, body) {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    const n = new Notification(title, {
        body: body,
        icon: '/icons/icon-512.png',
        badge: '/icons/icon-512.png',
        tag: 'studylife-session',
        renotify: true
    });
    n.onclick = function () { window.focus(); n.close(); };
}

function setAppBadge(count) {
    try {
        if (!('setAppBadge' in navigator)) return;
        if (count > 0) {
            navigator.setAppBadge(count).catch(() => { /* ignore */ });
        } else {
            navigator.clearAppBadge().catch(() => { /* ignore */ });
        }
    } catch (e) { /* badging unsupported, ignore */ }
}

function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - base64String.length % 4) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const rawData = window.atob(base64);
    return Uint8Array.from([...rawData].map(c => c.charCodeAt(0)));
}

async function subscribePush(vapidPublicKey) {
    // Web Push only exists in the PWA - the native app uses local notifications.
    return null;
}

async function unsubscribePush() { /* Web Push: PWA only */ }

function initGlobalSearchHotkey(dotnetRef) {
    if (window._globalSearchHotkeyCleanup) window._globalSearchHotkeyCleanup();
    var handler = function (e) {
        if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('ToggleGlobalSearch').catch(function () { });
        }
    };
    document.addEventListener('keydown', handler);
    window._globalSearchHotkeyCleanup = function () {
        document.removeEventListener('keydown', handler);
        delete window._globalSearchHotkeyCleanup;
    };
}

function isNarrowViewport() { return window.matchMedia('(max-width: 768px)').matches; }

function scrollElementToCurrentTime(scrollContainerId) {
    var container = document.getElementById(scrollContainerId);
    if (!container) return;
    var nowLine = container.querySelector('.cal-now-line');
    if (!nowLine) return;

    var containerRect = container.getBoundingClientRect();
    var lineRect = nowLine.getBoundingClientRect();
    var lineOffsetInContent = (lineRect.top - containerRect.top) + container.scrollTop;

    var target = lineOffsetInContent - container.clientHeight * 0.3;
    var maxScroll = container.scrollHeight - container.clientHeight;
    container.scrollTop = Math.max(0, Math.min(target, maxScroll));
}

// Same geometry as scrollElementToCurrentTime above, generalized to an arbitrary selector
// instead of hardcoding .cal-now-line - used by the planner's calendar preview
// (PlannerCalendarPreview.razor) to open scrolled to the first proposed session instead
// of midnight. 1:1 copy from StudyLife.Client/wwwroot/js/interop.js (see the comment
// there). A no-op if either the container or a matching element isn't in the DOM.
function scrollElementToSelector(scrollContainerId, targetSelector) {
    var container = document.getElementById(scrollContainerId);
    if (!container) return;
    var target = container.querySelector(targetSelector);
    if (!target) return;

    var containerRect = container.getBoundingClientRect();
    var targetRect = target.getBoundingClientRect();
    var targetOffsetInContent = (targetRect.top - containerRect.top) + container.scrollTop;

    var scrollTo = targetOffsetInContent - container.clientHeight * 0.3;
    var maxScroll = container.scrollHeight - container.clientHeight;
    container.scrollTop = Math.max(0, Math.min(scrollTo, maxScroll));
}

// ============================================================================
// Notes voice features - 1:1 copy from StudyLife.Client/wwwroot/js/interop.js
// (see the comments there for the full rationale of each fallback).
// ============================================================================

function speakText(text, lang) {
    if (!window.speechSynthesis) return false;
    window.speechSynthesis.cancel();
    var utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = lang;
    window.speechSynthesis.speak(utterance);
    return true;
}

let _dictationRecorder = null;
let _dictationChunks = [];

async function startDictationRecording() {
    if (!navigator.mediaDevices || !window.MediaRecorder) return false;
    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        _dictationChunks = [];
        _dictationRecorder = new MediaRecorder(stream);
        _dictationRecorder.ondataavailable = function (e) {
            if (e.data.size > 0) _dictationChunks.push(e.data);
        };
        _dictationRecorder.start();
        return true;
    } catch {
        return false;
    }
}

function stopDictationRecording() {
    return new Promise(function (resolve, reject) {
        if (!_dictationRecorder) { reject('not recording'); return; }
        _dictationRecorder.onstop = async function () {
            _dictationRecorder.stream.getTracks().forEach(function (t) { t.stop(); });
            _dictationRecorder = null;
            try {
                const blob = new Blob(_dictationChunks, { type: 'audio/webm' });
                resolve(await blobToWavBase64(blob));
            } catch (err) {
                reject(String(err));
            }
        };
        _dictationRecorder.stop();
    });
}

function cancelDictationRecording() {
    if (!_dictationRecorder) return;
    _dictationRecorder.onstop = null;
    _dictationRecorder.stream.getTracks().forEach(function (t) { t.stop(); });
    _dictationRecorder.stop();
    _dictationRecorder = null;
    _dictationChunks = [];
}

let _activeSpeechRecognition = null;

function startBrowserSpeechRecognition(lang) {
    return new Promise(function (resolve, reject) {
        const SpeechRecognitionApi = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognitionApi) { reject('not supported'); return; }
        const recognition = new SpeechRecognitionApi();
        recognition.lang = lang;
        recognition.interimResults = false;
        recognition.continuous = true;
        let finalTranscript = '';
        let started = false;
        recognition.onresult = function (event) {
            for (let i = event.resultIndex; i < event.results.length; i++) {
                if (event.results[i].isFinal) finalTranscript += event.results[i][0].transcript + ' ';
            }
        };
        recognition.onstart = function () { started = true; resolve(true); };
        recognition.onerror = function (event) {
            if (!started) resolve(false);
        };
        recognition.onend = function () {
            _activeSpeechRecognition = null;
            window._lastSpeechRecognitionResult = finalTranscript.trim();
        };
        _activeSpeechRecognition = recognition;
        recognition.start();
    });
}

async function stopBrowserSpeechRecognition() {
    if (!_activeSpeechRecognition) return window._lastSpeechRecognitionResult || '';
    _activeSpeechRecognition.stop();
    for (let i = 0; i < 20 && _activeSpeechRecognition !== null; i++) {
        await new Promise(function (r) { setTimeout(r, 100); });
    }
    const result = window._lastSpeechRecognitionResult || '';
    window._lastSpeechRecognitionResult = undefined;
    return result;
}

async function blobToWavBase64(blob) {
    const arrayBuffer = await blob.arrayBuffer();
    const decodeCtx = new (window.AudioContext || window.webkitAudioContext)();
    const decoded = await decodeCtx.decodeAudioData(arrayBuffer);
    decodeCtx.close();

    const targetRate = 16000;
    const offlineCtx = new OfflineAudioContext(1, Math.ceil(decoded.duration * targetRate), targetRate);
    const source = offlineCtx.createBufferSource();
    source.buffer = decoded;
    source.connect(offlineCtx.destination);
    source.start();
    const rendered = await offlineCtx.startRendering();
    const samples = rendered.getChannelData(0);

    let sumSquares = 0;
    for (let i = 0; i < samples.length; i++) sumSquares += samples[i] * samples[i];
    const rms = Math.sqrt(sumSquares / samples.length);
    if (rms < 0.01) return null;

    const wavBytes = encodeWavPcm16(samples, targetRate);
    let binary = '';
    for (let i = 0; i < wavBytes.length; i++) binary += String.fromCharCode(wavBytes[i]);
    return window.btoa(binary);
}

function encodeWavPcm16(samples, sampleRate) {
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);
    function writeString(offset, str) {
        for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i));
    }
    writeString(0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    writeString(8, 'WAVE');
    writeString(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    writeString(36, 'data');
    view.setUint32(40, samples.length * 2, true);
    let offset = 44;
    for (let i = 0; i < samples.length; i++, offset += 2) {
        const s = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
    }
    return new Uint8Array(buffer);
}
