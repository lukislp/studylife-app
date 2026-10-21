#!/bin/bash
# Builds the Mac Catalyst app once and packages it twice:
#   - StudyLife-macos.pkg: App Sandbox, "Apple Distribution" + Mac App Store profile, signed
#     with the "Mac Installer Distribution" certificate -> TestFlight.
#   - StudyLife-macos.zip: the GitHub release asset. "Developer ID Application" + hardened
#     runtime, notarized and stapled when the Developer ID secrets exist; otherwise ad-hoc
#     signed exactly like the unsigned builds before (Gatekeeper asks once).
#
# Run from the repo root with ./studylife checked out and the csproj reduced to the single
# net10.0-maccatalyst target (see test-maccatalyst / release-macos in ci-cd.yml).
#
# Environment:
#   SIGN_MODE        app-store (release) or adhoc (pull requests: ad-hoc signatures with the
#                    real entitlements, unsigned .pkg, no notarization - nothing secret needed)
#   APP_VERSION      x.y.z;  BUILD_NUMBER optional (major*10000+minor*100+patch)
#   app-store only, from scripts/apple/keychain.sh setup:
#   APPLE_TEAM_ID, SIGNING_KEYCHAIN, DISTRIBUTION_IDENTITY, INSTALLER_IDENTITY, PROFILE_DIR,
#   DEVELOPER_ID_IDENTITY (empty = no notarized ZIP), ASC_API_KEY_PATH, ASC_API_KEY_ID,
#   ASC_API_ISSUER_ID
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"

fail() { echo "::error::$*" >&2; exit 1; }

SIGN_MODE="${SIGN_MODE:?SIGN_MODE is app-store or adhoc}"
APP_VERSION="${APP_VERSION:?APP_VERSION is not set}"
BUILD_NUMBER="${BUILD_NUMBER:-$(bash scripts/build-android-apk.sh version-code "$APP_VERSION")}"
SIGNING="python3 $ROOT/scripts/apple/signing.py"
APP_ID="app.studylife.mobile"
PKG_OUTPUT="$ROOT/StudyLife-macos.pkg"
ZIP_OUTPUT="$ROOT/StudyLife-macos.zip"
MAS_ENTITLEMENTS="$ROOT/src/StudyLife.App/Platforms/MacCatalyst/Entitlements.plist"
DEVID_ENTITLEMENTS="$ROOT/scripts/apple/entitlements/maccatalyst-developer-id.plist"

case "$SIGN_MODE" in
  app-store)
    : "${APPLE_TEAM_ID:?}" "${SIGNING_KEYCHAIN:?}" "${DISTRIBUTION_IDENTITY:?}" "${INSTALLER_IDENTITY:?}" "${PROFILE_DIR:?}"
    DEVELOPER_ID_IDENTITY="${DEVELOPER_ID_IDENTITY:-}"
    ;;
  adhoc) DEVELOPER_ID_IDENTITY="" ;;
  *) fail "SIGN_MODE must be app-store or adhoc, not '$SIGN_MODE'" ;;
esac
echo "Mac Catalyst build: version $APP_VERSION, build $BUILD_NUMBER, signing $SIGN_MODE"

# --- 1. One unsigned universal build ---
dotnet publish src/StudyLife.App/StudyLife.App.csproj -c Release \
  -p:ApplicationDisplayVersion="$APP_VERSION" -p:ApplicationVersion="$BUILD_NUMBER" \
  -p:EnableCodeSigning=false -p:CreatePackage=false
BUILT_APP="$(find src/StudyLife.App/bin/Release/net10.0-maccatalyst -maxdepth 4 -name "*.app" -type d | head -1)"
[ -n "$BUILT_APP" ] || fail "dotnet publish produced no .app"
echo "Built: $BUILT_APP"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

copy_app() {
  # copy_app <variant> -> path of a fresh copy (ditto keeps the bundle intact)
  mkdir -p "$WORK/$1"
  ditto "$BUILT_APP" "$WORK/$1/$(basename "$BUILT_APP")"
  echo "$WORK/$1/$(basename "$BUILT_APP")"
}

# sign_app <app> <identity> <entitlements> <hardened: yes|no> [keychain]
# Inside out: every nested Mach-O file (MonoBundle dylibs, frameworks) first, then the app.
sign_app() {
  local app="$1" identity="$2" ents="$3" hardened="$4" keychain="${5:-}"
  local opts="" exe f
  if [ "$hardened" = yes ]; then
    opts="--options runtime"
    # Secure timestamps exist only for real identities (notarization requires one).
    [ "$identity" != "-" ] && opts="$opts --timestamp"
  fi
  [ -n "$keychain" ] && opts="$opts --keychain $keychain"
  exe="$app/Contents/MacOS/$(plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist")"
  find "$app/Contents" -type f ! -path "*/_CodeSignature/*" ! -path "$exe" -print0 \
    | xargs -0 file | { grep "Mach-O" || true; } | cut -d: -f1 | awk '{ print length, $0 }' | sort -rn | cut -d' ' -f2- \
    | while read -r f; do
        # shellcheck disable=SC2086
        codesign -f -s "$identity" $opts "$f"
      done
  find "$app/Contents" -depth -type d \( -name "*.framework" -o -name "*.appex" \) | while read -r f; do
    # shellcheck disable=SC2086
    codesign -f -s "$identity" $opts "$f"
  done
  # shellcheck disable=SC2086
  codesign -f -s "$identity" $opts --entitlements "$ents" "$app"
  codesign --verify --deep --strict --verbose=2 "$app"
}

