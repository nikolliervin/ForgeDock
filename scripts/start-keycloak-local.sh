#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
for tool in python3 openssl docker curl; do
  command -v "$tool" >/dev/null || { echo "Missing prerequisite: $tool" >&2; exit 1; }
done
[[ -f .env ]] || { echo 'Run make init first.' >&2; exit 1; }
set -a
source .env
set +a
umask 077
python3 scripts/configure-keycloak-local.py
set -a
source .env
set +a
root="${ForgeDock__RuntimePath:-$PWD/.runtime}/keycloak"

owned_start() {
  local name=$1
  shift
  if docker container inspect "$name" >/dev/null 2>&1; then
    [[ $(docker inspect -f '{{index .Config.Labels "io.forgedock.managed"}}' "$name") == true ]] || {
      echo "Refusing to reuse unowned container $name." >&2; exit 1;
    }
    # Changing secrets/ports/images must not silently keep stale container settings.
    python3 - "$name" <<'VERIFY'
import json, os, subprocess, sys
name = sys.argv[1]
info = json.loads(subprocess.run(['docker', 'inspect', name], check=True, capture_output=True, text=True).stdout)[0]
actual_env = dict(line.split('=', 1) for line in info['Config']['Env'] if '=' in line)
root = os.path.join(os.environ['ForgeDock__RuntimePath'], 'keycloak')
if name in ('forgedock-keycloak', 'forgedock-keycloak-db'):
    envfile = 'keycloak.env' if name == 'forgedock-keycloak' else 'postgres.env'
    expected = dict(line.split('=', 1) for line in open(os.path.join(root, envfile)).read().splitlines() if '=' in line)
    if any(actual_env.get(key) != value for key, value in expected.items()):
        raise SystemExit('Owned container configuration changed. Recreate ' + name + ' while retaining its database and certificates; see docs/AUTHENTICATION.md.')
if name == 'forgedock-keycloak':
    binding = info['HostConfig']['PortBindings']['8443/tcp'][0]
    if binding['HostPort'] != os.environ['ForgeDock__Keycloak__HttpsPort'] or binding['HostIp'] != '127.0.0.1' or info['Config']['Image'] != os.environ['ForgeDock__Keycloak__Image']:
        raise SystemExit('Owned Keycloak image/ports changed. Recreate its container; see docs/AUTHENTICATION.md.')
VERIFY
    docker start "$name" >/dev/null
  else
    docker run -d --name "$name" --label io.forgedock.managed=true --restart unless-stopped "$@" >/dev/null
  fi
}
if ! docker network inspect forgedock >/dev/null 2>&1; then
  docker network create --label io.forgedock.managed=true forgedock >/dev/null
elif [[ $(docker network inspect -f '{{index .Labels "io.forgedock.managed"}}' forgedock) != true ]]; then
  echo 'Refusing to reuse an unowned Docker network.' >&2; exit 1
fi
owned_start forgedock-keycloak-db --network forgedock --env-file "$root/postgres.env" \
  --security-opt no-new-privileges:true --memory 512m --pids-limit 128 \
  -v "$root/postgres:/var/lib/postgresql/data:z" postgres:17-alpine
for attempt in {1..30}; do
  if docker exec forgedock-keycloak-db pg_isready -U keycloak -d keycloak >/dev/null; then break; fi
  sleep 1
done
docker exec forgedock-keycloak-db pg_isready -U keycloak -d keycloak >/dev/null
owned_start forgedock-keycloak --network forgedock --env-file "$root/keycloak.env" \
  --user "$(id -u):$(id -g)" --cap-drop ALL --security-opt no-new-privileges:true --memory 1536m --cpus 2 --pids-limit 256 \
  -p "127.0.0.1:${ForgeDock__Keycloak__HttpsPort}:8443" \
  -v "$PWD/keycloak/themes:/opt/keycloak/themes:ro,z" \
  -v "$root/tls:/etc/keycloak/tls:ro,z" -v "$root/import:/opt/keycloak/data/import:ro,z" \
  "${ForgeDock__Keycloak__Image}" start --import-realm
owned_start forgedock-sso-proxy --network host --user "$(id -u):$(id -g)" \
  --cap-drop ALL --cap-add NET_BIND_SERVICE --security-opt no-new-privileges:true --memory 128m --pids-limit 128 \
  -v "$root/proxy:/etc/caddy:ro,z" -v "$root/tls:/etc/forgedock-tls:ro,z" \
  caddy:2.11.4-alpine
for attempt in {1..90}; do
  if curl --silent --fail --cacert "$root/tls/ca.crt" \
      "${ForgeDock__Oidc__Authority}/.well-known/openid-configuration" >/dev/null; then
    bash scripts/with-env.sh python3 scripts/configure-keycloak-mfa.py
    echo "Keycloak ready: https://localhost:${ForgeDock__Keycloak__HttpsPort}"
    echo "Dashboard: ${ForgeDock__Oidc__PublicOrigin} (start the API and web server with make api / make web)."
    echo 'Trust the local CA in your browser before first login; see docs/AUTHENTICATION.md.'
    exit 0
  fi
  sleep 1
done
echo 'Keycloak did not become ready. Inspect docker logs forgedock-keycloak.' >&2
exit 1
