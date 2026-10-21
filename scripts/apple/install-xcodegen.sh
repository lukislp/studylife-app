#!/bin/bash
# Installs a pinned XcodeGen release (checksum-verified) for the CI iOS jobs. The Mac build
# machine keeps its own copy in ~/tools/xcodegen; scripts read the path from XCODEGEN.
# Appends XCODEGEN=<binary> to GITHUB_ENV.
set -euo pipefail

VERSION=2.46.0
SHA256=4d9e34b62172d645eed6457cac13fc222569974098ef4ee9c3368bedf0196806   # xcodegen.zip of 2.46.0

dest="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/xcodegen-$VERSION"
mkdir -p "$dest"
curl -fsSL --retry 3 -o "$dest/xcodegen.zip" \
  "https://github.com/yonaskolb/XcodeGen/releases/download/$VERSION/xcodegen.zip"
actual="$(shasum -a 256 "$dest/xcodegen.zip" | cut -d' ' -f1)"
[ "$actual" = "$SHA256" ] || { echo "::error::xcodegen.zip checksum mismatch ($actual)"; exit 1; }
unzip -q -o "$dest/xcodegen.zip" -d "$dest"
bin="$dest/xcodegen/bin/xcodegen"
[ -x "$bin" ] || { echo "::error::$bin missing after unzip"; exit 1; }
"$bin" --version
echo "XCODEGEN=$bin" >> "${GITHUB_ENV:-/dev/null}"
