# Implementation status

## Demonstrable vertical slice

Real authenticated UI/API project creation → public Git clone → Docker image build → constrained container start → HTTP health probe → nginx route switch → HTTP 200 application response → persisted logs/history → redeploy → retained-image rollback has been executed on Fedora. Restarted API/worker processes preserve history and application routing.

The dashboard includes settings, encrypted write-only environment editing, deployment history/progress/logs, live health status, restart, confirmed stop, and confirmed project deletion. Built assets are served by the API at port 5080; Vite remains available at 5173 for development.

## Verification

Backend/frontend builds pass. Fourteen focused lifecycle/validation/encryption/process-execution tests pass. Two browser tests against the built frontend passed, including narrow-viewport layout and the workflow through login, project creation, secret editor, actual deployment, logs, routed response, stop, restart, deletion, and logout. EF migrations apply and the model has no pending changes. Anonymous API access is rejected, unsafe URLs are rejected, missing Dockerfile fails without replacing the active route, and encrypted variables reach Docker without appearing in persisted deployment logs. See MVP_VERIFICATION.md.

## Known limitations / remaining hardening

- Rollback verification reused the original image and switched the real upstream, but the tested upstream repository version was the same commit; a changed-content two-version test remains.
- Crash-time nginx/database reconciliation and stronger execution fencing/concurrent lifecycle locking are not complete.
- Application monitoring/log collection shares the serial deployment worker and pauses during builds.
- Source/image/log retention and cleanup automation are absent; deleting a project removes its containers, route, and DB history but retains images/source directories.
- Single trusted operator only; no custom domains/TLS, private Git credentials, hostile-tenant build isolation, production service packaging, or metrics/exporter stack.
- Git hostname checks do not fully address DNS rebinding/private-address resolution; restrict worker egress for untrusted inputs.

The main workflow is demonstrable. The complete system prompt's hardened definition of done is not claimed.

## Compose support

Compose projects select a repository-relative file and routed service. The worker builds local service contexts, retains encrypted manifests/immutable images, starts dependent services, checks HTTP/service health, and records service-prefixed logs/status. Stop/restart/delete and rollback share the owned stack lifecycle; named volumes survive deployment changes and project deletion. The dashboard configures Compose mode and shows each service.

Thirty unit tests and one opt-in real Docker integration test pass. The integration test proves visibly different-version rollback and Redis persistence. A public WordPress/MariaDB stack also deployed through the API and serves its UI through nginx. See COMPOSE.md and MVP_VERIFICATION.md for supported features and limitations.
