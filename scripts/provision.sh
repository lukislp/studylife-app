#!/bin/bash
# Generates (or renews) the Personal Team certificate + provisioning profiles for the app
# and widget extension via the dummy project in native/ios-signing and registers the
# iPhone connected via USB. Prerequisite: Apple ID set up once in Xcode
# (Xcode -> Settings -> Accounts). Headless thanks to -allowProvisioningUpdates.
set -euo pipefail
cd "$(dirname "$0")/../native/ios-signing"

# Headless contexts (SSH/launchd): unlock the keychain like in sign-and-install.sh
if [ -f "$HOME/.studylife-sign-pass" ]; then
    security unlock-keychain -p "$(cat "$HOME/.studylife-sign-pass")" "$HOME/Library/Keychains/login.keychain-db" || true
fi

"$HOME/tools/xcodegen/bin/xcodegen" generate

# Read the iPhone UDID in Apple format (8-16 hex, e.g. 00008140-...) from the xcodebuild destinations
DEVICE=$(xcodebuild -project StudyLifeShell.xcodeproj -scheme StudyLifeShell -showdestinations 2>/dev/null \
    | grep "platform:iOS," | grep -Eo 'id:[0-9A-F]{8}-[0-9A-F]{16}' | head -1 | cut -d: -f2)
[ -n "$DEVICE" ] || { echo "No iPhone found on the Mac (USB + trusted?)"; exit 1; }
echo "Device UDID: $DEVICE"

xcodebuild -project StudyLifeShell.xcodeproj -scheme StudyLifeShell \
    -destination "platform=iOS,id=$DEVICE" \
    -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
    build 2>&1 | tail -5

echo "Profiles now present:"
security find-identity -v -p codesigning | head -3
