#!/usr/bin/env bash
# App Store screenshots of the iOS app in the Simulator (README "App Store screenshots").
#
# Expects a StudyAtrium server in demo mode at SERVER_URL whose TLS certificate chains to CA_CERT, and a
# simulator build of the app made with -p:StoreScreenshots=true at APP_PATH. For every device below it
# creates a fresh simulator, trusts CA_CERT there, sets a clean status bar, installs the app and launches it
# once per screen with the store screenshot arguments (German system and app language). The app writes a
# status file into its data container once the page is signed in, on the right route and done loading
# (StoreScreenshotSession); only then is the screen captured.
#
# Output: OUT_DIR/<device>/<nn>-<screen>.png, plus OUT_DIR/logs/ with the app's console output.
# Environment: APP_PATH, SERVER_URL, CA_CERT, OUT_DIR (required), APPEARANCE (dark|light, default dark).
set -euo pipefail

: "${APP_PATH:?simulator .app bundle}" "${SERVER_URL:?server address}" "${CA_CERT:?CA certificate (PEM)}" "${OUT_DIR:?output folder}"
APPEARANCE="${APPEARANCE:-dark}"
BUNDLE_ID="app.studylife.mobile"
STATUS_FILE="store-screenshot-status.txt" # StoreScreenshotSession.StatusFileName
STATUS_WAIT_SECONDS=180                   # above the app's own 120 s readiness timeout
LANGUAGE_ARGS=(-AppleLanguages "(de)" -AppleLocale "de_DE")

# name and route; the route is what the app's router knows (StudyLife.Client/Pages).
SCREENS=(
  "01-dashboard /"
  "02-calendar /calendar"
  "03-focus /focus"
  "04-notes /notes"
  "05-statistics /auswertung"
)

# folder name | expected pixel size | simulator device types, best first (the first one this Xcode has wins).
DEVICES=(
  "iphone-6.9|1320x2868|iPhone 17 Pro Max|iPhone 16 Pro Max"
  "ipad-13|2064x2752|iPad Pro 13-inch (M5)|iPad Pro 13-inch (M4)"
)

log() { echo "[store-screenshots] $*"; }

latest_ios_runtime() {
  xcrun simctl list runtimes --json | python3 -c '
import json, sys
runtimes = [r for r in json.load(sys.stdin)["runtimes"] if r.get("isAvailable") and r.get("platform", r["name"].split()[0]) == "iOS"]
runtimes.sort(key=lambda r: [int(p) for p in r["version"].split(".")])
print(runtimes[-1]["identifier"] if runtimes else "")'
}

device_type_id() {
  xcrun simctl list devicetypes --json | python3 -c '
import json, sys
wanted = sys.argv[1]
print(next((t["identifier"] for t in json.load(sys.stdin)["devicetypes"] if t["name"] == wanted), ""))' "$1"
}

data_container() { xcrun simctl get_app_container "$1" "$BUNDLE_ID" data 2>/dev/null || true; }

wait_for_status() {
  local udid="$1" waited=0 container status
  while [ "$waited" -lt "$STATUS_WAIT_SECONDS" ]; do
    container="$(data_container "$udid")"
    if [ -n "$container" ]; then
      status="$(find "$container" -name "$STATUS_FILE" -exec cat {} \; 2>/dev/null | head -n 1)"
      if [ -n "$status" ]; then
        echo "$status"
        return 0
      fi
    fi
    sleep 2
    waited=$((waited + 2))
  done
  echo "no-status"
}

clear_status() {
  local container
  container="$(data_container "$1")"
  [ -z "$container" ] || find "$container" -name "$STATUS_FILE" -delete 2>/dev/null || true
}

RUNTIME="$(latest_ios_runtime)"
[ -n "$RUNTIME" ] || { echo "::error::no iOS simulator runtime available"; exit 1; }
log "runtime: $RUNTIME"
mkdir -p "$OUT_DIR/logs"
failures=0

