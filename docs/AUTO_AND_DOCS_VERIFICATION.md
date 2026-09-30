# Auto builds and React documentation verification

Verified locally on 2026-09-30.

## Application checks

- Backend build succeeds without warnings or errors.
- Backend tests: 37 passed, one opt-in Compose Docker test skipped.
- EF reports no pending model changes; the AutomaticBuildCommands migration is applied to the local database.
- Frontend production build and TypeScript check pass.
- `./scripts/start-local.sh` starts the API, worker, and frontend with an existing `.env`. API readiness and `/docs` respond successfully. Sending Ctrl+C exits with status 130 and stops the API/frontend processes. Infrastructure remains running.

## Automatic builds

- The pinned Railpack 0.40.1 installer verifies the archive checksum and starts BuildKit 0.33.0.
- The Dockerfile-free local Node fixture builds into a Docker image and responds over HTTP under the worker's container resource limits.
- A temporary project using the public `railwayapp-templates/expressjs` repository passes the complete Git/API/worker flow in Auto mode with `npm run build` and `npm start` overrides.
- The worker supplies runtime `PORT=3456`, the application passes HTTP health checks, and nginx serves its expected JSON response.
- Retained-image rollback reaches Running and serves the same response without cloning or rebuilding.
- The verification project and its containers are deleted after the check. Retained images and sources follow the normal retention behavior.

The initial integration attempt found nginx could not start with retained static routes to missing containers. Startup now backs up and converts those routes to request-time Docker DNS resolution, validates nginx, and reloads it. New single-application and Compose routes use the same resolution behavior. The integration check passed after that fix.

## Documentation and navigation

- Six docs browser checks pass through both Vite and the API-hosted production frontend: public direct routes, search/shortcut/copy/history, dashboard state preservation, mobile navigation, unknown-page recovery, and desktop layout.
- The final seven-check Vite suite also verifies documentation is separate from project navigation and the SVG logo aligns with the wordmark.
- Desktop and mobile screenshots were reviewed. Documentation matches the dashboard's dark surfaces, blue accents, and borders. The table of contents tracks the visible section.
- Screenshots and startup logs are private artifacts under `.runtime`; they are not committed.

## Scope

Saved variables remain runtime-only for single-application builds. Private repositories, build-time secrets, custom Auto build root directories, public domain/TLS management, and Git push webhooks are not included. Existing projects and old snapshots preserve their original deployment mode.
