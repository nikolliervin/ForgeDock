#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ ! -f .env ]]; then echo 'Create .env first; see docs/DEVELOPMENT.md.' >&2; exit 1; fi
set -a
source .env
set +a
# Trust the local identity provider's private CA only for this process. Never disable TLS validation.
if [[ ${ForgeDock__Auth__Mode:-} == keycloak && ${ForgeDock__Keycloak__LocalEnabled:-false} == true ]]; then
  auth_root="${ForgeDock__RuntimePath:-$PWD/.runtime}/keycloak/tls/ca.crt"
  [[ -f "$auth_root" ]] || { echo 'Run make keycloak first to create the local HTTPS certificates.' >&2; exit 1; }
  export SSL_CERT_FILE="$auth_root"
  export NODE_EXTRA_CA_CERTS="$auth_root"
fi
exec "$@"
