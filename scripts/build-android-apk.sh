#!/bin/bash
# Builds, signs and verifies the Android APK with a given keystore and app version. CI uses it
# twice (.github/workflows/ci-cd.yml): release-android signs with the release key from the
# repository secrets, test-android with a throwaway key generated on the runner, so a pull
# request exercises the exact signing path a release takes.
#
# Android installs an APK as an update only when its signing certificate matches the installed
# one and its versionCode is higher. Hence one fixed release key, and a versionCode derived
# from the release version:
#
#   versionCode = major * 10000 + minor * 100 + patch      (1.8.28 -> 10828)
#
# Monotonic while minor and patch stay below 100 (anything else is rejected, never wrapped);
# Android's ceiling of 2100000000 leaves room up to major 209999. Builds before this scheme
# all used versionCode 1.
#
# Usage, from the repo root, with ./studylife checked out and the csproj already reduced to
# the single net10.0-android target (see test-android in ci-cd.yml):
#
#   scripts/build-android-apk.sh version-code 1.8.28   # prints 10828
#   scripts/build-android-apk.sh build                 # builds StudyLife-android.apk
#
# Environment for "build":
#   APP_VERSION                 release version, x.y.z
#   ANDROID_KEYSTORE_BASE64     the keystore file, base64 encoded (the repository secret), or
#   ANDROID_KEYSTORE            a path to the keystore file instead
#   ANDROID_KEYSTORE_PASSWORD   keystore password
#   ANDROID_KEY_ALIAS           key alias inside the keystore
#   ANDROID_KEY_PASSWORD        key password, optional (defaults to the keystore password)
#   APK_OUTPUT                  output path, optional (default StudyLife-android.apk)
#
# The passwords reach MSBuild as env:NAME references, never as values on a command line.
set -euo pipefail

version_code() {
  local version="$1"
  if ! [[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
    echo "Expected a plain x.y.z version, got '$version'." >&2
    return 1
  fi
  local major=$((10#${BASH_REMATCH[1]})) minor=$((10#${BASH_REMATCH[2]})) patch=$((10#${BASH_REMATCH[3]}))
  if [ "$minor" -ge 100 ] || [ "$patch" -ge 100 ]; then
    echo "$version does not fit the versionCode scheme (minor and patch must be below 100)." \
      "Widen the formula without lowering any code already shipped." >&2
    return 1
  fi
  local code=$((major * 10000 + minor * 100 + patch))
  if [ "$code" -gt 2100000000 ]; then
    echo "$version exceeds Android's versionCode limit." >&2
    return 1
  fi
  echo "$code"
}

fail() {
  echo "::error::$*" >&2
  exit 1
}

build() {
  local version="${APP_VERSION:?APP_VERSION is not set}"
  local output="${APK_OUTPUT:-StudyLife-android.apk}"
  local code
  code="$(version_code "$version")" || exit 1

  [ -n "${ANDROID_KEYSTORE_PASSWORD:-}" ] || fail "ANDROID_KEYSTORE_PASSWORD is not set."
  [ -n "${ANDROID_KEY_ALIAS:-}" ] || fail "ANDROID_KEY_ALIAS is not set."
  export ANDROID_KEYSTORE_PASSWORD
  export ANDROID_KEY_PASSWORD="${ANDROID_KEY_PASSWORD:-$ANDROID_KEYSTORE_PASSWORD}"

  local keystore="${ANDROID_KEYSTORE:-}"
  if [ -z "$keystore" ]; then
    [ -n "${ANDROID_KEYSTORE_BASE64:-}" ] || fail "Neither ANDROID_KEYSTORE nor ANDROID_KEYSTORE_BASE64 is set."
    DECODED_KEYSTORE="$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/studylife-release.XXXXXX")"
    # The decoded key never outlives this script, whichever way it exits (a global, because
    # the trap runs after this function's locals are gone).
    trap 'rm -f "$DECODED_KEYSTORE"' EXIT
    keystore="$DECODED_KEYSTORE"
    printf '%s' "$ANDROID_KEYSTORE_BASE64" | base64 --decode > "$keystore" \
      || fail "ANDROID_KEYSTORE_BASE64 is not valid base64."
  fi
  [ -s "$keystore" ] || fail "Keystore $keystore is missing or empty."

  # Fails early with keytool's own message on a wrong password or alias, before a 4-minute build.
  keytool -list -keystore "$keystore" -storepass:env ANDROID_KEYSTORE_PASSWORD \
    -alias "$ANDROID_KEY_ALIAS" > /dev/null \
    || fail "Keystore password or alias ($ANDROID_KEY_ALIAS) does not match the keystore."

  echo "Building versionName $version, versionCode $code"
  dotnet publish src/StudyLife.App/StudyLife.App.csproj -c Release \
    -p:ApplicationDisplayVersion="$version" \
    -p:ApplicationVersion="$code" \
    -p:AndroidKeyStore=true \
    -p:AndroidSigningKeyStore="$keystore" \
    -p:AndroidSigningKeyAlias="$ANDROID_KEY_ALIAS" \
    -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD \
    -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD

  local apks=(src/StudyLife.App/bin/Release/net10.0-android/publish/*-Signed.apk)
  [ "${#apks[@]}" -eq 1 ] && [ -f "${apks[0]}" ] || fail "Expected exactly one signed APK, found: ${apks[*]}"
  cp "${apks[0]}" "$output"

  verify "$output" "$version" "$code"
}

# apksigner and aapt2 come with the Android SDK (ANDROID_HOME, preinstalled on GitHub's
# Ubuntu runners) - nothing is downloaded.
verify() {
  local apk="$1" version="$2" code="$3"
  local sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
  [ -n "$sdk" ] || fail "ANDROID_HOME is not set; cannot find apksigner."
  local build_tools
  build_tools="$(find "$sdk/build-tools" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -1)"
  [ -x "$build_tools/apksigner" ] || fail "apksigner not found under $sdk/build-tools."

  local certs
  certs="$("$build_tools/apksigner" verify --print-certs "$apk")" || fail "apksigner rejected $apk."
  echo "$certs"
  if grep -q 'CN=Android Debug' <<< "$certs"; then
    fail "$apk is signed with an Android debug certificate, not the given keystore."
  fi
  local cert_sha256
  cert_sha256="$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' <<< "$certs")"
  [ -n "$cert_sha256" ] || fail "No signer certificate found in $apk."

  local badging
  badging="$("$build_tools/aapt2" dump badging "$apk" | sed -n 1p)"
  echo "$badging"
  grep -q "versionCode='$code'" <<< "$badging" || fail "$apk has the wrong versionCode (expected $code)."
  grep -q "versionName='$version'" <<< "$badging" || fail "$apk has the wrong versionName (expected $version)."

  local apk_sha256
  apk_sha256="$(sha256sum "$apk" | cut -d' ' -f1)"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
      echo "### Android APK (${APK_SUMMARY_TITLE:-$apk})"
      echo ""
      echo "| | |"
      echo "| --- | --- |"
      echo "| versionName | \`$version\` |"
      echo "| versionCode | \`$code\` |"
      echo "| Signer certificate SHA-256 | \`$cert_sha256\` |"
      echo "| APK SHA-256 | \`$apk_sha256\` |"
    } >> "$GITHUB_STEP_SUMMARY"
  fi
  echo "Signer certificate SHA-256: $cert_sha256"
}

case "${1:-}" in
  version-code) version_code "${2:?usage: $0 version-code <x.y.z>}" ;;
  build) build ;;
  *)
    echo "usage: $0 version-code <x.y.z> | build" >&2
    exit 2
    ;;
esac
