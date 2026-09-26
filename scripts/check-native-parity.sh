#!/bin/bash
# Catches the "works on web, throws immediately on native" bug class found live 2026-09-26
# (LocalDateNames DI registration + several interop.js functions, see PR #56): the native app
# doesn't reference StudyLife.Client's Program.cs or wwwroot/js/interop.js at all - it needs its
# own mirror of both (MauiProgram.cs's DI registrations, and wwwroot/index.html's hand-copied
# inline <script> functions), and nothing enforces that they stay in sync. This script is that
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

# ── Check 2: interop.js's global functions must exist in index.html's hand-copied script ──
# index.html can't just <script src="js/interop.js"> it (the native bridge script further down
# needs to override several of interop.js's functions with native equivalents - Wake Lock,
# Notifications, Print, ...), so every function gets manually duplicated instead. Anything
# added to interop.js and never mirrored here throws a JS ReferenceError the moment a native
# page calls it.
INTEROP_FUNCS=$(grep -oP '^(async )?function \K\w+' "$CLIENT_INTEROP" | sort -u)
APP_FUNCS=$(grep -oP '^\s*(async )?function \K\w+' "$APP_INDEXHTML" | sort -u)

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
    echo "::error::Function(s) defined in $CLIENT_INTEROP but missing from $APP_INDEXHTML's hand-copied inline script - calling one of these from a native page throws a JS ReferenceError, normally surfacing as the page's <ErrorBoundary> fallback ('etwas ist schiefgelaufen') instead of the intended behavior. Copy the function into index.html (see the existing ones there for the pattern), or if it's genuinely native-irrelevant, add it to ALLOWLIST in scripts/check-native-parity.sh with a one-line reason.Missing:$MISSING_JS"
fi

if [ "$FAILED" -ne 0 ]; then
    exit 1
fi

echo "OK: no native DI/JS-interop parity gaps found."
