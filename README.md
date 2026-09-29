# ForgeDock

A single-host deployment platform for public HTTPS Git repositories with Dockerfiles. The .NET API stores projects and queued deployments in PostgreSQL; a Linux worker builds and starts Docker containers and switches nginx routes after HTTP health checks. React provides the management dashboard.

```bash
make init
make infra
make migrate
npm --prefix web ci
# In separate terminals:
make api
make worker
make web
```

Open http://127.0.0.1:5173 and sign in with `ForgeDock__ApiToken` from your private `.env`. Application routes use `http://<project-id-without-hyphens>.localhost:8088`.

Start with [development setup](docs/DEVELOPMENT.md). Read [architecture](docs/ARCHITECTURE.md), [security](docs/SECURITY.md), [implementation status](docs/IMPLEMENTATION_STATUS.md), and [verification results](docs/MVP_VERIFICATION.md) before deploying publicly.
