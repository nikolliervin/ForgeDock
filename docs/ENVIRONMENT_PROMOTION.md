# Application environments and image promotion

Apply the `EnvironmentGroups` migration and restart the API/worker. In each project's **Releases** tab, save the same application group name and a different environment name, for example `my-app` with `staging` and `production`. Names use lowercase letters, digits, and hyphens, up to 50 characters. Each group/environment pair is unique. Leave both fields empty to ungroup a project. PR preview projects cannot join these groups.

Environments are independent projects: each retains its own runtime variables, databases, domains, routes, resource limits, backup schedules, and deployment history. Create/configure each project normally before grouping it. Grouping does not copy secrets or database contents.

From the destination project's Releases tab, select a successful retained release from another environment and **Promote release**. Confirmation queues the original image without cloning or rebuilding. The deployment snapshots the destination's current runtime variables, health checks, port, and resource configuration. The standard start, health check, route switch, and old-container lifecycle runs; a failed health check keeps the previously routed version.

Promotion currently supports Auto/Dockerfile single applications with matching repository URLs (`.git` suffix is ignored). Compose stacks must deploy independently because their retained manifests also contain environment-specific networks, volumes, and credentials. Build-time frontend values remain baked into the source image; runtime promotion cannot change them. Configure environment-independent builds when using promotion. Schema/data migration is not performed by promotion itself.

History labels promoted deployments with the `Promotion` trigger and records their source deployment in `rollbackSourceId`, the existing retained-image execution reference. Storage cleanup protects reused image tags across environments. Normal rollback restores the destination deployment's saved runtime snapshot. Deleting a source project does not remove an image still referenced by a retained destination release.

Endpoints (authenticated): `GET /api/projects/{id}/releases`, `PUT /api/projects/{id}/releases/group` with `applicationName`/`environmentName`, and `POST /api/projects/{id}/releases/promote` with `sourceDeploymentId`/`confirm`. Promotion shares the storage advisory lock and rejects pending destination deployment/operation/backup work.

## Verification

Unit tests verify exact image reuse, target-only secret/resource snapshots, skipped build transitions, and rejection of invalid groups, missing images, and Compose. A Docker/PostgreSQL integration test uses an isolated control database and uniquely tagged unprivileged nginx test image with the existing local nginx proxy/network. It runs the actual worker promotion, checks the running container's image digest matches the source, verifies target runtime values through nginx, and confirms no source checkout or build stage exists. The fixture removes its own containers, tags, and route afterward.

```bash
FORGEDOCK_DOCKER_TESTS=1 bash scripts/with-env.sh \
  dotnet test tests/ForgeDock.Tests --filter FullyQualifiedName~ReleasePromotion
npm --prefix web run build
npm --prefix web run test:e2e -- releases.spec.ts
```

The browser test uses API fixtures for grouping, release selection, and required promotion confirmation. Image promotion cannot isolate build-time secrets already embedded in an image.
