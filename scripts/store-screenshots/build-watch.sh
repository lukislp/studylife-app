#!/usr/bin/env bash
# Builds the Apple Watch app for the watchOS Simulator for the store screenshots (README "App Store screenshots").
#
# Same XcodeGen project as the release build (native/ios-signing/project.yml, scripts/apple/release-ios.sh), but
# only the Watch app target (and the complication extension it embeds), for the simulator and signed ad hoc
# ("sign to run locally"): no team, no profile, no secret. Ad-hoc signing keeps the target's entitlements, so the
# App Group container the Watch app reads its snapshot from exists in the simulator.
#
# Environment: XCODEGEN (xcodegen binary, scripts/apple/install-xcodegen.sh). Prints the .app path.
set -euo pipefail

: "${XCODEGEN:?xcodegen binary (scripts/apple/install-xcodegen.sh)}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT_DIR="$ROOT/native/ios-signing"
BUILD_DIR="$PROJECT_DIR/build-watchsimulator"

(cd "$PROJECT_DIR" && "$XCODEGEN" generate >/dev/null)
xcodebuild -project "$PROJECT_DIR/StudyLifeShell.xcodeproj" -target StudyLifeWatchShell -configuration Release \
  -sdk watchsimulator ARCHS=arm64 ONLY_ACTIVE_ARCH=NO SYMROOT="$BUILD_DIR" \
  DEVELOPMENT_TEAM= CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY=- PROVISIONING_PROFILE_SPECIFIER= \
  build -quiet >&2

app="$BUILD_DIR/Release-watchsimulator/StudyLifeWatchShell.app"
[ -d "$app" ] || { echo "::error::xcodebuild produced no Watch app at $app" >&2; exit 1; }
echo "$app"
