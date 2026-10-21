#!/bin/bash
# Uploads an .ipa (ios) or .pkg (macos) to App Store Connect with the API key; the build then
# shows up in TestFlight after Apple's processing. altool is Apple's documented command-line
# upload path (Xcode). Since Xcode 26 its exit code is not reliable, so the JSON result decides.
#
#   scripts/apple/upload-testflight.sh <file> <ios|macos>
#
# Environment: ASC_API_KEY_ID, ASC_API_ISSUER_ID, API_PRIVATE_KEYS_DIR (keychain.sh setup puts
# AuthKey_<id>.p8 there; altool looks the key up by id in that directory).
set -euo pipefail

file="${1:?file}"
platform="${2:?ios or macos}"
: "${ASC_API_KEY_ID:?}" "${ASC_API_ISSUER_ID:?}" "${API_PRIVATE_KEYS_DIR:?}"
[ -f "$file" ] || { echo "::error::$file does not exist"; exit 1; }
[ -f "$API_PRIVATE_KEYS_DIR/AuthKey_$ASC_API_KEY_ID.p8" ] || { echo "::error::API key file missing"; exit 1; }

out="$(mktemp)"
trap 'rm -f "$out"' EXIT
set +e
xcrun altool --upload-app -f "$file" -t "$platform" \
  --apiKey "$ASC_API_KEY_ID" --apiIssuer "$ASC_API_ISSUER_ID" --output-format json > "$out" 2>&1
code=$?
set -e
cat "$out"

python3 - "$out" "$code" <<'PY'
import json, re, sys
text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
code = int(sys.argv[2])
match = re.search(r"\{.*\}", text, re.S)
result = {}
if match:
    try:
        result = json.loads(match.group(0))
    except ValueError:
        pass
errors = result.get("product-errors") or []
for e in errors:
    msg = e.get("message", "")
    reason = (e.get("userInfo") or {}).get("NSLocalizedFailureReason", "")
    print(f"::error::App Store Connect: {msg} {reason}".strip())
if code != 0 or errors or "success-message" not in result:
    if not errors:
        print(f"::error::altool upload failed (exit code {code}); see the output above")
    sys.exit(1)
print(result["success-message"])
PY
