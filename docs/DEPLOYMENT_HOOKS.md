# Deployment hooks

Apply `DeploymentHooks` and restart the API/worker. In **Project → Settings → Deployment hooks**, configure before-routing and after-routing shell commands and a timeout of 1–900 seconds (default 120). Empty commands disable their phase. Commands are snapshotted with each queued deployment; later edits affect future deployments only. Images must include `/bin/sh` and a `timeout` utility supporting `-s TERM -k 5`.

Hooks execute in the candidate app container as its configured user after its initial HTTP health check. Before-routing commands run while the earlier app is still serving. After-routing commands run after nginx reload and before the candidate is marked active and the old container stops. Compose hooks target the routed app service. Container ownership and image are verified, execution uses the immutable container ID, and containers with host access or bind mounts are rejected.

A failed before-routing hook prevents the route switch. A failed after-routing hook restores and reloads the previous single-app route; Compose uses its existing retained-stack recovery. The candidate can receive traffic briefly before an after-routing failure. Database changes and external side effects cannot be undone; migrations must be compatible with the previous app and retries. Rollback/restart skip hooks to avoid replaying old schema-changing commands. Promotion runs the destination environment's snapshotted hooks.

The in-container timeout sends TERM at the configured deadline and escalates to KILL after five seconds. The host waits up to another 15 seconds. Process-tree termination follows the image's `timeout` implementation; detached processes or commands that deliberately escape that supervision require operator inspection. Worker interruption is recorded as failure and hooks are not automatically retried.

Settings show the latest 30 executions with phase, deployment link, state, exit code, and bounded output (64 KiB). Saved environment secret values and their individual lines are redacted. Commands are configuration and should reference environment variables instead of containing credentials. Only known runtime secret values can be redacted; avoid printing external secrets. Deployment logs include phase/result summaries, and hook history is removed with deployment history.

Authenticated endpoints: `GET /api/projects/{id}/hooks` and `PUT /api/projects/{id}/hooks` with `preDeployCommand`, `postDeployCommand`, and `hookTimeoutSeconds`.

## Verification

Seven hook tests passed, including the actual worker with an isolated PostgreSQL control database and an unprivileged nginx test image. The Docker check proves before/after execution, runtime-secret redaction, after-routing exit-code failure with prior route/active deployment preserved, and a one-second timeout stopping a long command before its final output. Unit checks cover validation, shell argument quoting, multiline redaction, and immutable hook snapshots.

```bash
FORGEDOCK_DOCKER_TESTS=1 bash scripts/with-env.sh \
  dotnet test tests/ForgeDock.Tests --filter FullyQualifiedName~DeploymentHook
npm --prefix web run build
npm --prefix web run test:e2e -- hooks.spec.ts
```

The browser test verifies settings edits and recorded output using API fixtures. Compose hook recovery uses the existing Compose recovery path and was not separately exercised with a new real Compose stack.
