# Implementation status

## Demonstrable vertical slice

Real authenticated UI/API project creation → public Git clone → Docker image build → constrained container start → HTTP health probe → nginx route switch → HTTP 200 application response → persisted logs/history → redeploy → retained-image rollback has been executed on Fedora. Restarted API/worker processes preserve history and application routing.

The dashboard includes settings, encrypted write-only environment editing, deployment history/progress/logs, live health status, restart, confirmed stop, and confirmed project deletion. Built assets are served by the API at port 5080; Vite remains available at 5173 for development.

## Verification

Backend/frontend builds pass. Thirty-two focused lifecycle/validation/encryption/process-execution tests pass. Four browser tests against the built frontend passed, including narrow-viewport layout and the workflow through login, project creation, secret editor, actual deployment, logs, routed response, stop, restart, deletion, and logout. EF migrations apply and the model has no pending changes. Anonymous API access is rejected, unsafe URLs are rejected, missing Dockerfile fails without replacing the active route, and encrypted variables reach Docker without appearing in persisted deployment logs. See MVP_VERIFICATION.md.

## Known limitations / remaining hardening

- Crash-time nginx/database reconciliation and stronger execution fencing/concurrent lifecycle locking are not complete.
- Application monitoring/log collection shares the serial deployment worker and pauses during builds.
- Source/image/log retention and cleanup automation are absent; deleting a project removes its containers, route, and DB history but retains images/source directories.
- Single trusted operator only; private GitHub access uses a shared worker token; no hostile-tenant build isolation, production service packaging, or metrics/exporter stack.
- Git hostname checks do not fully address DNS rebinding/private-address resolution; restrict worker egress for untrusted inputs.

The main workflow is demonstrable. The complete system prompt's hardened definition of done is not claimed.

## Compose support

Compose projects select a repository-relative file and routed service. The worker builds local service contexts, retains encrypted manifests/immutable images, starts dependent services, checks HTTP/service health, and records service-prefixed logs/status. Stop/restart/delete and rollback share the owned stack lifecycle; named volumes survive deployment changes and project deletion. The dashboard configures Compose mode and shows each service.

Thirty-two unit tests and one opt-in real Docker integration test pass. The integration test proves visibly different-version rollback and Redis persistence. A public WordPress/MariaDB stack also deployed through the API and serves its UI through nginx. See COMPOSE.md and MVP_VERIFICATION.md for supported features and limitations.

## Automatic builds and built-in documentation

Auto mode now uses a configured Dockerfile when present and otherwise builds with Railpack. Optional build/start command overrides are persisted and snapshotted. The worker supplies a default runtime PORT, isolates build process credentials, and reuses the existing health-check, routing, and retained-image rollback flow. The startup script installs the pinned builder and starts BuildKit.

Public React documentation is available at `/docs`, including direct links from built API hosting, search, copyable code examples, mobile navigation, and guides for supported deployment workflows. Its dark theme matches the dashboard, and Documentation is separate from the project list. See [verification results](AUTO_AND_DOCS_VERIFICATION.md) for the backend, browser, launcher, and real Auto deployment checks.

## Custom domains and automatic HTTPS

The Domains tab supports hostname creation, DNS ownership records, verification requests, certificate status, and queued removal. Verified names follow project deployment routes. An opt-in Caddy edge issues and renews certificates, redirects HTTP to HTTPS, and preserves forwarded hostname/scheme headers. Public hosting remains disabled by default.

Current checks: 55 backend tests and 10 documentation/navigation/domain browser tests pass; backend and production frontend builds pass. Local Pebble ACME issuance, certificate hostname/expiry, HTTPS forwarding, redirects, and unknown-host rejection were exercised. Public Let's Encrypt issuance requires an operator-owned hostname and publicly reachable server and has not been exercised. See [domain setup](CUSTOM_DOMAINS.md) and [verification](CUSTOM_DOMAINS_VERIFICATION.md).

## Application metrics and bulk environment imports

The Metrics tab shows CPU, memory, received/sent traffic rates, and 24-hour usage history, plus per-service uptime, limits, processes, disk/network totals, and Compose service filtering. Collection runs separately from deployments, stores samples in PostgreSQL, and cleans up expired history. Freshness and missing data are explicit. The Environment tab supports atomic encrypted .env imports with protected replacement of existing variables. See [metrics](METRICS.md) and [verification](METRICS_VERIFICATION.md).
