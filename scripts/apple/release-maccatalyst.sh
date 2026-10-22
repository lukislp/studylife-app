#!/bin/bash
# Builds the Mac Catalyst app once and packages it twice:
#   - StudyLife-macos.pkg: App Sandbox, "Apple Distribution" + Mac App Store profile, signed
#     with the "Mac Installer Distribution" certificate -> TestFlight.
#   - StudyLife-macos.zip: the GitHub release asset. "Developer ID Application" + hardened
#     runtime, notarized and stapled when the Developer ID secrets exist; otherwise ad-hoc
#     signed exactly like the unsigned builds before (Gatekeeper asks once).
#
# Run from the repo root with ./studylife checked out and the csproj reduced to the single
# net10.0-maccatalyst target (see test-maccatalyst / release-macos in ci-cd.yml and
# testflight-macos in apple-testflight.yml).
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
# Release builds for both RuntimeIdentifiers (maccatalyst-x64;maccatalyst-arm64, the MAUI
# default): each RID gets an inner build under <rid>/ with only its thin executable and no
# compiled asset catalog; the outer build merges them into the universal app directly under
# net10.0-maccatalyst/ and compiles the icons (Assets.car, .icns, CFBundleIconName).
# Picking an inner one shipped an x86_64-only app without icons (App Store Connect 90236,
# 90794, 91167).
OUT_DIR="src/StudyLife.App/bin/Release/net10.0-maccatalyst"
echo "App bundles built:"
find "$OUT_DIR" -maxdepth 3 -name "*.app" -type d | sed 's/^/  /'
BUILT_APP="$(find "$OUT_DIR" -mindepth 1 -maxdepth 1 -name "*.app" -type d | head -1)"
[ -n "$BUILT_APP" ] || fail "dotnet publish produced no universal .app directly in $OUT_DIR"
echo "Built: $BUILT_APP"

# The .icns. actool gets the complete mac icon set (Platforms/MacCatalyst/Assets.xcassets,
# 16-512 pt at 1x/2x) and puts every size into Assets.car, but for Mac Catalyst
# (--platform macosx --ui-framework-family uikit) it writes appicon.icns with only the
# 16/32/128/256 px images. App Store Connect requires the 512pt@2x image in the .icns (90236),
# so build it with iconutil from the same PNGs and replace actool's file.
ICONSET_SRC="src/StudyLife.App/Platforms/MacCatalyst/Assets.xcassets/appicon.appiconset"
icon_file="$(plutil -extract CFBundleIconFile raw -o - "$BUILT_APP/Contents/Info.plist" 2>/dev/null || echo appicon)"
icon_file="${icon_file%.icns}"
ICONSET_DIR="$(mktemp -d)/$icon_file.iconset"
mkdir -p "$ICONSET_DIR"
for pt in 16 32 128 256 512; do
  cp "$ICONSET_SRC/mac-$pt@1x.png" "$ICONSET_DIR/icon_${pt}x${pt}.png"
  cp "$ICONSET_SRC/mac-$pt@2x.png" "$ICONSET_DIR/icon_${pt}x${pt}@2x.png"
done
iconutil -c icns -o "$BUILT_APP/Contents/Resources/$icon_file.icns" "$ICONSET_DIR"
plutil -replace CFBundleIconFile -string "$icon_file" "$BUILT_APP/Contents/Info.plist"

# App Store Connect requirements for a Mac Catalyst app, checked before anything is signed:
# an arm64 slice (91167), CFBundleIconName (90794) and an .icns with the 512pt@2x image (90236).
check_mac_bundle() {
  local app="$1" exe archs icon_name
  exe="$app/Contents/MacOS/$(plutil -extract CFBundleExecutable raw -o - "$app/Contents/Info.plist")"
  archs="$(lipo -archs "$exe")"
  icon_name="$(plutil -extract CFBundleIconName raw -o - "$app/Contents/Info.plist" 2>/dev/null || true)"
  echo "Executable architectures: $archs; CFBundleIconName: ${icon_name:-<none>}"
  [[ " $archs " == *" arm64 "* ]] || fail "$exe has no arm64 slice ($archs) - App Store builds must be universal"
  [[ " $archs " == *" x86_64 "* ]] || fail "$exe has no x86_64 slice ($archs) - Intel Macs could not run it"
  [ -n "$icon_name" ] || fail "Info.plist has no CFBundleIconName (app icon not compiled into the asset catalog)"
  [ -f "$app/Contents/Resources/Assets.car" ] || fail "the app has no Assets.car"
  python3 - "$app/Contents/Resources" <<'PY' || fail "no .icns with a 1024x1024 (512pt@2x) image in $app/Contents/Resources"
import glob, os, struct, sys
found = {}
for path in glob.glob(os.path.join(sys.argv[1], "*.icns")):
    data = open(path, "rb").read()
    types, pos = [], 8
    while data[:4] == b"icns" and pos + 8 <= len(data):
        kind, size = struct.unpack(">4sI", data[pos:pos + 8])
        types.append(kind.decode("latin1"))
        if size < 8:
            break
        pos += size
    found[os.path.basename(path)] = types
print("icns files:", found or "none")
# ic10 = 1024x1024 (512x512@2x)
sys.exit(0 if any("ic10" in t for t in found.values()) else 1)
PY
}
check_mac_bundle "$BUILT_APP"

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
  [ "$SIGN_MODE" = app-store ] && echo "::notice::No Developer ID certificate configured - StudyLife-macos.zip is ad-hoc signed (unsigned for Gatekeeper); the release keeps its own ad-hoc signed ZIP."
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
