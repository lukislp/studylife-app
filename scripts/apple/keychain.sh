#!/bin/bash
# Signing material for the TestFlight jobs (.github/workflows/apple-testflight.yml: testflight-ios,
# testflight-macos). Everything lands in one throwaway directory under RUNNER_TEMP and in a
# temporary keychain; "cleanup" removes both and runs in an always() step.
#
#   scripts/apple/keychain.sh check  <ios|macos>   fail if a required secret is empty
#   scripts/apple/keychain.sh setup  <ios|macos>   keychain + certificates + profiles + API key
#   scripts/apple/keychain.sh cleanup
#
# Secrets (environment "release", see the README, "Apple release signing"):
#   APPLE_TEAM_ID                       10-character team id
#   APPLE_DISTRIBUTION_P12_BASE64       "Apple Distribution" certificate + key, .p12, base64
#   APPLE_DISTRIBUTION_P12_PASSWORD
#   APPLE_MAC_INSTALLER_P12_BASE64      "Mac Installer Distribution" (macos only)
#   APPLE_MAC_INSTALLER_P12_PASSWORD
#   APPLE_DEVELOPER_ID_P12_BASE64       "Developer ID Application" (macos, OPTIONAL: without it
#   APPLE_DEVELOPER_ID_P12_PASSWORD       no notarized StudyLife-macos.zip, TestFlight still runs)
#   APPLE_PROFILE_IOS_BASE64            App Store profiles, base64 (ios only) for
#   APPLE_PROFILE_IOS_WIDGETS_BASE64      app.studylife.mobile, .widgets, .share,
#   APPLE_PROFILE_IOS_SHARE_BASE64        .watchkitapp and .watchkitapp.widgets
#   APPLE_PROFILE_WATCH_BASE64
#   APPLE_PROFILE_WATCH_WIDGETS_BASE64
#   APPLE_PROFILE_MAC_BASE64            Mac Catalyst App Store profile (macos only)
#   ASC_API_KEY_P8_BASE64               App Store Connect API key (.p8), base64
#   ASC_API_KEY_ID
#   ASC_API_ISSUER_ID
#
# "setup" appends to GITHUB_ENV: SIGNING_KEYCHAIN, SIGNING_DIR, PROFILE_DIR, ASC_API_KEY_PATH,
# API_PRIVATE_KEYS_DIR (read by altool), and per platform DISTRIBUTION_IDENTITY,
# INSTALLER_IDENTITY, DEVELOPER_ID_IDENTITY (SHA-1 hashes / names of the imported identities;
# DEVELOPER_ID_IDENTITY stays empty without the optional Developer ID secrets).
#
# The .p12 files must use the legacy encryption macOS understands: export them with
# `openssl pkcs12 -export -legacy ...` (OpenSSL 3) or from Keychain Access.
set -euo pipefail

# Apple's intermediate certificates, pinned by SHA-256 (downloaded from
# https://www.apple.com/certificateauthority/ on 2026-10-21). A .p12 exported from Keychain
# Access carries only the leaf certificate; codesign needs the chain.
WWDR_G3_SHA256=dcf21878c77f4198e4b4614f03d696d89c66c66008d4244e1b99161aac91601f   # valid to 2030-02-20
DEVID_G2_SHA256=f16cd3c54c7f83cea4bf1a3e6a0819c8aaa8e4a1528fd144715f350643d2df3a  # valid to 2031-09-17

fail() { echo "::error::$*" >&2; exit 1; }

required_secrets() {
  local common="APPLE_TEAM_ID APPLE_DISTRIBUTION_P12_BASE64 APPLE_DISTRIBUTION_P12_PASSWORD ASC_API_KEY_P8_BASE64 ASC_API_KEY_ID ASC_API_ISSUER_ID"
  case "$1" in
    ios) echo "$common APPLE_PROFILE_IOS_BASE64 APPLE_PROFILE_IOS_WIDGETS_BASE64 APPLE_PROFILE_IOS_SHARE_BASE64 APPLE_PROFILE_WATCH_BASE64 APPLE_PROFILE_WATCH_WIDGETS_BASE64" ;;
    macos) echo "$common APPLE_MAC_INSTALLER_P12_BASE64 APPLE_MAC_INSTALLER_P12_PASSWORD APPLE_PROFILE_MAC_BASE64" ;;
    *) fail "unknown platform '$1' (ios or macos)" ;;
  esac
}

