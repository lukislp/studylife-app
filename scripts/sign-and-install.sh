#!/bin/bash
# Option B - self-signing (instead of Sideloadly, whose signer doesn't set the
# CS_EXECSEG_MAIN_BINARY flag for app extensions -> AMFI kills the extension with "has entitlements
# but is not a main binary"). Prerequisites, one-time via native/ios-signing:
#   1) Apple ID set up in Xcode (Xcode -> Settings -> Accounts)
#   2) xcodebuild -allowProvisioningUpdates has generated the certificate + profiles for
#      app.studylife.mobile and app.studylife.mobile.widgets (scripts/provision.sh)
# After that: correctly signs the (unsigned) IPA built by build-ios-ipa.sh, including the
# extension, and installs it via devicectl on the iPhone connected to the Mac via USB.
# 7-day renewal = simply run this script again.
set -euo pipefail
cd "$(dirname "$0")/.."

IPA="src/StudyLife.App/bin/Release/net10.0-ios/ios-arm64/publish/StudyLife.App.ipa"
APP_ID="app.studylife.mobile"

[ -f "$IPA" ] || { echo "IPA missing - run scripts/build-ios-ipa.sh first"; exit 1; }

# --- Unlock the keychain (SSH sessions have their own, locked security session).
# The password lives ONLY locally on the Mac in ~/.studylife-sign-pass (chmod 600, created
# by the owner themselves) - standard CI pattern, it never leaves the Mac. ---
if [ -f "$HOME/.studylife-sign-pass" ]; then
    security unlock-keychain -p "$(cat "$HOME/.studylife-sign-pass")" "$HOME/Library/Keychains/login.keychain-db"
    echo "Keychain unlocked."
fi

# --- Signing identity (Personal Team) ---
IDENTITY=$(security find-identity -v -p codesigning | awk -F'"' '/Apple Development/ {print $2; exit}')
[ -n "$IDENTITY" ] || { echo "No 'Apple Development' identity in the keychain - run scripts/provision.sh"; exit 1; }
echo "Identity: $IDENTITY"

