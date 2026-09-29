#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -e .env ]]; then echo '.env already exists; keeping it.'; exit 0; fi
umask 077
password=$(openssl rand -hex 24)
token=$(openssl rand -hex 32)
key=$(openssl rand -base64 32)
cat > .env <<EOF
POSTGRES_PASSWORD=$password
ConnectionStrings__ForgeDock='Host=localhost;Port=5432;Database=forgedock;Username=forgedock;Password=$password'
ForgeDock__ApiToken=$token
ForgeDock__SecretKey=$key
ForgeDock__RuntimePath='$PWD/.runtime'
ForgeDock__WebRoot='$PWD/web/dist'
ASPNETCORE_URLS=http://127.0.0.1:5080
EOF
mkdir -p .runtime/routes .runtime/secrets
echo 'Created private .env with generated local credentials. Keep it private and back up the secret key.'