check() {
  local missing="" name
  for name in $(required_secrets "$1"); do
    [ -n "${!name:-}" ] || missing="$missing $name"
  done
  if [ -n "$missing" ]; then
    fail "Apple signing secrets missing in the 'release' environment:$missing. No unsigned $1 build is ever published (README, \"Apple release signing\")."
  fi
  [[ "$APPLE_TEAM_ID" =~ ^[A-Z0-9]{10}$ ]] || fail "APPLE_TEAM_ID must be the 10-character team id."
  echo "All $1 signing secrets are set."
  if [ "$1" = macos ]; then
    if developer_id_configured; then
      echo "Developer ID secrets are set: StudyLife-macos.zip will be signed and notarized."
    elif [ -n "${APPLE_DEVELOPER_ID_P12_BASE64:-}${APPLE_DEVELOPER_ID_P12_PASSWORD:-}" ]; then
      fail "Only one of APPLE_DEVELOPER_ID_P12_BASE64 / APPLE_DEVELOPER_ID_P12_PASSWORD is set - set both or neither."
    else
      echo "::notice::No Developer ID secrets: no notarized StudyLife-macos.zip (the release keeps its ad-hoc signed one), TestFlight upload is unaffected."
    fi
  fi
}

developer_id_configured() {
  [ -n "${APPLE_DEVELOPER_ID_P12_BASE64:-}" ] && [ -n "${APPLE_DEVELOPER_ID_P12_PASSWORD:-}" ]
}

export_env() {
  echo "$1=$2" >> "${GITHUB_ENV:-/dev/null}"
  export "$1=$2"
}

decode() {
  # decode <env var name> <output file>
  local value="${!1}"
  printf '%s' "$value" | base64 --decode > "$2" 2>/dev/null || fail "$1 is not valid base64."
  [ -s "$2" ] || fail "$1 decodes to an empty file."
}

fetch_ca() {
  local name="$1" sha="$2" out="$SIGNING_DIR/$1"
  curl -fsSL --retry 3 -o "$out" "https://www.apple.com/certificateauthority/$name" \
    || fail "download of Apple's $name failed"
  [ "$(shasum -a 256 "$out" | cut -d' ' -f1)" = "$sha" ] || fail "$name does not match its pinned SHA-256"
  security import "$out" -k "$SIGNING_KEYCHAIN" > /dev/null
}

import_p12() {
  # import_p12 <base64 var> <password var>
  local file="$SIGNING_DIR/$1.p12" password="${!2}"
  decode "$1" "$file"
  security import "$file" -k "$SIGNING_KEYCHAIN" -f pkcs12 -P "$password" \
    -T /usr/bin/codesign -T /usr/bin/productbuild -T /usr/bin/pkgbuild -T /usr/bin/security > /dev/null \
    || fail "$1 could not be imported - wrong $2, not a .p12 with the private key, or AES-encrypted (export with 'openssl pkcs12 -export -legacy')?"
  rm -f "$file"
}

identity() {
  # identity <codesigning|basic> <name prefix ...> -> prints "<sha1> <name>" of the first match
  local policy="$1"; shift
  local list prefix line
  if [ "$policy" = codesigning ]; then
    list="$(security find-identity -v -p codesigning "$SIGNING_KEYCHAIN")"
  else
    list="$(security find-identity -v "$SIGNING_KEYCHAIN")"
  fi
  for prefix in "$@"; do
    line="$(printf '%s\n' "$list" | grep -F "\"$prefix" | grep -F "($APPLE_TEAM_ID)\"" | head -1 || true)"
    if [ -n "$line" ]; then
      printf '%s\n' "$line" | sed -E 's/^ *[0-9]+\) ([0-9A-F]{40}) "(.*)"$/\1 \2/'
      return 0
    fi
  done
  echo "Identities in the signing keychain:" >&2
  printf '%s\n' "$list" >&2
  fail "no valid '$*' identity for team $APPLE_TEAM_ID in the imported certificates"
}

install_profile() {
  # install_profile <base64 var> <file name>
  decode "$1" "$PROFILE_DIR/$2"
}

