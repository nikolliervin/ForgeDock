#!/usr/bin/env bash
# Install only this machine's localhost/loopback-constrained development CA.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
source .env
set +a
certificate="${ForgeDock__RuntimePath:-$PWD/.runtime}/keycloak/tls/ca.crt"
[[ -f "$certificate" ]] || { echo 'Run make keycloak first.' >&2; exit 1; }
openssl x509 -in "$certificate" -noout -text | python3 -c '
import sys
text = sys.stdin.read()
assert "X509v3 Name Constraints: critical" in text and "DNS:localhost" in text and "127.0.0.0/255.0.0.0" in text, "Refusing to trust a CA without localhost/loopback name constraints"
'
if [[ $EUID != 0 ]]; then
  echo 'Run sudo bash scripts/trust-keycloak-local.sh to trust the localhost-only CA.' >&2
  exit 1
fi
if command -v update-ca-trust >/dev/null; then
  target=/etc/pki/ca-trust/source/anchors/forgedock-local-sso.crt
  install -m 644 "$certificate" "$target"
  update-ca-trust extract
elif command -v update-ca-certificates >/dev/null; then
  target=/usr/local/share/ca-certificates/forgedock-local-sso.crt
  install -m 644 "$certificate" "$target"
  update-ca-certificates
else
  echo 'Import the CA manually into your browser certificate-authority store.' >&2
  exit 1
fi
# Chromium on Linux also reads custom roots from the user's NSS database.
browser_user=${1:-${SUDO_USER:-$(id -un)}}
if command -v certutil >/dev/null && command -v runuser >/dev/null; then
  browser_home=$(getent passwd "$browser_user" | cut -d: -f6)
  [[ -n "$browser_home" ]] || { echo 'Unknown browser user.' >&2; exit 1; }
  database="$browser_home/.pki/nssdb"
  runuser -u "$browser_user" -- mkdir -p "$database"
  runuser -u "$browser_user" -- chmod 700 "$database"
  if [[ ! -f "$database/cert9.db" ]]; then
    runuser -u "$browser_user" -- certutil -N -d "sql:$database" --empty-password
  fi
  runuser -u "$browser_user" -- certutil -A -d "sql:$database" -n 'ForgeDock Local SSO CA' -t 'C,,' -i "$target"
fi
echo 'Trusted ForgeDock development CA for localhost and loopback only. Restart your browser if needed.'
