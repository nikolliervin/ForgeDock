#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

for command in dotnet npm docker git openssl make setsid curl tar sha256sum install; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Missing prerequisite: $command. See docs/DEVELOPMENT.md." >&2
    exit 1
  fi
done
if ! docker info >/dev/null 2>&1; then
  echo 'Docker is unavailable. Start Docker and ensure your user can access it.' >&2
  exit 1
fi

make init
make infra
# Start the selected local identity provider before the API.
set -a
source .env
set +a
if [[ ${ForgeDock__Auth__Mode:-} == keycloak && ${ForgeDock__Keycloak__LocalEnabled:-false} == true ]]; then
  bash scripts/start-keycloak-local.sh
fi
make edge
make railpack
make migrate
npm --prefix web ci
dotnet build ForgeDock.sln

# Each service gets its own process group so its children stop with it.
pids=()
cleanup() {
  trap - EXIT INT TERM
  for pid in "${pids[@]}"; do
    kill -TERM -- "-$pid" 2>/dev/null || true
  done
  for pid in "${pids[@]}"; do
    wait "$pid" 2>/dev/null || true
  done
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

setsid bash scripts/with-env.sh dotnet run --project src/ForgeDock.Api --no-build --no-launch-profile &
pids+=("$!")
setsid bash scripts/with-env.sh dotnet run --project src/ForgeDock.Worker --no-build --no-launch-profile &
pids+=("$!")
setsid npm --prefix web run dev &
pids+=("$!")

printf '\nStarting ForgeDock at %s/dashboard\n' "${ForgeDock__Oidc__PublicOrigin:-http://127.0.0.1:5173}"
echo 'Sign in with your configured SSO provider (HTTPS required) or the legacy ForgeDock__ApiToken. See docs/SSO.md. Press Ctrl+C to stop the app.'
echo 'Infrastructure containers stay running for the next launch.'

status=0
wait -n "${pids[@]}" || status=$?
echo 'A service exited; stopping the remaining services.' >&2
exit "$status"