setup() {
  local platform="$1"
  check "$platform"

  export_env SIGNING_DIR "$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/studylife-signing.XXXXXX")"
  export_env PROFILE_DIR "$SIGNING_DIR/profiles"
  mkdir -p "$PROFILE_DIR" "$SIGNING_DIR/private_keys"
  chmod 700 "$SIGNING_DIR"

  export_env SIGNING_KEYCHAIN "$SIGNING_DIR/signing.keychain-db"
  local kc_password
  kc_password="$(openssl rand -hex 24)"
  security create-keychain -p "$kc_password" "$SIGNING_KEYCHAIN"
  security set-keychain-settings -lut 7200 "$SIGNING_KEYCHAIN"
  security unlock-keychain -p "$kc_password" "$SIGNING_KEYCHAIN"
  # Put it first in the search list so codesign finds the chain; the login keychain stays.
  # shellcheck disable=SC2046
  security list-keychains -d user -s "$SIGNING_KEYCHAIN" $(security list-keychains -d user | tr -d '"')

  fetch_ca AppleWWDRCAG3.cer "$WWDR_G3_SHA256"
  import_p12 APPLE_DISTRIBUTION_P12_BASE64 APPLE_DISTRIBUTION_P12_PASSWORD
  if [ "$platform" = macos ]; then
    import_p12 APPLE_MAC_INSTALLER_P12_BASE64 APPLE_MAC_INSTALLER_P12_PASSWORD
    if developer_id_configured; then
      fetch_ca DeveloperIDG2CA.cer "$DEVID_G2_SHA256"
      import_p12 APPLE_DEVELOPER_ID_P12_BASE64 APPLE_DEVELOPER_ID_P12_PASSWORD
    fi
  fi
  # Lets codesign/productbuild use the keys without a UI prompt.
  security set-key-partition-list -S apple-tool:,apple:,codesign:,productbuild: -s -k "$kc_password" "$SIGNING_KEYCHAIN" > /dev/null

  local dist
  dist="$(identity codesigning "Apple Distribution: ")"
  export_env DISTRIBUTION_IDENTITY "${dist%% *}"
  echo "Distribution identity: ${dist#* }"
  if [ "$platform" = ios ]; then
    install_profile APPLE_PROFILE_IOS_BASE64 app.mobileprovision
    install_profile APPLE_PROFILE_IOS_WIDGETS_BASE64 widgets.mobileprovision
    install_profile APPLE_PROFILE_IOS_SHARE_BASE64 share.mobileprovision
    install_profile APPLE_PROFILE_WATCH_BASE64 watchkitapp.mobileprovision
    install_profile APPLE_PROFILE_WATCH_WIDGETS_BASE64 watchkitapp-widgets.mobileprovision
  else
    local installer devid
    # Apple names the certificate "Mac Installer Distribution" in the portal; its common name
    # is still "3rd Party Mac Developer Installer".
    installer="$(identity basic "3rd Party Mac Developer Installer: " "Mac Installer Distribution: ")"
    export_env INSTALLER_IDENTITY "${installer#* }"
    echo "Installer identity: ${installer#* }"
    if developer_id_configured; then
      devid="$(identity codesigning "Developer ID Application: ")"
      export_env DEVELOPER_ID_IDENTITY "${devid%% *}"
      echo "Developer ID identity: ${devid#* }"
    else
      export_env DEVELOPER_ID_IDENTITY ""
    fi
    install_profile APPLE_PROFILE_MAC_BASE64 mac.provisionprofile
  fi

  [[ "$ASC_API_KEY_ID" =~ ^[A-Z0-9]{8,12}$ ]] || fail "ASC_API_KEY_ID does not look like a key id."
  export_env ASC_API_KEY_PATH "$SIGNING_DIR/private_keys/AuthKey_$ASC_API_KEY_ID.p8"
  decode ASC_API_KEY_P8_BASE64 "$ASC_API_KEY_PATH"
  grep -q "BEGIN PRIVATE KEY" "$ASC_API_KEY_PATH" || fail "ASC_API_KEY_P8_BASE64 is not a .p8 private key."
  chmod 600 "$ASC_API_KEY_PATH"
  export_env API_PRIVATE_KEYS_DIR "$SIGNING_DIR/private_keys"
}

cleanup() {
  if [ -n "${SIGNING_KEYCHAIN:-}" ] && [ -f "$SIGNING_KEYCHAIN" ]; then
    security delete-keychain "$SIGNING_KEYCHAIN" || true
  fi
  if [ -n "${SIGNING_DIR:-}" ] && [ -d "$SIGNING_DIR" ]; then
    rm -rf "$SIGNING_DIR"
  fi
  echo "Signing keychain and decoded files removed."
}

case "${1:-}" in
  check) check "${2:?platform}" ;;
  setup) setup "${2:?platform}" ;;
  cleanup) cleanup ;;
  *) echo "usage: $0 check|setup <ios|macos> | cleanup" >&2; exit 2 ;;
esac
