#!/usr/bin/env bash
# App Store screenshots of the iOS app and its Apple Watch app in the Simulator (README "App Store screenshots").
#
# Expects a StudyAtrium server in demo mode at SERVER_URL whose TLS certificate chains to CA_CERT, seeded in
# LANGUAGE and for the time STATUS_TIME (demo-server.sh), and a simulator build of the app made with
# -p:StoreScreenshots=true at APP_PATH. For every phone/tablet device below it creates a fresh simulator, trusts
# CA_CERT there, sets a clean status bar, installs the app and launches it once per screen with the store
# screenshot arguments (system and app language from LANGUAGE, the app's clock pinned to STATUS_TIME). The app
# writes a status file into its data container once the page is signed in, on the right route and done loading
# (StoreScreenshotSession); only then is the screen captured.
#
# The Watch app (WATCH_APP_PATH, a watchsimulator build) has no server connection of its own: on a real device
# it shows what the phone sends it. Here the phone app writes that very snapshot (HomeWidgetSnapshot, computed
# from the demo data for STATUS_TIME) next to its status file, and the script puts it into the Watch
# simulator's App Group container before launching the Watch app.
#
# Output: OUT_DIR/<device>/<nn>-<screen>.png, plus OUT_DIR/logs/ with the app's console output.
# Environment: APP_PATH, SERVER_URL, CA_CERT, OUT_DIR (required), LANGUAGE (de|en, default de),
# APPEARANCE (dark|light, default dark), WATCH_APP_PATH (optional; no Watch screenshots without it).
set -euo pipefail

: "${APP_PATH:?simulator .app bundle}" "${SERVER_URL:?server address}" "${CA_CERT:?CA certificate (PEM)}" "${OUT_DIR:?output folder}"
APPEARANCE="${APPEARANCE:-dark}"
LANGUAGE="${LANGUAGE:-de}"
WATCH_APP_PATH="${WATCH_APP_PATH:-}"
BUNDLE_ID="app.studylife.mobile"
WATCH_BUNDLE_ID="app.studylife.mobile.watchkitapp"
APP_GROUP="group.app.studylife.mobile"
STATUS_FILE="store-screenshot-status.txt"                   # StoreScreenshotSession.StatusFileName
WATCH_SNAPSHOT_FILE="store-screenshot-watch-snapshot.json"  # StoreScreenshotSession.WatchSnapshotFileName
STATUS_WAIT_SECONDS=180                                     # above the app's own 120 s readiness timeout
# The time in the status bar override and the app's clock (--clock), so every "now" in a screenshot agrees.
STATUS_TIME="09:41"

case "$LANGUAGE" in
  de) APPLE_LANGUAGE="de"; APPLE_LOCALE="de_DE"; NOTE_TITLE="Merkzettel Eigenwerte" ;;
  en) APPLE_LANGUAGE="en"; APPLE_LOCALE="en_US"; NOTE_TITLE="Eigenvalues cheat sheet" ;;
  *) echo "::error::LANGUAGE must be de or en, not '$LANGUAGE'"; exit 1 ;;
esac
LANGUAGE_ARGS=(-AppleLanguages "($APPLE_LANGUAGE)" -AppleLocale "$APPLE_LOCALE")

# folder name | expected pixel size | simulator device types, best first (the first one this Xcode has wins).
DEVICES=(
  "iphone-6.9|1320x2868|iPhone 17 Pro Max|iPhone 16 Pro Max"
  "ipad-13|2064x2752|iPad Pro 13-inch (M5)|iPad Pro 13-inch (M4)"
)
# Apple's accepted Watch sizes include 416x496 (Series 10/11 46mm) and 422x514 (Ultra 3); one size serves all.
WATCH_DEVICE="watch-46mm|416x496|Apple Watch Series 11 (46mm)|Apple Watch Series 10 (46mm)"

log() { echo "[store-screenshots] $*"; }

latest_runtime() {
  xcrun simctl list runtimes --json | python3 -c '
import json, sys
platform = sys.argv[1]
runtimes = [r for r in json.load(sys.stdin)["runtimes"] if r.get("isAvailable") and r.get("platform", r["name"].split()[0]) == platform]
runtimes.sort(key=lambda r: [int(p) for p in r["version"].split(".")])
print(runtimes[-1]["identifier"] if runtimes else "")' "$1"
}

device_type_id() {
  xcrun simctl list devicetypes --json | python3 -c '
import json, sys
wanted = sys.argv[1]
print(next((t["identifier"] for t in json.load(sys.stdin)["devicetypes"] if t["name"] == wanted), ""))' "$1"
}

# Prints "<type id>|<name>" of the first available device type in "name|name|...", or nothing.
pick_device_type() {
  local candidates="$1" name type_id
  IFS='|' read -r -a names <<<"$candidates"
  for name in "${names[@]}"; do
    type_id="$(device_type_id "$name")"
    if [ -n "$type_id" ]; then echo "$type_id|$name"; return 0; fi
  done
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
  [ -z "$container" ] || find "$container" \( -name "$STATUS_FILE" -o -name "$WATCH_SNAPSHOT_FILE" \) -delete 2>/dev/null || true
}

