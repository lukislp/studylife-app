#!/bin/bash
# Catches the "works on web, throws immediately on native" bug class found live 2026-09-26
# (LocalDateNames DI registration + several interop.js functions, see PR #56): the native app
# doesn't reference StudyLife.Client's Program.cs or wwwroot/js/interop.js at all - it needs its
# own mirror of both (MauiProgram.cs's DI registrations, and wwwroot/js/native-interop.js's
# hand-copied functions), and nothing enforces that they stay in sync. This script is that
# enforcement, run from CI (see .github/workflows/ci-cd.yml) on every push/PR - a pure text
# comparison, no dotnet/workload install needed, so it also catches this before any of the
# iOS/Android/macOS/Windows build jobs even start.
#
# Run from the repo root, with the sibling studylife checkout already present at ./studylife
# (same layout every other CI job and the local Mac build pipeline already assume).
set -euo pipefail
cd "$(dirname "$0")/.."

CLIENT_PROGRAM="studylife/src/StudyLife.Client/Program.cs"
CLIENT_INTEROP="studylife/src/StudyLife.Client/wwwroot/js/interop.js"
APP_MAUIPROGRAM="src/StudyLife.App/MauiProgram.cs"
APP_INDEXHTML="src/StudyLife.App/wwwroot/index.html"
# The app's own scripts, loaded by index.html: the interop mirror and the native overrides.
APP_SCRIPTS="src/StudyLife.App/wwwroot/js/native-interop.js src/StudyLife.App/wwwroot/js/native-bridge.js"

FAILED=0

# ── Check 1: shared Client services must be registered in MauiProgram.cs too ────────────
# Scoped to single-generic-argument self-registrations (AddScoped<Foo>()) - the pattern used
# for StudyLife.Client's own shared services (AppStateService, TimerService, LocalDateNames,
# ...). Deliberately excludes the two-argument interface->implementation registrations
# (AddScoped<INativeAppAuth, NativeAppAuth>()), since native and web are EXPECTED to bind those
# to different concrete types - that asymmetry is the whole point of IClientPlatform/INative*.
CLIENT_SERVICES=$(grep -oP 'Add(?:Scoped|Singleton|Transient)<\K\w+(?=>\(\))' "$CLIENT_PROGRAM" | sort -u)
APP_SERVICES=$(grep -oP 'Add(?:Scoped|Singleton|Transient)<\K\w+(?=>\(\))' "$APP_MAUIPROGRAM" | sort -u)
MISSING_DI=$(comm -23 <(echo "$CLIENT_SERVICES") <(echo "$APP_SERVICES") | sed '/^$/d' || true)

if [ -n "$MISSING_DI" ]; then
    FAILED=1
    echo "::error::Service(s) registered in $CLIENT_PROGRAM but missing from $APP_MAUIPROGRAM - any MainLayout/component that @injects one of these by concrete type throws resolving it, before the component's own code (or a try/catch) ever runs, showing the native app's generic 'unhandled error' screen immediately. Add the matching AddScoped/AddSingleton/AddTransient line to MauiProgram.cs. Missing: $(echo "$MISSING_DI" | tr '\n' ' ')"
fi

# ── Check 2: interop.js's global functions must exist in the app's hand-copied scripts ──
# index.html can't just <script src="js/interop.js"> it (js/native-bridge.js needs to override
# several of interop.js's functions with native equivalents - Wake Lock, Notifications,
# Print, ... - and interop.js carries web-only code), so every function gets manually
# duplicated into js/native-interop.js instead. Anything
# added to interop.js and never mirrored here throws a JS ReferenceError the moment a native
# page calls it.
INTEROP_FUNCS=$(grep -oP '^(async )?function \K\w+' "$CLIENT_INTEROP" | sort -u)
# shellcheck disable=SC2086 # APP_SCRIPTS is a list of paths without spaces
APP_FUNCS=$(cat $APP_SCRIPTS | grep -oP '^\s*(async )?function \K\w+' | sort -u)

