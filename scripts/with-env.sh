#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ ! -f .env ]]; then echo 'Create .env first; see docs/DEVELOPMENT.md.' >&2; exit 1; fi
set -a
source .env
set +a
exec "$@"
