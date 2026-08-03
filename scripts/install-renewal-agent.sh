#!/bin/bash
# Registers the launchd agent for the automatic 7-day renewal of the free-account
# signature: Monday + Thursday at 03:30, scripts/renew.sh runs (profile refresh + re-
# signing + installation over WiFi). Prerequisites: the Mac is running/awake at night,
# the user is logged in (keychain), the iPhone is on the same WiFi network. Log under
# ~/Library/Logs/studylife-renewal.log; if a run fails (iPhone unreachable), the next
# scheduled run automatically picks up the slack - the Mon<->Thu gap stays under 7 days.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
PLIST="$HOME/Library/LaunchAgents/app.studylife.resign.plist"

mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>app.studylife.resign</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/bash</string>
        <string>$REPO/scripts/renew.sh</string>
    </array>
    <key>StartCalendarInterval</key>
    <array>
        <dict><key>Weekday</key><integer>1</integer><key>Hour</key><integer>3</integer><key>Minute</key><integer>30</integer></dict>
        <dict><key>Weekday</key><integer>4</integer><key>Hour</key><integer>3</integer><key>Minute</key><integer>30</integer></dict>
    </array>
    <key>StandardOutPath</key><string>$HOME/Library/Logs/studylife-renewal.log</string>
    <key>StandardErrorPath</key><string>$HOME/Library/Logs/studylife-renewal.log</string>
</dict>
</plist>
EOF

launchctl unload "$PLIST" 2>/dev/null || true
launchctl load "$PLIST"
echo "Renewal agent registered (Mon + Thu 03:30). Log: ~/Library/Logs/studylife-renewal.log"
