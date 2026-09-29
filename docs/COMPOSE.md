# Compose deployment support

## Model and scope

A project selects Dockerfile or Compose mode. Compose projects configure a repository-relative Compose file, a public service name, the public service's internal HTTP port, and health path. Standard service build contexts are resolved relative to the Compose file, so separate frontend/backend directories work.

The worker resolves Compose using saved project variables as interpolation inputs, validates paths/resource settings, builds service images, retains immutable image tags, and starts the stack under `forgedock-<project-id>`. Normalized manifests contain resolved environment values and are encrypted in PostgreSQL for rollback. Temporary env/manifest files use mode 0600 and are deleted after commands. Compose processes receive a minimal environment so platform database/encryption/token configuration is not exposed to interpolation.

## Networking and persistent data

Host-published Compose ports are removed. Only the selected public service joins ForgeDock's ingress network. Original service names and aliases work on the private stack networks. Container names and network/volume names are rewritten into the owned project namespace; explicitly declared container names do not collide with other apps. All managed resources receive ownership labels, checked before operations.

Named volumes are stable across deployments, stop, restart, rollback, and project deletion. Deletion removes stack containers/private networks and DB metadata but deliberately does not remove volumes. Back up data independently. Rollback restores retained images/configuration, not database contents or schema. Incompatible schema migrations require application-specific recovery.

## Lifecycle

Compose updates are in place: service containers are replaced within the same stack, sharing persistent data. Brief downtime is possible. `up --wait` checks Compose service health/dependencies, followed by an HTTP probe of the selected service. Only then is nginx reloaded and deployment state marked Running. Per-service state/health and service-prefixed logs are available in the dashboard.

Failure after stack changes triggers a best-effort replay of the previous retained manifest/images. Failure before stack changes leaves the active stack alone. Recovery cannot undo DB migrations. Worker-crash reconciliation and concurrent lifecycle fencing retain the existing MVP limitations.

Stop halts the stack. Restart replays the active retained manifest. Rollback replays an earlier successful manifest with retained image tags and environment values. Delete runs scoped `down` without deleting volumes. Dockerfile mode continues to work.

## Supported / rejected configuration

Supported: multiple services, images and repository-local builds (including targets), dependencies and health checks, private networks, named local volumes, repository-local bind mounts, and repository-file configs/secrets. Repository paths cannot escape the checkout or traverse symbolic links. Only one replica per service is supported.

Rejected: privileged containers, host namespaces/networking, added capabilities, device/socket/host filesystem mounts, external volumes/networks, custom drivers/options, profiles, SSH-forwarded builds, additional build contexts, build entitlements/outputs, and lifecycle hooks. Original host port mappings are intentionally ignored. Each service defaults to a 2 GiB memory limit, one CPU, 256 PIDs, restricted default capabilities (retaining those needed by database initialization), and no-new-privileges. Operators remain trusted; arbitrary builds are not tenant-isolated.

## Tooling

Run `bash scripts/install-compose.sh` if `docker compose` is unavailable. It downloads checksum-verified Docker Compose v5.5.1 into `.runtime/tools`, leaving host Docker configuration alone. `ForgeDock__ComposeExecutable` can point to another standalone binary. The worker otherwise falls back to the installed Docker Compose plugin.

## Verification status

Configuration and normalization tests pass. End-to-end stack verification is in progress; consult MVP_VERIFICATION.md for completed runtime scenarios.