# Deliberately NOT mirrored, with the reason each doesn't apply to a native BlazorWebView shell:
#   dispatchTimerStateChanged   - a DOM CustomEvent solely for browser extensions to observe
#                                 (studylife-focusguard/focustunes); no extension can attach here.
#   studylifeLock*              - Web Locks API cross-BROWSER-TAB coordination; a native app is
#                                 only ever one "tab", there's nothing to coordinate.
#   studylifeTelemetryInit/Flush, studylifeGetBootMarks, studylifeGetConnectionType,
#   studylifeSanitizeErrorStack, studylifeCollectStaticVitals, studylifeObserveVitals,
#   studylifeReportVitalsOnce, studylifeMarkFirstRender
#                                - WASM-download/web-vitals boot telemetry; native has its own,
#                                  separate telemetry pipeline (INativeTelemetry/TelemetryBridge).
#                                  All of these call sites are wrapped in try/catch and already
#                                  degrade silently by design (see TelemetryService.cs) - adding
#                                  them would report meaningless metrics, not fix a bug.
# When adding a new name here, say why it's native-irrelevant, same as above - this list is a
# deliberate exemption, not a place to silence a real gap.
ALLOWLIST="dispatchTimerStateChanged studylifeLockAcquire studylifeLockRelease studylifeLockTryAcquire studylifeTelemetryInit studylifeTelemetryFlush studylifeGetBootMarks studylifeGetConnectionType studylifeSanitizeErrorStack studylifeCollectStaticVitals studylifeObserveVitals studylifeReportVitalsOnce studylifeMarkFirstRender"

MISSING_JS=""
while IFS= read -r fn; do
    [ -n "$fn" ] || continue
    if ! grep -qx "$fn" <<<"$APP_FUNCS" && ! grep -qw "$fn" <<<"$ALLOWLIST"; then
        MISSING_JS="$MISSING_JS $fn"
    fi
done <<<"$INTEROP_FUNCS"

if [ -n "$MISSING_JS" ]; then
    FAILED=1
    echo "::error::Function(s) defined in $CLIENT_INTEROP but missing from the app's hand-copied scripts ($APP_SCRIPTS) - calling one of these from a native page throws a JS ReferenceError, normally surfacing as the page's <ErrorBoundary> fallback ('etwas ist schiefgelaufen') instead of the intended behavior. Copy the function into src/StudyLife.App/wwwroot/js/native-interop.js (see the existing ones there for the pattern), or if it's genuinely native-irrelevant, add it to ALLOWLIST in scripts/check-native-parity.sh with a one-line reason.Missing:$MISSING_JS"
fi

# ── Check 3: index.html keeps its Content-Security-Policy and runs no inline script ─────────
# The bundled page has no server in front of it, so its meta CSP is the only thing between an
# injected markup string (for example a crafted Markdown note) and script execution (audit
# 2026-11-16, finding H1). That only holds while script-src allows no inline script, so the
# app's scripts must stay files: a new inline <script> block or inline event handler would
# need 'unsafe-inline' and silently undo the protection.
CSP_LINE=$(grep -oP '<meta http-equiv="Content-Security-Policy" content="\K[^"]+' "$APP_INDEXHTML" || true)
SCRIPT_SRC=$(grep -oP "(^|; *)script-src \K[^;]+" <<<"$CSP_LINE" || true)
if [ -z "$CSP_LINE" ]; then
    FAILED=1
    echo "::error::$APP_INDEXHTML has no <meta http-equiv=\"Content-Security-Policy\"> - the bundled page needs its own CSP, the server's header never reaches it."
elif [ -z "$SCRIPT_SRC" ] || grep -qE "unsafe-inline|unsafe-eval|\*|https?:|data:|blob:" <<<"$SCRIPT_SRC"; then
    FAILED=1
    echo "::error::$APP_INDEXHTML's CSP script-src must be bundle-only ('self'), got: '${SCRIPT_SRC}'."
fi
if grep -qP '<script(?![^>]*\ssrc=)[^>]*>' "$APP_INDEXHTML"; then
    FAILED=1
    echo "::error::$APP_INDEXHTML contains an inline <script> block - the CSP blocks it. Move the code into a file under src/StudyLife.App/wwwroot/js/ and load it with <script src>."
fi
if grep -qiP '<[a-z][^>]*\son[a-z]+\s*=' "$APP_INDEXHTML"; then
    FAILED=1
    echo "::error::$APP_INDEXHTML contains an inline event handler attribute (on...=) - the CSP blocks it. Attach the listener from a script file instead."
fi

if [ "$FAILED" -ne 0 ]; then
    exit 1
fi

echo "OK: no native DI/JS-interop parity gaps found; index.html CSP is bundle-only for scripts."