# --- Search provisioning profiles by application-identifier (newest wins) ---
find_profile() {
    local wanted="$1" best="" best_exp=""
    for dir in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" \
               "$HOME/Library/MobileDevice/Provisioning Profiles"; do
        [ -d "$dir" ] || continue
        for p in "$dir"/*.mobileprovision; do
            [ -f "$p" ] || continue
            local plist appid exp
            plist=$(security cms -D -i "$p" 2>/dev/null) || continue
            appid=$(echo "$plist" | plutil -extract Entitlements.application-identifier raw -o - - 2>/dev/null) || continue
            [ "$appid" = "$wanted" ] || continue
            exp=$(echo "$plist" | plutil -extract ExpirationDate raw -o - - 2>/dev/null) || exp=""
            if [ -z "$best" ] || [[ "$exp" > "$best_exp" ]]; then best="$p"; best_exp="$exp"; fi
        done
    done
    echo "$best"
}

TEAM=$(security find-certificate -c "$IDENTITY" -p | openssl x509 -noout -subject 2>/dev/null | sed -n 's/.*OU *= *\([A-Z0-9]*\).*/\1/p')
[ -n "$TEAM" ] || TEAM="YOUR_TEAM_ID"
echo "Team: $TEAM"

APP_PROFILE=$(find_profile "$TEAM.$APP_ID")
[ -n "$APP_PROFILE" ] || { echo "No profile for $TEAM.$APP_ID - run scripts/provision.sh"; exit 1; }
echo "App profile: $APP_PROFILE"

# --- Unpack IPA ---
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
unzip -q "$IPA" -d "$WORK"
APP="$WORK/Payload/StudyLife.App.app"

# --- Build all app extensions (Widget + Share) freshly with XCODE and replace the ones
# from the IPA: hand-linked binaries were rejected by AMFI; the xcodebuild variant uses
# Apple's exact linker setup for app extensions. CODE_SIGNING_ALLOWED=NO, signing happens below.
echo "Building app extensions with xcodebuild ..."
(cd native/ios-signing \
    && "$HOME/tools/xcodegen/bin/xcodegen" generate > /dev/null \
    && xcodebuild -project StudyLifeShell.xcodeproj -scheme StudyLifeShell -configuration Release \
        -destination "generic/platform=iOS" -derivedDataPath build \
        CODE_SIGNING_ALLOWED=NO build > /dev/null)
XCODE_PLUGINS="native/ios-signing/build/Build/Products/Release-iphoneos/StudyLifeShell.app/PlugIns"
[ -d "$XCODE_PLUGINS" ] || { echo "xcodebuild did not produce any extensions ($XCODE_PLUGINS missing)"; exit 1; }
rm -rf "$APP/PlugIns"
mkdir -p "$APP/PlugIns"
cp -R "$XCODE_PLUGINS/." "$APP/PlugIns/"

# Watch companion app: Apple embeds it under Watch/ (confirmed via a real build), NOT
# PlugIns/ like an .appex - a separate harvest+sign path from the one above.
XCODE_WATCH="native/ios-signing/build/Build/Products/Release-iphoneos/StudyLifeShell.app/Watch"
if [ -d "$XCODE_WATCH" ]; then
    rm -rf "$APP/Watch"
    mkdir -p "$APP/Watch"
    cp -R "$XCODE_WATCH/." "$APP/Watch/"
else
    echo "No Watch directory produced - skipping the Watch app"
fi

extract_entitlements() { security cms -D -i "$1" | plutil -extract Entitlements xml1 -o "$2" -; }

extract_entitlements "$APP_PROFILE" "$WORK/ent-app.plist"

# IMPORTANT: Apple NEVER embeds the real associated-domains value into the downloaded
# profile - it always just contains the placeholder "*" (capability enabled, no statement
# about which domain). Without this patch, the app would effectively be signed with
# associated-domains=["*"] instead of the real domain - the native passkey dialog would
# then not work despite a "correct" profile. The real domain comes from our own local
# source (project.yml/Entitlements.PaidSigning.plist), not from Apple.
# Dots in the keypath MUST be escaped (plutil interprets ".") otherwise it looks for
# "Entitlements.com.apple.developer.associated-domains" as a nested path instead of the
# one (dot-containing) top-level key - failed in production with "Key path not found".
plutil -replace com\\.apple\\.developer\\.associated-domains \
    -json '["webcredentials:studylife.example.com"]' \
    "$WORK/ent-app.plist" \
    || plutil -insert com\\.apple\\.developer\\.associated-domains \
        -json '["webcredentials:studylife.example.com"]' \
        "$WORK/ent-app.plist"

# Sign each extension with the profile matching its own bundle id (provision.sh
# automatically creates the profiles for all extension ids declared in project.yml).
APPEX_COUNT=0
for APPEX in "$APP/PlugIns"/*.appex; do
    [ -d "$APPEX" ] || continue
    APPEX_ID=$(plutil -extract CFBundleIdentifier raw -o - "$APPEX/Info.plist")
    APPEX_PROFILE=$(find_profile "$TEAM.$APPEX_ID")
    [ -n "$APPEX_PROFILE" ] || { echo "No profile for $TEAM.$APPEX_ID - run scripts/provision.sh"; exit 1; }
    cp "$APPEX_PROFILE" "$APPEX/embedded.mobileprovision"
    extract_entitlements "$APPEX_PROFILE" "$WORK/ent-appex-$APPEX_COUNT.plist"
    codesign -f -s "$IDENTITY" --entitlements "$WORK/ent-appex-$APPEX_COUNT.plist" "$APPEX"
    echo "Extension signed: $APPEX_ID"
    APPEX_COUNT=$((APPEX_COUNT + 1))
done
[ "$APPEX_COUNT" -gt 0 ] || { echo "WARNING: no extensions in the IPA (did build-ios-ipa.sh run?)"; }

# Sign the Watch app the same way (own bundle id -> own profile, find_profile is generic).
# CONFIRMED (live test): embedding it under Watch/ does NOT auto-propagate to the paired
# Watch via the phone install below - devicectl "device info apps" on the Watch stayed
# empty after a plain phone install. A separate, explicit devicectl install targeting the
# Watch's own device id (see below, after the phone install) is required.
WATCH_APP_PATH=""
for WATCHAPP in "$APP/Watch"/*.app; do
    [ -d "$WATCHAPP" ] || continue

    # Nested complication extension (Watch/StudyLifeWatchShell.app/PlugIns/*.appex) - same
    # rule as any app-extension: sign it with its OWN profile BEFORE signing the containing
    # (watch) app, exactly like the phone's own PlugIns loop above.
    WATCH_APPEX_COUNT=0
    for WATCHAPPEX in "$WATCHAPP/PlugIns"/*.appex; do
        [ -d "$WATCHAPPEX" ] || continue
        WATCHAPPEX_ID=$(plutil -extract CFBundleIdentifier raw -o - "$WATCHAPPEX/Info.plist")
        WATCHAPPEX_PROFILE=$(find_profile "$TEAM.$WATCHAPPEX_ID")
        [ -n "$WATCHAPPEX_PROFILE" ] || { echo "No profile for $TEAM.$WATCHAPPEX_ID - run scripts/provision.sh"; exit 1; }
        cp "$WATCHAPPEX_PROFILE" "$WATCHAPPEX/embedded.mobileprovision"
        extract_entitlements "$WATCHAPPEX_PROFILE" "$WORK/ent-watchappex-$WATCH_APPEX_COUNT.plist"
        codesign -f -s "$IDENTITY" --entitlements "$WORK/ent-watchappex-$WATCH_APPEX_COUNT.plist" "$WATCHAPPEX"
        echo "Watch extension signed: $WATCHAPPEX_ID"
        WATCH_APPEX_COUNT=$((WATCH_APPEX_COUNT + 1))
    done

    WATCHAPP_ID=$(plutil -extract CFBundleIdentifier raw -o - "$WATCHAPP/Info.plist")
    WATCHAPP_PROFILE=$(find_profile "$TEAM.$WATCHAPP_ID")
    [ -n "$WATCHAPP_PROFILE" ] || { echo "No profile for $TEAM.$WATCHAPP_ID - run scripts/provision.sh"; exit 1; }
    cp "$WATCHAPP_PROFILE" "$WATCHAPP/embedded.mobileprovision"
    extract_entitlements "$WATCHAPP_PROFILE" "$WORK/ent-watch.plist"
    codesign -f -s "$IDENTITY" --entitlements "$WORK/ent-watch.plist" "$WATCHAPP"
    echo "Watch app signed: $WATCHAPP_ID"
    WATCH_APP_PATH="$WATCHAPP"
done

# Sign any bundled dylibs/frameworks before the main app (MAUI apps usually have
# none - directory guard, otherwise find would abort the script via pipefail)
if [ -d "$APP/Frameworks" ]; then
    find "$APP/Frameworks" -maxdepth 1 \( -name "*.dylib" -o -name "*.framework" \) | while read -r f; do
        codesign -f -s "$IDENTITY" "$f"
    done
fi

cp "$APP_PROFILE" "$APP/embedded.mobileprovision"
codesign -f -s "$IDENTITY" --entitlements "$WORK/ent-app.plist" "$APP"
echo "App signed."

codesign --verify --deep --strict "$APP" && echo "Signature verification OK"

# --- Install onto the iPhone (USB or WiFi after pairing). Explicitly filter for
# iPhone lines - otherwise the Apple Watch appears first in the devicectl list. ---
DEVICE=$(xcrun devicectl list devices 2>/dev/null | grep "iPhone" | grep -v "Watch" \
    | grep -Eo '[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}' | head -1)
[ -n "$DEVICE" ] || { echo "No iPhone found (USB/Wi-Fi + trusted?)"; exit 1; }
echo "Installing onto device $DEVICE ..."
xcrun devicectl device install app --device "$DEVICE" "$APP"
echo "DONE - app installed, including correctly signed extension."

# Watch app: separate install call against the Watch's own device id (see comment above -
# does NOT come along automatically with the phone install). Needs the Watch to be unlocked/
# active, on the same network as the Mac, and Developer Mode enabled on the Watch itself
# (Settings -> Privacy & Security -> Developer Mode) - all one-time prerequisites, same
# category as the iPhone's own Developer Mode/pairing requirements.
if [ -n "$WATCH_APP_PATH" ]; then
    WATCH_DEVICE=$(xcrun devicectl list devices 2>/dev/null | grep "Watch" \
        | grep -Eo '[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}' | head -1)
    if [ -n "$WATCH_DEVICE" ]; then
        echo "Installing Watch app onto device $WATCH_DEVICE ..."
        xcrun devicectl device install app --device "$WATCH_DEVICE" "$WATCH_APP_PATH" \
            && echo "Watch app installed." \
            || echo "WARNING: Watch app installation failed (Watch unlocked/reachable/Developer Mode on?)"
    else
        echo "WARNING: no Apple Watch found in devicectl - Watch app not installed"
    fi
fi
