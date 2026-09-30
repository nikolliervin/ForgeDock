# MVP verification

Environment: Fedora 44, Linux x64; .NET SDK 10.0.112/runtime 10.0.12; Node 22.23.1; npm 10.9.8; Git 2.55.0; Docker 29.7.2. Local verification date: 2026-09-30.

## Reproduction commands

```bash
make init
make infra
make migrate
npm --prefix web ci
npm exec --prefix web -- playwright install chromium
# Separate terminals:
make api
make worker
# Built dashboard at port 5080; optional Vite development UI:
make web
make build
make test
FORGEDOCK_TEST_URL=http://127.0.0.1:5080 make e2e
```

The browser suite's narrow-viewport scenario expects the retained `Docker welcome demo` acceptance project. Create it through the UI using the repository/settings below before running the suite. Its main workflow creates and deletes a separate temporary demo project.

## Results

| Scenario | Result |
| --- | --- |
| Backend build | Pass, zero warnings/errors |
| Frontend TypeScript/production build | Pass |
| Unit tests | 14 pass, including literal arguments, failure output, and cancellation; scaffold-only test removed |
| npm dependency audit | Zero reported vulnerabilities |
| PostgreSQL/nginx startup | Pass using CLI scripts |
| EF migrations on a new database | Pass; subsequent operation migration also applied |
| EF pending-model check | No pending changes |
| Token login and anonymous rejection | Pass; unauthenticated management returns 401 |
| Unsafe Git URL | Rejected with 400 |
| Project configuration / immutable queued snapshot | Missing-Dockerfile snapshot still failed after settings were restored |
| Public Git retrieval and Docker build | Pass |
| Container start / HTTP health | Pass |
| nginx application routing | HTTP 200 and expected HTML |
| Deployment logs | 178 initial persisted rows returned through API |
| Redeploy | New container running; previous deployment stopped |
| Rollback | Retained original image used by new container; active deployment changed; routed HTTP 200 |
| Environment API | Returns names only; values never returned |
| Environment delivery | Actual container metadata contained the configured test value |
| Deployment log secret exposure | Test value absent from stored deployment logs |
| Missing Dockerfile | Failed with actionable error; existing active route preserved |
| API/worker restart | History/status preserved; application continued returning HTTP 200 |
| Browser interaction | Pass: login/create/environment/deploy/logs/application response/stop/restart/delete/logout |
| Narrow-viewport layout | Pass at 390px width, no horizontal overflow |
| Built frontend serving | API port 5080 serves dashboard; browser test passed against it |

Representative repository: `https://github.com/docker/welcome-to-docker.git`, branch `main`, root Dockerfile, container port `3000`, health path `/`. Recorded commit: `68c1b9f87c41fb3fef2667e27149865c2f42d1eb`. First verification project ID: `f9ec4147-2118-4cc2-9c93-94faf33fe17c`; routed host `f9ec414721184cc29c9394faf33fe17c.localhost:8088`.

The API/worker were gracefully stopped and restarted while Docker applications continued running. No database was recreated. All five recorded deployment histories remained available with expected terminal/current states.

Docker initially had no socket; starting the installed service resolved this. The Compose plugin is absent; `make infra` uses Docker CLI. Playwright used its Ubuntu fallback Chromium build on Fedora and passed.

## Limits of evidence

Rollback was verified against a retained image from the same upstream commit; the actual upstream/container switch is proven, but a changed-page two-version test remains. Unclean crash during route switching, Docker daemon restart, exhaustive DB failure recovery, distributed worker fencing, concurrent management requests, and production remote TLS exposure were not tested. Periodic logs are bounded polling and do not guarantee complete delivery. See SECURITY.md and IMPLEMENTATION_STATUS.md before public exposure.

## Compose verification

32 unit tests and one opt-in Docker integration test pass. The integration runs two versions, restores retained images, recovers after failed startup, verifies forced container recreation, mounts repository secret files, preserves Redis data across stop/restart/rollback, and retains named volumes after stack deletion.

The public docker/awesome-compose WordPress/MariaDB stack deployed and restarted successfully. Browser verification follows the root URL to `/wp-admin/install.php`, preserves port 8088, and loads the WordPress installation page. The existing Dockerfile workflow and mobile layout checks also pass. A repair migration fixes empty service-status JSON in deployments created before Compose support; historical deployment API requests now return successfully.
