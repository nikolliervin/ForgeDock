# Development

Prerequisites: Linux, .NET 10 SDK, Node 22.12+ and npm, Git, Docker Engine, OpenSSL, Bash, and Make. Start Docker (`sudo systemctl start docker` on Fedora) and ensure your user can access it. Docker access grants extensive host privileges.

```bash
make init
make infra
make migrate
npm --prefix web ci
```

`make init` generates a private `.env` with local credentials; it preserves an existing file. Keep the connection string quoted because it contains semicolons. Environment files are sourced as trusted shell configuration by the helper scripts. Do not source untrusted environment files.

In three separate terminals run `make api`, `make worker`, and `make web`. Open http://127.0.0.1:5173. Read `ForgeDock__ApiToken` from `.env` locally to sign in; do not share it. API port is 5080, application proxy port is 8088, PostgreSQL port is 5432. All host ports bind to loopback. The scripts use Docker CLI and do not require Compose.

For a public representative demo, create a project with repository `https://github.com/docker/welcome-to-docker.git`, branch `main`, Dockerfile `Dockerfile`, port `3000`, and health path `/`. Deploy and open the application link after it reaches Running. The repository is third-party content and may change. The local fixture in `examples/demo` uses port 8080 and `/health`; publish it to a public HTTPS Git repository to exercise the complete Git workflow.

```bash
make test
make build
# Generate a migration after schema changes:
bash scripts/with-env.sh dotnet ef migrations add ChangeName --project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api
# Check schema drift:
bash scripts/with-env.sh dotnet ef migrations has-pending-model-changes --project src/ForgeDock.Infrastructure --startup-project src/ForgeDock.Api
```

Do not commit `.env`, `.runtime`, private AI prompts, or generated build output. PostgreSQL data lives in the owned `forgedock-postgres-data` volume. Application images/containers are intentionally retained. Stop platform processes with Ctrl+C; stop infrastructure explicitly with `docker stop forgedock-proxy forgedock-postgres`. Do not remove the database volume unless intentionally deleting all history.

Browser verification uses Playwright. Install its browser with `npm exec --prefix web -- playwright install chromium`, then run `make e2e` with the platform running. To verify built frontend hosting, use `FORGEDOCK_TEST_URL=http://127.0.0.1:5080 make e2e`. The workflow creates a demo project, deploys it, stops/restarts it, and deletes it; it requires network access to the public demo repository and registry. The narrow-viewport check expects the retained `Docker welcome demo` project from initial acceptance setup. Screenshots are private runtime artifacts.

For Compose applications run `make compose` to install the checksum-verified official executable into the private runtime directory. `make docker-test` exercises real service builds, forced restart, changed-version rollback, persistent data, and secret file mounts. The WordPress browser check expects the retained `Compose WordPress demo` project.
