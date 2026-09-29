#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
source .env
set +a
mkdir -p .runtime/routes .runtime/secrets
if ! docker network inspect forgedock >/dev/null 2>&1; then
  docker network create --label io.forgedock.managed=true forgedock >/dev/null
elif [[ $(docker network inspect -f '{{index .Labels "io.forgedock.managed"}}' forgedock) != true ]]; then
  echo 'Existing forgedock network lacks ownership label; refusing to reuse it.' >&2; exit 1
fi
if ! docker volume inspect forgedock-postgres-data >/dev/null 2>&1; then
  docker volume create --label io.forgedock.managed=true forgedock-postgres-data >/dev/null
elif [[ $(docker volume inspect -f '{{index .Labels "io.forgedock.managed"}}' forgedock-postgres-data) != true ]]; then
  echo 'Existing database volume lacks ownership label.' >&2; exit 1
fi
start_owned() {
  local name=$1
  shift
  if docker container inspect "$name" >/dev/null 2>&1; then
    [[ $(docker inspect -f '{{index .Config.Labels "io.forgedock.managed"}}' "$name") == true ]] || { echo "Refusing unowned container $name" >&2; exit 1; }
    docker start "$name" >/dev/null
  else
    docker run -d --name "$name" --label io.forgedock.managed=true --network forgedock "$@" >/dev/null
  fi
}
umask 077
printf 'POSTGRES_USER=forgedock\nPOSTGRES_DB=forgedock\nPOSTGRES_PASSWORD=%s\n' "$POSTGRES_PASSWORD" > .runtime/secrets/postgres.env
start_owned forgedock-postgres --env-file .runtime/secrets/postgres.env -p 127.0.0.1:5432:5432 -v forgedock-postgres-data:/var/lib/postgresql/data postgres:17-alpine
start_owned forgedock-proxy -p 127.0.0.1:8088:80 -v "$PWD/.runtime/routes:/etc/nginx/conf.d:ro,z" nginx:alpine
rm -f .runtime/secrets/postgres.env
for attempt in {1..30}; do
  if docker exec forgedock-postgres pg_isready -U forgedock -d forgedock >/dev/null; then echo 'PostgreSQL and nginx started.'; exit 0; fi
  sleep 1
done
echo 'Database failed to become ready.' >&2
exit 1
