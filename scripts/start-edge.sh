#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
source .env
set +a
if [[ ${ForgeDock__Domains__Enabled:-false} != true ]]; then
  echo 'Custom domains are disabled. See docs/CUSTOM_DOMAINS.md to configure the HTTPS edge.'
  exit 0
fi
for setting in ForgeDock__Domains__Target ForgeDock__Domains__Addresses ForgeDock__Domains__Email; do
  if [[ -z ${!setting:-} ]]; then echo "Set $setting in .env before starting the HTTPS edge." >&2; exit 1; fi
done
name=${ForgeDock__Domains__EdgeContainer:-forgedock-edge}
[[ $name =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || { echo 'Invalid edge container name.' >&2; exit 1; }
binding=${ForgeDock__Domains__BindAddress:-127.0.0.1}
http_port=${ForgeDock__Domains__HttpPort:-8080}
https_port=${ForgeDock__Domains__HttpsPort:-8443}
[[ $binding == 127.0.0.1 || $binding == 0.0.0.0 || $binding == :: ]] || { echo 'Edge BindAddress must be 127.0.0.1, 0.0.0.0, or ::.' >&2; exit 1; }
for port in "$http_port" "$https_port"; do
  [[ $port =~ ^[1-9][0-9]{0,4}$ && $port -le 65535 ]] || { echo 'Edge ports must be between 1 and 65535.' >&2; exit 1; }
done
[[ $http_port != "$https_port" ]] || { echo 'HTTP and HTTPS ports must differ.' >&2; exit 1; }
root=${ForgeDock__RuntimePath:-$PWD/.runtime}
[[ $root == /* ]] || { echo 'RuntimePath must be absolute for the HTTPS edge.' >&2; exit 1; }
mkdir -p "$root/edge/config" "$root/edge/data" "$root/edge/state"
chmod 700 "$root/edge/data" "$root/edge/state"
if [[ ! -f "$root/edge/config/Caddyfile" ]]; then
  cat > "$root/edge/config/Caddyfile" <<'CADDY'
{
  admin localhost:2019
  persist_config off
}
:80 {
  respond "Not found" 404
}
CADDY
fi
if docker container inspect "$name" >/dev/null 2>&1; then
  [[ $(docker inspect -f '{{index .Config.Labels "io.forgedock.managed"}}' "$name") == true ]] || { echo 'Refusing to reuse an unowned HTTPS edge container.' >&2; exit 1; }
  existing_http=$(docker inspect -f '{{(index (index .HostConfig.PortBindings "80/tcp") 0).HostPort}}' "$name")
  existing_https=$(docker inspect -f '{{(index (index .HostConfig.PortBindings "443/tcp") 0).HostPort}}' "$name")
  existing_binding=$(docker inspect -f '{{(index (index .HostConfig.PortBindings "80/tcp") 0).HostIp}}' "$name")
  if [[ $existing_http != "$http_port" || $existing_https != "$https_port" || $existing_binding != "$binding" ]]; then
    echo "The owned $name container has different port bindings. Stop and remove that container, then run make edge again. Certificate data is retained in $root/edge/data." >&2
    exit 1
  fi
  docker start "$name" >/dev/null
else
  publish_binding=$binding
  [[ $binding != :: ]] || publish_binding='[::]'
  docker run -d --name "$name" --label io.forgedock.managed=true --network forgedock --restart unless-stopped \
    --user "$(id -u):$(id -g)" --cap-drop ALL --cap-add NET_BIND_SERVICE --security-opt no-new-privileges:true --memory 256m --cpus 1 --pids-limit 128 \
    -p "$publish_binding:$http_port:80" -p "$publish_binding:$https_port:443" \
    -v "$root/edge/config:/etc/caddy:ro,z" -v "$root/edge/data:/data:z" -v "$root/edge/state:/config:z" \
    caddy:2.11.4-alpine >/dev/null
fi
for attempt in {1..30}; do
  if docker exec "$name" caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null 2>&1; then
    docker exec "$name" caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile >/dev/null
    echo "HTTPS edge ready on $binding (HTTP $http_port, HTTPS $https_port)."
    exit 0
  fi
  sleep 1
done
echo "HTTPS edge failed to become ready. Inspect docker logs $name." >&2
exit 1