# System language and region for the whole simulator, not only the app (LANGUAGE_ARGS): the status bar belongs
# to the system and formats its date with the system locale. Takes effect after the restart in boot_clean.
set_system_language() {
  xcrun simctl spawn "$1" defaults write -g AppleLanguages -array "$APPLE_LANGUAGE"
  xcrun simctl spawn "$1" defaults write -g AppleLocale -string "$APPLE_LOCALE"
}

boot_clean() {
  xcrun simctl shutdown "$1"
  xcrun simctl boot "$1"
  xcrun simctl bootstatus "$1" -b >/dev/null
}

check_size() {
  local file="$1" expected="$2" size
  size="$(sips -g pixelWidth -g pixelHeight "$file" | awk '/pixelWidth/ {w=$2} /pixelHeight/ {h=$2} END {print w "x" h}')"
  [ "$size" = "$expected" ] || echo "::warning::$file is $size, App Store Connect expects $expected for this display size" >&2
  echo "$size"
}

# The id of the demo note shown on the notes screenshot, asked from the demo server (the ids change with every
# reseed). Signs in through the demo login the app itself uses; nothing else is involved. Empty when not found.
demo_note_id() {
  python3 - "$SERVER_URL" "$CA_CERT" "$NOTE_TITLE" <<'PY'
import json, ssl, sys, urllib.request
server, ca, title = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
context = ssl.create_default_context(cafile=ca)
def call(path, method="GET", token=None):
    request = urllib.request.Request(server + path, method=method, data=b"" if method == "POST" else None)
    if token:
        request.add_header("X-Session-Token", token)
    with urllib.request.urlopen(request, context=context, timeout=30) as response:
        return json.load(response)
try:
    token = call("/api/auth/demo-login", "POST")["token"]
    print(next((n["id"] for n in call("/api/notes", token=token) if n.get("title") == title), ""))
except Exception as error:
    print(f"demo note lookup failed: {error}", file=sys.stderr)
    print("")
PY
}

NOTE_ID="$(demo_note_id)"
if [ -n "$NOTE_ID" ]; then
  NOTES_ROUTE="/notes?note=$NOTE_ID"
else
  echo "::warning::the demo note '$NOTE_TITLE' was not found; the notes screenshot shows the list only"
  NOTES_ROUTE="/notes"
fi

# name and route; the route is what the app's router knows (StudyLife.Client/Pages).
SCREENS=(
  "01-dashboard /"
  "02-calendar /calendar"
  "03-focus /focus"
  "04-notes $NOTES_ROUTE"
  "05-statistics /auswertung"
)

IOS_RUNTIME="$(latest_runtime iOS)"
[ -n "$IOS_RUNTIME" ] || { echo "::error::no iOS simulator runtime available"; exit 1; }
log "language: $LANGUAGE ($APPLE_LOCALE), runtime: $IOS_RUNTIME"
mkdir -p "$OUT_DIR/logs"
failures=0
watch_snapshot=""

for entry in "${DEVICES[@]}"; do
  folder="$(cut -d'|' -f1 <<<"$entry")"
  expected_size="$(cut -d'|' -f2 <<<"$entry")"
  picked="$(pick_device_type "$(cut -d'|' -f3- <<<"$entry")")"
  if [ -z "$picked" ]; then
    echo "::error::none of these simulators exists in this Xcode: $(cut -d'|' -f3- <<<"$entry")"
    failures=$((failures + 1))
    continue
  fi
  type_id="${picked%%|*}"
  device_name="${picked#*|}"

  log "$folder: $device_name"
  udid="$(xcrun simctl create "store-$folder" "$type_id" "$IOS_RUNTIME")"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  set_system_language "$udid"
  if [[ "$folder" == ipad-* ]]; then
    # iPadOS 26 starts new simulators in "Windowed Apps" multitasking, which draws the window's resize handle
    # (a small grey arc) into the bottom right corner of every app. "Full Screen Apps" (Settings > Multitasking
    # & Gestures) is what these two SpringBoard defaults record; it takes effect with the restart below.
    xcrun simctl spawn "$udid" defaults write com.apple.springboard SBChamoisWindowingEnabled -bool false
    xcrun simctl spawn "$udid" defaults write com.apple.springboard SBMedusaMultitaskingEnabled -bool false
  fi
  boot_clean "$udid"
  # The server's certificate is issued by the throwaway CA of this run; trusting it here keeps the app's
  # transport security untouched (https, normal certificate validation).
  xcrun simctl keychain "$udid" add-root-cert "$CA_CERT"
  xcrun simctl ui "$udid" appearance "$APPEARANCE"
  xcrun simctl status_bar "$udid" override --time "$STATUS_TIME" --dataNetwork wifi --wifiMode active --wifiBars 3 \
    --cellularMode active --cellularBars 4 --batteryState charged --batteryLevel 100
  xcrun simctl install "$udid" "$APP_PATH"

  mkdir -p "$OUT_DIR/$folder"
  for screen in "${SCREENS[@]}"; do
    read -r shot route <<<"$screen"
    # The phone stacks the note under the list, so an open note ends behind the floating menu button there;
    # the list alone reads better. The iPad shows list and note side by side.
    if [[ "$folder" == iphone-* && "$shot" == 04-notes ]]; then route="/notes"; fi
    # A second start when the first did not report ready: on the very first start the shared client can lose
    # the fresh demo session to a 401 of a request it sent before signing in, and stay on the sign-in page.
    for attempt in 1 2; do
      clear_status "$udid"
      xcrun simctl launch --terminate-running-process \
        --stdout="$OUT_DIR/logs/$folder-$shot-$attempt.log" --stderr="$OUT_DIR/logs/$folder-$shot-$attempt.log" \
        "$udid" "$BUNDLE_ID" "${LANGUAGE_ARGS[@]}" --store-screenshots "$SERVER_URL" --route "$route" --clock "$STATUS_TIME" >/dev/null
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
    log "$folder/$shot.png ($(check_size "$file" "$expected_size"), $status)"
    # The Watch gets the phone's snapshot of the dashboard run (the first device's is enough).
    if [ "$shot" = "01-dashboard" ] && [ -z "$watch_snapshot" ]; then
      container="$(data_container "$udid")"
      found=""
      [ -z "$container" ] || found="$(find "$container" -name "$WATCH_SNAPSHOT_FILE" 2>/dev/null | head -n 1 || true)"
      if [ -n "$found" ]; then
        watch_snapshot="$OUT_DIR/logs/watch-snapshot.json"
        cp "$found" "$watch_snapshot"
      fi
    fi
  done

  xcrun simctl terminate "$udid" "$BUNDLE_ID" || true
  xcrun simctl shutdown "$udid" || true
  xcrun simctl delete "$udid" || true
