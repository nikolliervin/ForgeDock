#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version=v5.5.1
case "$(uname -m)" in x86_64) architecture=x86_64 ;; aarch64) architecture=aarch64 ;; *) echo 'Unsupported Compose binary architecture.' >&2; exit 1 ;; esac
mkdir -p .runtime/tools
binary="docker-compose-linux-$architecture"
url="https://github.com/docker/compose/releases/download/$version/$binary"
curl --fail --location --silent --show-error "$url" -o ".runtime/tools/$binary"
curl --fail --location --silent --show-error "$url.sha256" -o ".runtime/tools/$binary.sha256"
(cd .runtime/tools && sha256sum --check "$binary.sha256")
chmod 755 ".runtime/tools/$binary"
mv ".runtime/tools/$binary" .runtime/tools/docker-compose
.runtime/tools/docker-compose version
