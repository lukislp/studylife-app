#!/bin/bash
# Complete iOS build on the Mac: Live Activity native parts + unsigned IPA including
# the embedded widget extension. Sign the result afterwards with Sideloadly
# (do NOT check "Remove App Extensions", otherwise the Live Activity gets stripped again).
set -euo pipefail
cd "$(dirname "$0")/.."

bash native/ios-liveactivity/build.sh

export PATH="$HOME/.dotnet:$PATH"
cd src/StudyLife.App
dotnet publish -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -p:EnableCodeSigning=false

IPA="bin/Release/net10.0-ios/ios-arm64/publish/StudyLife.App.ipa"

# Open the IPA for metadata injection; the widget extension is only added later in
# sign-and-install.sh (Xcode-built, see the comment there).
WORK=$(mktemp -d)
unzip -q "$IPA" -d "$WORK"

# Generate AppIntents metadata for the Live Activity's pause button: the runtime only
# finds intents via Payload/App.app/Metadata.appintents, which is normally written
# exclusively by Xcode during an app build - reproduced here via the const-value route
# (same approach as Bazel's rules_apple; const values come from native/ios-liveactivity/build.sh).
NATIVE_DIR="$(cd ../../native/ios-liveactivity && pwd)"
XCODE_BUILD_VERSION=$(xcodebuild -version | awk '/Build version/ {print $3}')
SFL=$(mktemp); printf '%s\n' \
    "$NATIVE_DIR/TimerActivityAttributes.swift" \
    "$NATIVE_DIR/TimerControlIntent.swift" \
    "$NATIVE_DIR/LiveActivityBridge.swift" > "$SFL"
SCV=$(mktemp); printf '%s\n' "$NATIVE_DIR/out/StudyLifeLiveActivity.swiftconstvalues" > "$SCV"
xcrun appintentsmetadataprocessor \
    --output "$WORK/Payload/StudyLife.App.app" \
    --toolchain-dir "$(dirname "$(dirname "$(dirname "$(xcrun --find swiftc)")")")" \
    --module-name StudyLifeLiveActivity \
    --sdk-root "$(xcrun --sdk iphoneos --show-sdk-path)" \
    --xcode-version "$XCODE_BUILD_VERSION" \
    --platform-family iOS \
    --deployment-target 16.2 \
    --target-triple arm64-apple-ios16.2 \
    --source-file-list "$SFL" \
    --swift-const-vals-list "$SCV" \
    --force --quiet-warnings
[ -d "$WORK/Payload/StudyLife.App.app/Metadata.appintents" ] \
    && echo "AppIntents metadata generated." \
    || echo "WARNING: Metadata.appintents missing - the pause button will not work"
rm -f "$SFL" "$SCV"

rm "$IPA"
(cd "$WORK" && zip -qry "$OLDPWD/$IPA" Payload)
rm -rf "$WORK"

echo "IPA with Live Activity extension: src/StudyLife.App/$IPA"
