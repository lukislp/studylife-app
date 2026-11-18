#!/usr/bin/env bash
# Starts the pinned StudyAtrium server in demo mode for the store screenshots (README "App Store screenshots").
#
# Demo mode (studylife: Services/DemoModeGuard.cs) needs DEMO_MODE=true plus the explicit confirmation,
# because it wipes the database and seeds the demo dataset on every start - here the database is a new
# SQLite file in WORK_DIR, so nothing else is touched. The demo login page signs the app in as the seeded
# demo student; no account, password or secret is involved, and the screenshots can only ever show the
# demo dataset.
#
# The server speaks https with a certificate from a throwaway CA made here (valid 7 days, key never leaves
# the runner). The capture script trusts that CA inside each simulator, so the app connects with its normal
# transport security - nothing about App Transport Security is relaxed for the screenshots.
#
# Environment: SERVER_DLL (built StudyLife.Server.dll), WORK_DIR (empty folder), PORT (default 5443).
# Writes WORK_DIR/ca.pem (for the simulators) and WORK_DIR/server.log; prints the server address.
set -euo pipefail

: "${SERVER_DLL:?path to StudyLife.Server.dll}" "${WORK_DIR:?work folder}"
PORT="${PORT:-5443}"
URL="https://localhost:$PORT"
mkdir -p "$WORK_DIR"
cd "$WORK_DIR"

cat > ca.cnf <<'EOF'
[req]
distinguished_name = dn
x509_extensions = v3_ca
prompt = no
[dn]
CN = StudyAtrium store screenshots throwaway CA
[v3_ca]
basicConstraints = critical, CA:TRUE
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
EOF
cat > leaf.ext <<'EOF'
basicConstraints = CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = DNS:localhost, IP:127.0.0.1, IP:::1
EOF

openssl req -x509 -new -nodes -newkey rsa:2048 -sha256 -days 7 -config ca.cnf -keyout ca.key -out ca.pem 2>/dev/null
openssl req -new -nodes -newkey rsa:2048 -sha256 -subj "/CN=localhost" -keyout leaf.key -out leaf.csr 2>/dev/null
openssl x509 -req -sha256 -days 7 -in leaf.csr -CA ca.pem -CAkey ca.key -CAcreateserial -extfile leaf.ext -out leaf.pem 2>/dev/null
openssl pkcs12 -export -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
  -inkey leaf.key -in leaf.pem -out leaf.pfx -passout pass:store-screenshots
rm -f ca.key leaf.csr

# Content root = WORK_DIR, so the SQLite file lands in WORK_DIR/app_data.
ASPNETCORE_ENVIRONMENT=Production \
ASPNETCORE_URLS="$URL" \
Kestrel__Certificates__Default__Path="$WORK_DIR/leaf.pfx" \
Kestrel__Certificates__Default__Password=store-screenshots \
DEMO_MODE=true \
DEMO_MODE_CONFIRM_DATA_LOSS=yes-delete-all-data \
  nohup dotnet "$SERVER_DLL" > server.log 2>&1 &
echo $! > server.pid

for _ in $(seq 1 90); do
  if curl -fsS --cacert ca.pem "$URL/api/auth/demo" 2>/dev/null | grep -q '"demo":true'; then
    echo "[store-screenshots] demo server up at $URL" >&2
    echo "$URL"
    exit 0
  fi
  if ! kill -0 "$(cat server.pid)" 2>/dev/null; then
    echo "::error::the server exited during start-up" >&2
    cat server.log >&2
    exit 1
  fi
  sleep 2
done
echo "::error::the server did not report demo mode within 180 s" >&2
cat server.log >&2
exit 1