for entry in "${DEVICES[@]}"; do
  folder="$(cut -d'|' -f1 <<<"$entry")"
  expected_size="$(cut -d'|' -f2 <<<"$entry")"
  candidates="$(cut -d'|' -f3- <<<"$entry")"
  type_id=""
  IFS='|' read -r -a names <<<"$candidates"
  for name in "${names[@]}"; do
    type_id="$(device_type_id "$name")"
    if [ -n "$type_id" ]; then device_name="$name"; break; fi
  done
  if [ -z "$type_id" ]; then
    echo "::error::none of these simulators exists in this Xcode: $candidates"
    failures=$((failures + 1))
    continue
  fi

  log "$folder: $device_name"
  udid="$(xcrun simctl create "store-$folder" "$type_id" "$RUNTIME")"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  # German for the whole simulator, not only the app (LANGUAGE_ARGS): the status bar belongs to the system
  # and formats its date with the system locale. The setting takes effect after a restart of the simulator.
  xcrun simctl spawn "$udid" defaults write -g AppleLanguages -array de
  xcrun simctl spawn "$udid" defaults write -g AppleLocale -string de_DE
  xcrun simctl shutdown "$udid"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  # The server's certificate is issued by the throwaway CA of this run; trusting it here keeps the app's
  # transport security untouched (https, normal certificate validation).
  xcrun simctl keychain "$udid" add-root-cert "$CA_CERT"
  xcrun simctl ui "$udid" appearance "$APPEARANCE"
  xcrun simctl status_bar "$udid" override --time "9:41" --dataNetwork wifi --wifiMode active --wifiBars 3 \
    --cellularMode active --cellularBars 4 --batteryState charged --batteryLevel 100
  xcrun simctl install "$udid" "$APP_PATH"

  mkdir -p "$OUT_DIR/$folder"
  for screen in "${SCREENS[@]}"; do
    read -r shot route <<<"$screen"
    # A second start when the first did not report ready: on the very first start the shared client can lose
    # the fresh demo session to a 401 of a request it sent before signing in, and stay on the sign-in page.
    for attempt in 1 2; do
      clear_status "$udid"
      xcrun simctl launch --terminate-running-process \
        --stdout="$OUT_DIR/logs/$folder-$shot-$attempt.log" --stderr="$OUT_DIR/logs/$folder-$shot-$attempt.log" \
        "$udid" "$BUNDLE_ID" "${LANGUAGE_ARGS[@]}" --store-screenshots "$SERVER_URL" --route "$route" >/dev/null
      status="$(wait_for_status "$udid")"
      case "$status" in
        ready*) break ;;
      esac
      if [ "$attempt" = 1 ]; then log "$folder/$shot: $status, starting the app again"; fi
    done
    case "$status" in
      ready*) ;;
      timeout*) echo "::warning::$folder/$shot: the page did not settle within the app's timeout ($status); captured anyway, check it" ;;
      *)
        echo "::error::$folder/$shot: the app never reported a status (see logs/$folder-$shot-*.log)"
        failures=$((failures + 1))
        ;;
    esac
    # Let the last transition (page fade, chart animation) finish.
    sleep 2
    file="$OUT_DIR/$folder/$shot.png"
    xcrun simctl io "$udid" screenshot --type=png "$file" >/dev/null
    size="$(sips -g pixelWidth -g pixelHeight "$file" | awk '/pixelWidth/ {w=$2} /pixelHeight/ {h=$2} END {print w "x" h}')"
    log "$folder/$shot.png ($size, $status)"
    [ "$size" = "$expected_size" ] || echo "::warning::$folder/$shot.png is $size, App Store Connect expects $expected_size for this display size"
  done

  xcrun simctl terminate "$udid" "$BUNDLE_ID" || true
  xcrun simctl shutdown "$udid" || true
  xcrun simctl delete "$udid" || true
done

if [ "$failures" -gt 0 ]; then
  echo "::error::$failures screenshot(s) failed"
  exit 1
fi