# smoke_launch <app>: the hardened runtime and the sandbox both kill a misconfigured .NET app
# right at startup (missing JIT entitlement, blocked container) - catch that here.
smoke_launch() {
  local app="$1" exe pid
  exe="$app/Contents/MacOS/$(plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist")"
  "$exe" > "$WORK/launch.log" 2>&1 &
  pid=$!
  sleep 20
  if ! kill -0 "$pid" 2>/dev/null; then
    cat "$WORK/launch.log" >&2 || true
    fail "$(basename "$app") exited within 20 s of launch ($2)"
  fi
  kill "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  echo "Launch check passed ($2)"
}

summary_row() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "| $1 | $2 |" >> "$GITHUB_STEP_SUMMARY" || true; }
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  { echo "### macOS ($SIGN_MODE)"; echo ""; echo "| | |"; echo "| --- | --- |"; } >> "$GITHUB_STEP_SUMMARY"
fi
summary_row "Version / build" "\`$APP_VERSION\` / \`$BUILD_NUMBER\`"

# --- 2. TestFlight / Mac App Store package ---
MAS_APP="$(copy_app appstore)"
plutil -replace ITSAppUsesNonExemptEncryption -bool NO "$MAS_APP/Contents/Info.plist"
if [ "$SIGN_MODE" = app-store ]; then
  PROFILE="$PROFILE_DIR/mac.provisionprofile"
  $SIGNING check-profile "$PROFILE" --bundle-id "$APP_ID" --team "$APPLE_TEAM_ID" --kind app-store --platform maccatalyst
  cp "$PROFILE" "$MAS_APP/Contents/embedded.provisionprofile"
  $SIGNING entitlements --declared "$MAS_ENTITLEMENTS" --profile "$PROFILE" --out "$WORK/mas-ents.plist"
  sign_app "$MAS_APP" "$DISTRIBUTION_IDENTITY" "$WORK/mas-ents.plist" no "$SIGNING_KEYCHAIN"
  productbuild --component "$MAS_APP" /Applications --sign "$INSTALLER_IDENTITY" \
    --keychain "$SIGNING_KEYCHAIN" "$PKG_OUTPUT"
  pkgutil --check-signature "$PKG_OUTPUT"
else
  $SIGNING entitlements --declared "$MAS_ENTITLEMENTS" --out "$WORK/mas-ents.plist"
  sign_app "$MAS_APP" - "$WORK/mas-ents.plist" no
  # An Apple Distribution signature only runs once the App Store re-signs it, so the sandboxed
  # variant can only be launch-tested ad hoc - which is exactly what pull requests do.
  smoke_launch "$MAS_APP" "App Sandbox"
  productbuild --component "$MAS_APP" /Applications "$PKG_OUTPUT"
fi
summary_row "TestFlight package SHA-256" "\`$(shasum -a 256 "$PKG_OUTPUT" | cut -d' ' -f1)\`"

# --- 3. GitHub release ZIP ---
ZIP_APP="$(copy_app direct)"
if [ -n "$DEVELOPER_ID_IDENTITY" ]; then
  : "${ASC_API_KEY_PATH:?}" "${ASC_API_KEY_ID:?}" "${ASC_API_ISSUER_ID:?}"
  sign_app "$ZIP_APP" "$DEVELOPER_ID_IDENTITY" "$DEVID_ENTITLEMENTS" yes "$SIGNING_KEYCHAIN"
  ditto -c -k --keepParent "$ZIP_APP" "$WORK/notarize.zip"
  xcrun notarytool submit "$WORK/notarize.zip" --key "$ASC_API_KEY_PATH" --key-id "$ASC_API_KEY_ID" \
    --issuer "$ASC_API_ISSUER_ID" --wait --timeout 45m --output-format json > "$WORK/notary.json" || true
  cat "$WORK/notary.json"
  status="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("status",""))' "$WORK/notary.json" 2>/dev/null || true)"
  if [ "$status" != "Accepted" ]; then
    sub_id="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id",""))' "$WORK/notary.json" 2>/dev/null || true)"
    [ -n "$sub_id" ] && xcrun notarytool log "$sub_id" --key "$ASC_API_KEY_PATH" \
      --key-id "$ASC_API_KEY_ID" --issuer "$ASC_API_ISSUER_ID" || true
    fail "notarization did not succeed (status '${status:-none}')"
  fi
  xcrun stapler staple "$ZIP_APP"
  xcrun stapler validate "$ZIP_APP"
  spctl -a -vv -t exec "$ZIP_APP" 2>&1 | tee "$WORK/spctl.log"
  grep -q "source=Notarized Developer ID" "$WORK/spctl.log" || fail "Gatekeeper does not accept the app as notarized"
  smoke_launch "$ZIP_APP" "Developer ID, hardened runtime"
  summary_row "StudyLife-macos.zip" "Developer ID, notarized, stapled"
else
  [ "$SIGN_MODE" = app-store ] && echo "::notice::No Developer ID certificate configured - StudyLife-macos.zip is ad-hoc signed (unsigned for Gatekeeper), as before."
  # Ad hoc, with the hardened runtime, so pull requests prove the Developer ID entitlements
  # suffice; Gatekeeper treats the result as unsigned either way.
  sign_app "$ZIP_APP" - "$DEVID_ENTITLEMENTS" yes
  smoke_launch "$ZIP_APP" "ad hoc, hardened runtime"
  summary_row "StudyLife-macos.zip" "ad-hoc signed (not notarized)"
fi
rm -f "$ZIP_OUTPUT"
ditto -c -k --sequesterRsrc --keepParent "$ZIP_APP" "$ZIP_OUTPUT"
summary_row "StudyLife-macos.zip SHA-256" "\`$(shasum -a 256 "$ZIP_OUTPUT" | cut -d' ' -f1)\`"
echo "Done: $PKG_OUTPUT, $ZIP_OUTPUT"
