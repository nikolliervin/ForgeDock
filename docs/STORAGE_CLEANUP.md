# Storage cleanup

Open **Storage** in the dashboard sidebar. Save the number of successful deployments to retain per project (1–100), source checkout retention (1–365 days), log retention (1–365 days), and orphan/deleted-preview grace period (1–365 days). Automatic daily cleanup is off by default. Apply the `StorageCleanup` migration and restart the API and worker.

**Preview cleanup and disk usage** reports runtime file usage, filesystem capacity/free space, eligible image tags and source directories, and the number of old log entries. Image sizes share layers; this is an eligibility preview, not a promise of reclaimed bytes. The dashboard loads this preview automatically and provides artifact type filters and search. Refresh it to recheck eligibility. Unsaved policy changes disable cleanup; saving refreshes the preview using the new policy.

**Run cleanup** requires dashboard confirmation and creates a durable queued job. The serial deployment worker recomputes eligibility at execution time. Daily automatic cleanup uses the same lifecycle. Interrupted jobs are failed on worker restart; preview current state before retrying, because filesystem and Docker deletion cannot be rolled back.

Protected artifacts include active deployment images, the latest configured number of successful deployments per project, pending deployments and their rollback sources, image references retained in Compose manifests, and source checkouts used by pending builds. Rollback and restart queueing share a PostgreSQL advisory lock with image cleanup. Successful image removal clears that deployment's rollback availability, while preserving history and the ability to rebuild its revision.

Eligible source paths are GUID-named directories directly beneath the configured runtime `sources` folder; symbolic-link roots are skipped and disk-size traversal does not follow links. Orphan directories use filesystem modification time, while known deployments use deployment creation time. Image eligibility requires the platform's GUID project/deployment tag format. Conflicting project labels exclude images. Retained images reused by rollback are protected by their exact tag, even when their original deployment is old.

Cleanup removes stopped owned app containers where necessary to release eligible image tags. It never forces Docker image removal or removes volumes. Containers still using an image can prevent removal; shared layers/tags can also reduce reclaimed space. Database containers, persistent database/Compose volumes, backups, unrelated images/source paths, Docker build caches, and networks remain intact. Deleted preview images/checkouts follow the orphan grace period; preview volumes require separate explicit operator removal.

Endpoints: `GET /api/storage`, `PUT /api/storage/policy`, `GET /api/storage/preview`, and `POST /api/storage/cleanup` with `{ "confirm": true }`. All require management authentication. Settings are platform-wide, and completed job history lists removed artifacts in `resultJson`.

## Verification

On 2026-10-01, eight storage backend tests passed, including a real Docker/PostgreSQL integration check that uses an isolated control database/runtime directory and restricts Docker enumeration to its own unique image tags. It verifies pending rollback protection, orphan/source deletion, stopped-container removal, retained image survival, cleared rollback availability, expired log deletion, and preservation of a persistent volume and backup file. Unit checks cover ownership tag parsing, active/latest/pending image protection, and symlink-safe disk usage.

```bash
FORGEDOCK_DOCKER_TESTS=1 bash scripts/with-env.sh \
  dotnet test tests/ForgeDock.Tests --filter FullyQualifiedName~Storage
npm --prefix web run build
npm --prefix web run test:e2e -- storage.spec.ts
```

The browser check uses API fixtures to exercise policy editing, unsaved-change protection, automatic preview, artifact filters/search, required confirmation, queued job display, and mobile overflow. The redesigned dashboard was also visually checked at desktop and mobile sizes in light and dark themes. Automatic scheduling uses the serial worker, so long deployments can delay cleanup.
