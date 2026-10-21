#!/bin/bash
# Builds the complete iOS app (the .NET app plus the Xcode-built widget, share and Apple Watch
# bundles) and signs it inside out, either for TestFlight or ad hoc as a pull-request test.
# The Mac pipeline for direct device installs (build-ios-ipa.sh + sign-and-install.sh) stays
# as it is; this script reuses build-ios-ipa.sh and the same xcodegen project.
#
# Run from the repo root with ./studylife checked out, the csproj reduced to the single
# net10.0-ios target and XcodeGen installed (see test-ios in ci-cd.yml and testflight-ios in
# apple-testflight.yml).
#
# Environment:
#   SIGN_MODE        app-store (TestFlight) or adhoc (pull requests, nothing secret needed)
#   APP_VERSION      x.y.z -> CFBundleShortVersionString
#   BUILD_NUMBER     CFBundleVersion, optional: major*10000+minor*100+patch, like Android
#   IPA_OUTPUT       output path (default StudyLife-ios.ipa)
#   XCODEGEN         xcodegen binary (default ~/tools/xcodegen/bin/xcodegen)
#   app-store only, from scripts/apple/keychain.sh setup:
#   APPLE_TEAM_ID, SIGNING_KEYCHAIN, DISTRIBUTION_IDENTITY, PROFILE_DIR
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"

fail() { echo "::error::$*" >&2; exit 1; }

SIGN_MODE="${SIGN_MODE:?SIGN_MODE is app-store or adhoc}"
APP_VERSION="${APP_VERSION:?APP_VERSION is not set}"
BUILD_NUMBER="${BUILD_NUMBER:-$(bash scripts/build-android-apk.sh version-code "$APP_VERSION")}"
IPA_OUTPUT="${IPA_OUTPUT:-StudyLife-ios.ipa}"
XCODEGEN="${XCODEGEN:-$HOME/tools/xcodegen/bin/xcodegen}"
SIGNING="python3 $ROOT/scripts/apple/signing.py"
APP_ID="app.studylife.mobile"

case "$SIGN_MODE" in
  app-store)
    : "${APPLE_TEAM_ID:?}" "${SIGNING_KEYCHAIN:?}" "${DISTRIBUTION_IDENTITY:?}" "${PROFILE_DIR:?}"
    IDENTITY="$DISTRIBUTION_IDENTITY"
    KEYCHAIN_ARGS="--keychain $SIGNING_KEYCHAIN"
    TEAM="$APPLE_TEAM_ID"
    ;;
  adhoc)
    IDENTITY="-"
    KEYCHAIN_ARGS=""
    TEAM=""
    ;;
  *) fail "SIGN_MODE must be app-store or adhoc, not '$SIGN_MODE'" ;;
esac
echo "iOS build: version $APP_VERSION, build $BUILD_NUMBER, signing $SIGN_MODE"

# --- 1. .NET app (Live Activity bridge, AppIntents metadata) as an unsigned IPA ---
IOS_PUBLISH_EXTRA_ARGS="-p:ApplicationDisplayVersion=$APP_VERSION -p:ApplicationVersion=$BUILD_NUMBER" \
  bash scripts/build-ios-ipa.sh
IPA="src/StudyLife.App/bin/Release/net10.0-ios/ios-arm64/publish/StudyLife.App.ipa"
[ -f "$IPA" ] || fail "build-ios-ipa.sh produced no $IPA"

# --- 2. Widget, share and watch bundles with Xcode (same project as sign-and-install.sh) ---
(cd native/ios-signing \
  && "$XCODEGEN" generate > /dev/null \
  && xcodebuild -project StudyLifeShell.xcodeproj -scheme StudyLifeShell -configuration Release \
      -destination "generic/platform=iOS" -derivedDataPath build \
      MARKETING_VERSION="$APP_VERSION" CURRENT_PROJECT_VERSION="$BUILD_NUMBER" \
      DEVELOPMENT_TEAM="$TEAM" CODE_SIGNING_ALLOWED=NO build -quiet)
SHELL_APP="native/ios-signing/build/Build/Products/Release-iphoneos/StudyLifeShell.app"
[ -d "$SHELL_APP/PlugIns" ] || fail "xcodebuild produced no app extensions"
[ -d "$SHELL_APP/Watch" ] || fail "xcodebuild produced no Watch app"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
unzip -q "$IPA" -d "$WORK"
APP="$WORK/Payload/StudyLife.App.app"
rm -rf "$APP/PlugIns" "$APP/Watch"
cp -R "$SHELL_APP/PlugIns" "$APP/PlugIns"
cp -R "$SHELL_APP/Watch" "$APP/Watch"