done

capture_watch() {
  local folder expected_size picked type_id device_name runtime udid group file
  folder="$(cut -d'|' -f1 <<<"$WATCH_DEVICE")"
  expected_size="$(cut -d'|' -f2 <<<"$WATCH_DEVICE")"
  runtime="$(latest_runtime watchOS)"
  [ -n "$runtime" ] || { echo "::error::no watchOS simulator runtime available"; return 1; }
  picked="$(pick_device_type "$(cut -d'|' -f3- <<<"$WATCH_DEVICE")")"
  [ -n "$picked" ] || { echo "::error::none of these Watch simulators exists in this Xcode: $(cut -d'|' -f3- <<<"$WATCH_DEVICE")"; return 1; }
  type_id="${picked%%|*}"
  device_name="${picked#*|}"
  [ -n "$watch_snapshot" ] || echo "::warning::the phone app wrote no Watch snapshot; the Watch shows its empty state"

  log "$folder: $device_name ($runtime)"
  udid="$(xcrun simctl create "store-$folder" "$type_id" "$runtime")"
  xcrun simctl boot "$udid"
  xcrun simctl bootstatus "$udid" -b >/dev/null
  set_system_language "$udid"
  boot_clean "$udid"
  # watchOS simulators refuse status bar overrides (simctl: NSPOSIXErrorDomain 45, "not supported"), so the
  # Watch app draws the pinned time itself (--clock below) in place of the system clock; the override is tried
  # anyway in case a later Xcode supports it.
  xcrun simctl status_bar "$udid" override --time "$STATUS_TIME" >/dev/null 2>&1 \
    || log "$folder: no status bar override on watchOS, the Watch app draws the pinned clock itself"
  xcrun simctl install "$udid" "$WATCH_APP_PATH"
  if [ -n "$watch_snapshot" ]; then
    group="$(xcrun simctl get_app_container "$udid" "$WATCH_BUNDLE_ID" "$APP_GROUP" 2>/dev/null || true)"
    if [ -n "$group" ]; then
      cp "$watch_snapshot" "$group/watch-snapshot.json" # WatchSnapshot.swift: watchSnapshotFileName
    else
      echo "::warning::the Watch app has no App Group container in the simulator; the Watch shows its empty state"
    fi
  fi
  xcrun simctl launch --terminate-running-process \
    --stdout="$OUT_DIR/logs/$folder.log" --stderr="$OUT_DIR/logs/$folder.log" "$udid" "$WATCH_BUNDLE_ID" --store-screenshots --clock "$STATUS_TIME" >/dev/null
  # No readiness signal from a SwiftUI app: it reads the snapshot file on launch and draws once. The argument
  # keeps the notification permission sheet away (StudyLifeWatchApp.swift, simulator builds only).
  sleep 8
  mkdir -p "$OUT_DIR/$folder"
  file="$OUT_DIR/$folder/01-today.png"
  xcrun simctl io "$udid" screenshot --type=png "$file" >/dev/null
  log "$folder/01-today.png ($(check_size "$file" "$expected_size"))"
  xcrun simctl shutdown "$udid" || true
  xcrun simctl delete "$udid" || true
}

if [ -n "$WATCH_APP_PATH" ]; then
  capture_watch || failures=$((failures + 1))
else
  log "no WATCH_APP_PATH: no Watch screenshots"
fi

if [ "$failures" -gt 0 ]; then
  echo "::error::$failures screenshot(s) failed"
  exit 1
fi
