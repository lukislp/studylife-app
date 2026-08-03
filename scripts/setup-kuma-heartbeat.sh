#!/bin/bash
# Interactive setup of the renewal heartbeat (run on the Mac):
# creates a push monitor "StudyLife iOS Signing Renewal" in Uptime Kuma - preferably
# automatically via the Kuma API (Python library uptime-kuma-api); if that doesn't work
# with the running Kuma version (v2 beta API still in flux), a guided fallback walks
# through the UI. The result is ALWAYS ~/.studylife-kuma-push containing the push URL;
# credentials are never stored anywhere. renew.sh pings this URL after every renewal (up)
# or failure (down) - if the ping stays absent or comes back down, Kuma alerts well before
# the 7-day signature expires.
set -euo pipefail

DEFAULT_URL="https://uptimekuma.home.lan"
read -r -p "Uptime Kuma URL [$DEFAULT_URL]: " KUMA_URL
KUMA_URL=${KUMA_URL:-$DEFAULT_URL}
read -r -p "Username: " KUMA_USER
read -r -s -p "Password: " KUMA_PASS; echo

PUSH_URL=""
if command -v python3 >/dev/null 2>&1; then
    echo "Trying to create the monitor via the API ..."
    python3 -m pip install --user --quiet uptime-kuma-api 2>/dev/null || true
    PUSH_TOKEN=$(python3 - "$KUMA_URL" "$KUMA_USER" "$KUMA_PASS" <<'PY' 2>/dev/null || true
import sys
try:
    from uptime_kuma_api import UptimeKumaApi, MonitorType
    url, user, pw = sys.argv[1:4]
    api = UptimeKumaApi(url, ssl_verify=False, timeout=15)
    api.login(user, pw)
    name = "StudyLife iOS Signing Renewal"
    existing = [m for m in api.get_monitors() if m.get("name") == name]
    if existing:
        token = existing[0]["pushToken"]
    else:
        # 172800s = 2-day heartbeat window: alert only once two scheduled renewals
        # (Mon+Thu 03:30) have failed in a row - well before the 7-day deadline.
        result = api.add_monitor(type=MonitorType.PUSH, name=name,
                                 interval=172800, retryInterval=3600, maxretries=1)
        token = next(m for m in api.get_monitors() if m["id"] == result["monitorID"])["pushToken"]
    api.disconnect()
    print(token)
except Exception as exc:
    print(f"API approach failed: {exc}", file=sys.stderr)
    sys.exit(1)
PY
)
    [ -n "${PUSH_TOKEN:-}" ] && PUSH_URL="$KUMA_URL/api/push/$PUSH_TOKEN"
fi
unset KUMA_PASS

if [ -z "$PUSH_URL" ]; then
    echo
    echo "API approach not possible - do it manually instead:"
    echo "  1) Open $KUMA_URL -> 'New Monitor' -> type 'Push'"
    echo "  2) Name: StudyLife iOS Signing Renewal, heartbeat interval: 172800 seconds"
    echo "  3) Copy the push URL shown and paste it here"
    read -r -p "Push URL: " PUSH_URL
fi

printf '%s' "$PUSH_URL" > "$HOME/.studylife-kuma-push"
chmod 600 "$HOME/.studylife-kuma-push"

echo "Sending test ping ..."
if curl -fsSk --max-time 10 "$PUSH_URL?status=up&msg=setup-test" >/dev/null; then
    echo "Heartbeat set up - Kuma should now show the monitor as green."
else
    echo "WARNING: test ping failed - check the push URL/reachability."
fi