# --- 3. One version everywhere: App Store Connect rejects embedded bundles whose
# CFBundleVersion/CFBundleShortVersionString differ from the app's. ---
set_versions() {
  plutil -replace CFBundleShortVersionString -string "$APP_VERSION" "$1/Info.plist"
  plutil -replace CFBundleVersion -string "$BUILD_NUMBER" "$1/Info.plist"
}
bundles_inside_out() {
  # deepest first: watch extensions, watch app, phone extensions
  find "$APP/Watch" -name "*.appex" -type d -prune
  find "$APP/Watch" -maxdepth 1 -name "*.app" -type d
  find "$APP/PlugIns" -maxdepth 1 -name "*.appex" -type d
}
for b in $(bundles_inside_out) "$APP"; do set_versions "$b"; done
if [ "$SIGN_MODE" = app-store ]; then
  # Standard HTTPS only (no own cryptography) -> exempt; saves the export-compliance question
  # on every TestFlight build. Also in Platforms/iOS/Info.plist; enforced here for the bundles.
  plutil -replace ITSAppUsesNonExemptEncryption -bool NO "$APP/Info.plist"
fi

declared_for() {
  case "$1" in
    "$APP_ID") echo "$ROOT/scripts/apple/entitlements/ios-app.plist" ;;
    "$APP_ID.widgets" | "$APP_ID.share" | "$APP_ID.watchkitapp" | "$APP_ID.watchkitapp.widgets")
      echo "$ROOT/scripts/apple/entitlements/ios-extension.plist" ;;
    *) fail "no entitlements declared for bundle $1 (scripts/apple/entitlements)" ;;
  esac
}

# --- 4. Sign inside out ---
sign_bundle() {
  local bundle="$1" id ents="$WORK/ents-$RANDOM.plist" profile=""
  id="$(plutil -extract CFBundleIdentifier raw -o - "$bundle/Info.plist")"
  if [ "$SIGN_MODE" = app-store ]; then
    profile="$($SIGNING find-profile "$PROFILE_DIR" --bundle-id "$id" --team "$TEAM")" || exit 1
    $SIGNING check-profile "$profile" --bundle-id "$id" --team "$TEAM" --kind app-store --platform ios
    cp "$profile" "$bundle/embedded.mobileprovision"
    $SIGNING entitlements --declared "$(declared_for "$id")" --profile "$profile" --out "$ents"
  else
    $SIGNING entitlements --declared "$(declared_for "$id")" --out "$ents"
  fi
  # Nested dylibs/frameworks first (the .NET app may carry some; Xcode bundles usually none).
  if [ -d "$bundle/Frameworks" ]; then
    find "$bundle/Frameworks" -maxdepth 1 \( -name "*.dylib" -o -name "*.framework" \) | while read -r f; do
      # shellcheck disable=SC2086
      codesign -f -s "$IDENTITY" $KEYCHAIN_ARGS "$f"
    done
  fi
  # shellcheck disable=SC2086
  codesign -f -s "$IDENTITY" $KEYCHAIN_ARGS --entitlements "$ents" "$bundle"
  echo "Signed $id"
}
for b in $(bundles_inside_out); do sign_bundle "$b"; done
sign_bundle "$APP"

codesign --verify --deep --strict --verbose=2 "$APP"
if [ "$SIGN_MODE" = app-store ]; then
  codesign -d --entitlements - --xml "$APP" > "$WORK/final-ents.plist" 2>/dev/null
  plutil -extract get-task-allow raw -o - "$WORK/final-ents.plist" | grep -qx false \
    || fail "the app is signed with get-task-allow - not an App Store signature"
fi

rm -f "$ROOT/$IPA_OUTPUT"
(cd "$WORK" && zip -qry "$ROOT/$IPA_OUTPUT" Payload)
echo "IPA: $IPA_OUTPUT ($(du -h "$ROOT/$IPA_OUTPUT" | cut -f1))"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### iOS ($SIGN_MODE)"
    echo ""
    echo "| | |"
    echo "| --- | --- |"
    echo "| CFBundleShortVersionString | \`$APP_VERSION\` |"
    echo "| CFBundleVersion | \`$BUILD_NUMBER\` |"
    echo "| Bundles | $(bundles_inside_out | wc -l | tr -d ' ') embedded + app |"
    echo "| IPA SHA-256 | \`$(shasum -a 256 "$ROOT/$IPA_OUTPUT" | cut -d' ' -f1)\` |"
  } >> "$GITHUB_STEP_SUMMARY"
fi
