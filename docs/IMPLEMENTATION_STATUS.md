# Implementation status

## Implemented

- .NET 10 domain lifecycle and configuration validation.
- PostgreSQL EF migrations, projects, deployment snapshots/history, and durable logs.
- Bearer-authenticated REST API with project configuration, deploy, rollback, environment variables, health endpoints, and OpenAPI.
- AES-GCM environment storage and temporary mode-0600 runtime env files.
- Serial worker with PostgreSQL ownership lock, interrupted-deployment handling, Git clone, Docker build/start, HTTP health checks, nginx route generation/reload, and retained-image rollback.
- Recurring health checks and bounded container-log collection.
- React dashboard connected to the API, project settings/environment editor, deployment progress/history/logs, and rollback confirmation.
- Startup automation and a Dockerfile demo fixture.

## Verification

Backend and frontend builds pass. Lifecycle/validation/encryption tests pass. PostgreSQL/nginx start and EF migrations apply in Fedora. First real public-repository deployment is under verification. See MVP_VERIFICATION.md for current acceptance results.

## Remaining acceptance work

Prove real routed deployment, redeploy, rollback, variable handling, and restart persistence. Add workflow/API failure tests, production frontend packaging, stop/delete lifecycle controls, stronger SSRF defenses, crash-time reconciliation, cleanup/retention, and browser interaction verification. The full prompt's definition of done has not yet been met.
