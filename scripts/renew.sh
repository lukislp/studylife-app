#!/bin/bash
# Complete signature refresh (called twice a week by the launchd agent, can also be run
# manually at any time): first renews the Personal Team profiles (they expire, just like
# the signature, after 7 days), then re-signs the most recently built IPA and installs it
# onto the iPhone (WiFi is sufficient after the one-time USB pairing). App data is preserved.
#
# Heartbeat: reports success (up) or failure (down) to the Uptime Kuma push monitor,
# provided ~/.studylife-kuma-push exists (scripts/setup-kuma-heartbeat.sh). If the
# up ping stays absent for two scheduled runs, Kuma alerts - well before the 7-day
# signature expires.
set -euo pipefail
cd "$(dirname "$0")"

KUMA_PUSH_FILE="$HOME/.studylife-kuma-push"
ping_kuma() {
    [ -f "$KUMA_PUSH_FILE" ] || return 0
    curl -fsSk --max-time 10 "$(cat "$KUMA_PUSH_FILE")?status=$1&msg=$2" >/dev/null 2>&1 || true
}
trap 'ping_kuma down "renewal-failed-$(date +%H%M)"' ERR

echo "=== Renewal $(date) ==="
bash provision.sh
bash sign-and-install.sh
ping_kuma up "renewed"
echo "=== Renewal successful $(date) ==="
