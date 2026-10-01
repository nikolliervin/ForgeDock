# Automatic rollback

Apply `AutomaticRollback` and restart the API/worker. Enable **Settings → Automatic rollback**, then choose an observation window (1–60 minutes, default 10) and consecutive failed checks (1–10, default 3). Policy is snapshotted for future deployments; editing settings does not change an already running release's watch.

Successful new builds and promotions with a previous active deployment start an observation window. The existing HTTP/service monitor counts failed checks and resets the count after a healthy check. Container exits also count. Runtime log retrieval failures alone do not trigger rollback. Checks normally occur every 30 seconds when the serial worker is free, so builds and database work can delay observation; the window is elapsed wall-clock time and can expire during a long build.

After the threshold, the worker queues one recovery deployment using the previous environment's retained image, runtime snapshot, and Compose manifest where applicable. It waits while that project has deployment, operation, or backup work pending. The previous image is protected from storage retention during the window. Queued recovery survives restarts, and the trigger is recorded as `AutomaticRollback` in history and the original release log. Configured failure-notification channels receive a recovery deployment link.

Explicitly stopped deployments, expired windows, releases without a previous image, and already-triggered watches do not trigger recovery. Rollback/restart releases do not start another watch or replay hooks, preventing rollback loops. Recovery still uses normal startup and health checks; a failed recovery requires operator attention. Automatic rollback does not revert schema migrations, database contents, or external side effects. Compatible migrations and backups remain necessary.

Settings show recent release deadlines, failure counts, and whether recovery was queued. Authenticated endpoints: `GET /api/projects/{id}/rollback-policy` and `PUT /api/projects/{id}/rollback-policy` with `autoRollbackEnabled`, `rollbackWindowMinutes`, and `rollbackFailureThreshold`.

## Verification

Unit checks cover consecutive-failure reset, expiry, duplicate-trigger prevention, replay exclusion, and retention protection. A real worker/Docker/PostgreSQL test starts two releases, stops the second container, runs the actual monitor until one recovery is queued, verifies a durable notification and no duplicate recovery, then runs the recovery deployment and confirms the first release's runtime value is serving with no new watch.

```bash
FORGEDOCK_DOCKER_TESTS=1 bash scripts/with-env.sh \
  dotnet test tests/ForgeDock.Tests --filter FullyQualifiedName~AutomaticRollback
npm --prefix web run build
npm --prefix web run test:e2e -- automatic-rollback.spec.ts
```

The browser check uses API fixtures to verify policy editing and watch display. The new integration test exercises single-application recovery; Compose uses its existing retained-manifest lifecycle and was not separately tested with a failing real Compose release.
