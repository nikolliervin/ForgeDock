#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version=v0.40.1
case "$(uname -m)" in
  x86_64) architecture=x86_64; checksum=2842de93e68713af9037e0bc0a398d7da78f3b96aa4804303a638db2bc69bd30 ;;
  aarch64) architecture=arm64; checksum=c24a064b586b8f4f8c2fab44dd5ef19253e4c6cc4e1df793b3ae19cd87f7a5d4 ;;
  *) echo 'Railpack setup supports Linux x86_64 and aarch64.' >&2; exit 1 ;;
esac
if [[ $(uname -s) != Linux ]]; then echo 'Railpack setup requires Linux.' >&2; exit 1; fi
mkdir -p .runtime/tools
if [[ ! -x .runtime/tools/railpack || $(.runtime/tools/railpack --version) != *"${version#v}"* ]]; then
  temporary=$(mktemp -d .runtime/tools/railpack-install.XXXXXX)
  trap 'rm -rf "$temporary"' EXIT
  archive="railpack-$version-$architecture-unknown-linux-musl.tar.gz"
  curl --fail --location --silent --show-error "https://github.com/railwayapp/railpack/releases/download/$version/$archive" -o "$temporary/$archive"
  printf '%s  %s\n' "$checksum" "$temporary/$archive" | sha256sum --check
  tar -xzf "$temporary/$archive" -C "$temporary"
  install -m 755 "$temporary/railpack" .runtime/tools/railpack
fi
.runtime/tools/railpack --version

# BuildKit stays private: no published ports, and only labeled resources are reused.
name=forgedock-buildkit
if docker container inspect "$name" >/dev/null 2>&1; then
  if [[ $(docker inspect -f '{{index .Config.Labels "io.forgedock.managed"}}' "$name") != true ]]; then
    echo 'Existing BuildKit container lacks ownership label; refusing to reuse it.' >&2
    exit 1
  fi
  docker start "$name" >/dev/null
else
  docker run -d --name "$name" --label io.forgedock.managed=true --privileged moby/buildkit:v0.33.0 >/dev/null
fi
for attempt in {1..30}; do
  if docker exec "$name" buildctl debug workers >/dev/null 2>&1; then
    echo 'Railpack and BuildKit are ready for automatic builds.'
    exit 0
  fi
  sleep 1
done
echo 'BuildKit failed to become ready. Inspect docker logs forgedock-buildkit.' >&2
exit 1
