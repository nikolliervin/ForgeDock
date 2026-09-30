# ForgeDock

A single-host deployment platform for public HTTPS Git repositories with Dockerfiles or Docker Compose stacks. The .NET API stores projects and queued deployments in PostgreSQL; a Linux worker builds and starts Docker containers and switches nginx routes after HTTP health checks. React provides the management dashboard.

Run the local app with one command (requires the prerequisites in [development setup](docs/DEVELOPMENT.md)):

```bash
./scripts/start-local.sh
```

The script prepares the environment, starts PostgreSQL and nginx, applies migrations, installs frontend dependencies, builds the backend, and starts the API, worker, and dashboard. Press Ctrl+C to stop the app services; PostgreSQL and nginx remain running. An existing `.env` is preserved.

To run each step manually:

```bash
make init
make infra
make compose # for Compose application deployments
make migrate
npm --prefix web ci
# In separate terminals:
make api
make worker
make web
```

Open http://127.0.0.1:5173 and sign in with `ForgeDock__ApiToken` from your private `.env`. Application routes use `http://<project-id-without-hyphens>.localhost:8088`.

Start with [development setup](docs/DEVELOPMENT.md). Read [architecture](docs/ARCHITECTURE.md), [security](docs/SECURITY.md), [implementation status](docs/IMPLEMENTATION_STATUS.md), and [verification results](docs/MVP_VERIFICATION.md) before deploying publicly.

For multiple services, choose Docker Compose, set the repository-relative Compose file, routed service, and its internal port. See [Compose support](docs/COMPOSE.md).
